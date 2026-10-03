using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// The Postgres/Marten translator for <see cref="IObservabilityStore"/> (ADR 0006). This is the
/// baseline provider. Point and recent-slice reads translate cleanly to Marten LINQ; the analytical
/// percentile/top-N reductions are computed here for the baseline tier. A columnar provider
/// (DuckDB/ClickHouse) would instead push these down as native <c>percentile_cont</c> / <c>quantile</c>
/// and <c>GROUP BY</c>, which is exactly why the interface is capability-based intent, not shared SQL.
/// </summary>
public sealed class MartenObservabilityStore(IDocumentStore store) : IObservabilityStore
{
    private readonly IDocumentStore _store = store;

    public async Task<LatencyPercentiles> GetLatencyPercentilesAsync(SpanQuery query, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var durations = (await WindowedSpans(session, query).ToListAsync(ct))
            .Select(s => s.DurationNano)
            .OrderBy(d => d)
            .ToArray();

        if (durations.Length == 0)
            return new LatencyPercentiles(0, 0, 0, 0);

        return new LatencyPercentiles(
            durations.Length,
            Percentile(durations, 50),
            Percentile(durations, 95),
            Percentile(durations, 99));
    }

    public async Task<IReadOnlyList<OperationStat>> GetTopOperationsAsync(SpanQuery query, int limit = 10, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var spans = await WindowedSpans(session, query).ToListAsync(ct);

        return spans
            .GroupBy(s => s.Name)
            .Select(g => new OperationStat(g.Key, g.LongCount(), g.LongCount(s => s.StatusCode == 2)))
            .OrderByDescending(o => o.Count)
            .ThenBy(o => o.Operation, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    public async Task<TraceView> GetTraceAsync(Guid projectId, string traceId, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var spans = await session.Query<IngestedSpan>()
            .Where(s => s.ProjectId == projectId && s.TraceId == traceId)
            .ToListAsync(ct);
        var logs = await session.Query<IngestedLog>()
            .Where(l => l.ProjectId == projectId && l.TraceId == traceId)
            .ToListAsync(ct);
        return new TraceView(spans, logs);
    }

    public async Task<IReadOnlyList<IngestedLog>> GetLogsAsync(LogQuery query, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var q = session.Query<IngestedLog>()
            .Where(l => l.ProjectId == query.ProjectId
                && l.TimeUnixNano >= query.Window.StartUnixNano
                && l.TimeUnixNano < query.Window.EndUnixNano);
        if (query.ServiceId is Guid serviceId)
            q = q.Where(l => l.ServiceId == serviceId);
        if (query.MinSeverityNumber is int min)
            q = q.Where(l => l.SeverityNumber >= min);
        return await q.OrderByDescending(l => l.TimeUnixNano).Take(query.Limit <= 0 ? 200 : query.Limit).ToListAsync(ct);
    }

    public async Task<IssueOccurrence?> GetLatestOccurrenceAsync(Guid projectId, string fingerprint, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var span = await session.Query<IngestedSpan>()
            .Where(s => s.ProjectId == projectId && s.Fingerprint == fingerprint)
            .OrderByDescending(s => s.StartUnixNano)
            .FirstOrDefaultAsync(ct);
        var log = await session.Query<IngestedLog>()
            .Where(l => l.ProjectId == projectId && l.Fingerprint == fingerprint)
            .OrderByDescending(l => l.TimeUnixNano)
            .FirstOrDefaultAsync(ct);

        var spanOcc = span is null ? null : FromSpan(span);
        var logOcc = log is null ? null : FromLog(log);

        if (spanOcc is null) return logOcc;
        if (logOcc is null) return spanOcc;
        return spanOcc.AtUnixNano >= logOcc.AtUnixNano ? spanOcc : logOcc;
    }

    public async Task<IssueOccurrence?> GetLatestOccurrenceBySessionAsync(Guid projectId, string sessionId, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var span = await session.Query<IngestedSpan>()
            .Where(s => s.ProjectId == projectId && s.Fingerprint != null && s.Attributes["tamp.session.id"] == sessionId)
            .OrderByDescending(s => s.StartUnixNano)
            .FirstOrDefaultAsync(ct);
        var log = await session.Query<IngestedLog>()
            .Where(l => l.ProjectId == projectId && l.Fingerprint != null && l.Attributes["tamp.session.id"] == sessionId)
            .OrderByDescending(l => l.TimeUnixNano)
            .FirstOrDefaultAsync(ct);

        var spanOcc = span is null ? null : FromSpan(span);
        var logOcc = log is null ? null : FromLog(log);
        if (spanOcc is null) return logOcc;
        if (logOcc is null) return spanOcc;
        return spanOcc.AtUnixNano >= logOcc.AtUnixNano ? spanOcc : logOcc;
    }

    public async Task<IssueOccurrence?> GetLatestOccurrenceByTraceAsync(Guid projectId, string traceId, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var span = await session.Query<IngestedSpan>()
            .Where(s => s.ProjectId == projectId && s.Fingerprint != null && s.TraceId == traceId)
            .OrderByDescending(s => s.StartUnixNano)
            .FirstOrDefaultAsync(ct);
        var log = await session.Query<IngestedLog>()
            .Where(l => l.ProjectId == projectId && l.Fingerprint != null && l.TraceId == traceId)
            .OrderByDescending(l => l.TimeUnixNano)
            .FirstOrDefaultAsync(ct);

        var spanOcc = span is null ? null : FromSpan(span);
        var logOcc = log is null ? null : FromLog(log);
        if (spanOcc is null) return logOcc;
        if (logOcc is null) return spanOcc;
        return spanOcc.AtUnixNano >= logOcc.AtUnixNano ? spanOcc : logOcc;
    }

    private static IssueOccurrence FromSpan(IngestedSpan s) => new(
        "span", s.StartUnixNano, s.TraceId, s.SpanId, Session(s.Attributes), s.ServiceId, s.Fingerprint);

    private static IssueOccurrence FromLog(IngestedLog l) => new(
        "log", l.TimeUnixNano, l.TraceId, l.SpanId, Session(l.Attributes), l.ServiceId, l.Fingerprint);

    // The browser-minted correlation key, when present on the occurrence.
    private static string? Session(IReadOnlyDictionary<string, string> attrs) =>
        attrs.TryGetValue("tamp.session.id", out var v) ? v : null;

    private static IQueryable<IngestedSpan> WindowedSpans(IQuerySession session, SpanQuery query)
    {
        var q = session.Query<IngestedSpan>()
            .Where(s => s.ProjectId == query.ProjectId
                && s.StartUnixNano >= query.Window.StartUnixNano
                && s.StartUnixNano < query.Window.EndUnixNano);
        if (query.ServiceId is Guid serviceId)
            q = q.Where(s => s.ServiceId == serviceId);
        return q;
    }

    /// <summary>Nearest-rank percentile over an ascending-sorted array.</summary>
    private static double Percentile(long[] sortedAscending, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Length);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Length - 1);
        return sortedAscending[index];
    }
}
