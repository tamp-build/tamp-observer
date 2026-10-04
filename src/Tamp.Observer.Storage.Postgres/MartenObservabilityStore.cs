using Marten;
using Npgsql;
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
public sealed class MartenObservabilityStore(IDocumentStore store, string connectionString) : IObservabilityStore
{
    private readonly IDocumentStore _store = store;
    private readonly string _connectionString = connectionString;

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
        // Served from the operation rollup (TOBS-25): sum calls/errors per operation over the window.
        var sql = "SELECT operation, SUM(count), SUM(error_count) FROM observer.observer_rollup_operation "
            + "WHERE project_id = @p AND bucket_start >= @s AND bucket_start < @e"
            + (query.ServiceId is not null ? " AND service_id = @svc" : "")
            + " GROUP BY operation ORDER BY SUM(count) DESC, operation LIMIT @lim";
        var result = new List<OperationStat>();
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            AddScope(cmd, query);
            cmd.Parameters.AddWithValue("lim", limit <= 0 ? 10 : limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new OperationStat(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") { }
        return result;
    }

    public async Task<IReadOnlyList<OperationSeries>> GetOperationSeriesAsync(SpanQuery query, int buckets, int limit = 10, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        var width = BucketWidth(query.Window, buckets);
        limit = limit <= 0 ? 10 : limit;

        // Per (operation, rollup bucket) rows, folded into the requested output buckets; top operations by total calls.
        var sql = "SELECT operation, bucket_start, count, error_count, lat_hist FROM observer.observer_rollup_operation "
            + "WHERE project_id = @p AND bucket_start >= @s AND bucket_start < @e"
            + (query.ServiceId is not null ? " AND service_id = @svc" : "");
        var byOp = new Dictionary<string, (long[] Counts, long[] Errors, long[][] Lat, long Total)>(StringComparer.Ordinal);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            AddScope(cmd, query);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var op = reader.GetString(0);
                var b = BucketIndex(reader.GetInt64(1), query.Window.StartUnixNano, width, buckets);
                var count = reader.GetInt64(2);
                if (!byOp.TryGetValue(op, out var agg))
                {
                    agg = (new long[buckets], new long[buckets], new long[buckets][], 0);
                    for (var i = 0; i < buckets; i++) agg.Lat[i] = LatencyHistogram.Empty();
                    byOp[op] = agg;
                }
                agg.Counts[b] += count;
                agg.Errors[b] += reader.GetInt64(3);
                LatencyHistogram.AddInto(agg.Lat[b], reader.GetFieldValue<long[]>(4));
                byOp[op] = agg with { Total = agg.Total + count };
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") { }

        return byOp
            .OrderByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(kv =>
            {
                var all = LatencyHistogram.Empty();
                var series = new List<SeriesBucket>(buckets);
                long total = 0, errs = 0;
                for (var i = 0; i < buckets; i++)
                {
                    LatencyHistogram.AddInto(all, kv.Value.Lat[i]);
                    total += kv.Value.Counts[i];
                    errs += kv.Value.Errors[i];
                    series.Add(new SeriesBucket(
                        query.Window.StartUnixNano + (long)i * width, kv.Value.Counts[i], kv.Value.Errors[i],
                        (long)LatencyHistogram.Percentile(kv.Value.Lat[i], 0.95)));
                }
                return new OperationSeries(kv.Key, total, errs, (long)LatencyHistogram.Percentile(all, 0.95), series);
            })
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
        if (query.EnvironmentId is Guid envId)
            q = q.Where(l => l.EnvironmentId == envId);
        if (query.VersionId is Guid verId)
            q = q.Where(l => l.VersionId == verId);
        if (query.MinSeverityNumber is int min)
            q = q.Where(l => l.SeverityNumber >= min);
        if (query.TraceId is { Length: > 0 } traceId)
            q = q.Where(l => l.TraceId == traceId);
        if (query.Category is { Length: > 0 } category)
            q = q.Where(l => l.Attributes["log.category"] == category);
        if (query.SessionId is { Length: > 0 } sessionId)
            q = q.Where(l => l.Attributes["tamp.session.id"] == sessionId);
        if (query.Search is { Length: > 0 } search)
        {
            // Case-insensitive substring over the body (Marten -> lower(body) LIKE '%search%').
            var lowered = search.ToLowerInvariant();
            q = q.Where(l => l.Body != null && l.Body.ToLower().Contains(lowered));
        }
        if (query.BeforeUnixNano is long before)
            q = q.Where(l => l.TimeUnixNano < before);
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

    public async Task<IReadOnlyList<SeriesBucket>> GetSpanSeriesAsync(SpanQuery query, int buckets, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        var width = BucketWidth(query.Window, buckets);
        var counts = new long[buckets];
        var errors = new long[buckets];
        var bytes = new long[buckets];
        var lat = new long[buckets][];
        for (var i = 0; i < buckets; i++) lat[i] = LatencyHistogram.Empty();

        // Served from the materialized rollup (TOBS-25): fold the dense 60s rollup buckets into the requested
        // output buckets and derive p95 per output bucket from the summed latency histogram.
        var sql = "SELECT bucket_start, event_count, error_count, bytes, lat_hist FROM observer.observer_rollup_signal "
            + "WHERE project_id = @p AND signal = 'span' AND bucket_start >= @s AND bucket_start < @e"
            + (query.ServiceId is not null ? " AND service_id = @svc" : "");
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", query.ProjectId);
            cmd.Parameters.AddWithValue("s", query.Window.StartUnixNano);
            cmd.Parameters.AddWithValue("e", query.Window.EndUnixNano);
            if (query.ServiceId is Guid svc) cmd.Parameters.AddWithValue("svc", svc);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var bucketStart = reader.GetInt64(0);
                var b = BucketIndex(bucketStart, query.Window.StartUnixNano, width, buckets);
                counts[b] += reader.GetInt64(1);
                errors[b] += reader.GetInt64(2);
                bytes[b] += reader.GetInt64(3);
                LatencyHistogram.AddInto(lat[b], reader.GetFieldValue<long[]>(4));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Rollup table not created yet (fresh install before the evaluator's first run): empty series.
        }

        var list = new List<SeriesBucket>(buckets);
        for (var i = 0; i < buckets; i++)
            list.Add(new SeriesBucket(
                query.Window.StartUnixNano + (long)i * width, counts[i], errors[i],
                (long)LatencyHistogram.Percentile(lat[i], 0.95), bytes[i]));
        return list;
    }

    public async Task<IReadOnlyList<IssueSeries>> GetIssueSeriesAsync(Guid projectId, TimeWindow window, int buckets, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        var width = BucketWidth(window, buckets);
        var byFp = new Dictionary<string, long[]>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await using var conn = await OpenAsync(ct);
            // Per-fingerprint occurrence buckets from the issue rollup.
            await using (var cmd = new NpgsqlCommand(
                "SELECT fingerprint, bucket_start, count FROM observer.observer_rollup_issue "
                + "WHERE project_id = @p AND bucket_start >= @s AND bucket_start < @e", conn))
            {
                cmd.Parameters.AddWithValue("p", projectId);
                cmd.Parameters.AddWithValue("s", window.StartUnixNano);
                cmd.Parameters.AddWithValue("e", window.EndUnixNano);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var fp = reader.GetString(0);
                    if (!byFp.TryGetValue(fp, out var arr)) byFp[fp] = arr = new long[buckets];
                    arr[BucketIndex(reader.GetInt64(1), window.StartUnixNano, width, buckets)] += reader.GetInt64(2);
                }
            }
            // Distinct sessions per fingerprint from the session dedup set (by last-seen in the window).
            await using (var cmd = new NpgsqlCommand(
                "SELECT fingerprint, COUNT(DISTINCT session_id) FROM observer.observer_rollup_issue_session "
                + "WHERE project_id = @p AND last_seen_nano >= @s AND last_seen_nano < @e GROUP BY fingerprint", conn))
            {
                cmd.Parameters.AddWithValue("p", projectId);
                cmd.Parameters.AddWithValue("s", window.StartUnixNano);
                cmd.Parameters.AddWithValue("e", window.EndUnixNano);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    sessions[reader.GetString(0)] = (int)reader.GetInt64(1);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") { }

        return byFp.Select(kv => new IssueSeries(kv.Key, kv.Value, sessions.GetValueOrDefault(kv.Key))).ToList();
    }

    public async Task<ExceptionDetail?> GetLatestExceptionAsync(Guid projectId, string fingerprint, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var span = await session.Query<IngestedSpan>()
            .Where(s => s.ProjectId == projectId && s.Fingerprint == fingerprint)
            .OrderByDescending(s => s.StartUnixNano).FirstOrDefaultAsync(ct);
        var log = await session.Query<IngestedLog>()
            .Where(l => l.ProjectId == projectId && l.Fingerprint == fingerprint)
            .OrderByDescending(l => l.TimeUnixNano).FirstOrDefaultAsync(ct);

        var spanAt = span?.StartUnixNano ?? -1;
        var logAt = log?.TimeUnixNano ?? -1;
        var attrs = spanAt >= logAt ? span?.Attributes : log?.Attributes;
        if (attrs is null) return null;
        return new ExceptionDetail(
            attrs.GetValueOrDefault("exception.type"),
            attrs.GetValueOrDefault("exception.message"),
            attrs.GetValueOrDefault("exception.stacktrace"));
    }

    public async Task<IReadOnlyList<MetricLatest>> GetLatestMetricsAsync(Guid projectId, TimeWindow window, CancellationToken ct = default)
    {
        await using var session = _store.QuerySession();
        var points = await session.Query<IngestedMetric>()
            .Where(m => m.ProjectId == projectId
                && m.TimeUnixNano >= window.StartUnixNano && m.TimeUnixNano < window.EndUnixNano)
            .ToListAsync(ct);
        return points
            .GroupBy(m => (m.Name, m.ServiceId))
            .Select(g => g.OrderByDescending(m => m.TimeUnixNano).First())
            .Select(m => new MetricLatest(m.Name, m.ServiceId, m.Value, m.TimeUnixNano))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<MetricBucket>> GetMetricSeriesAsync(Guid projectId, string name, TimeWindow window, int buckets, Guid? serviceId = null, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        await using var session = _store.QuerySession();
        var q = session.Query<IngestedMetric>()
            .Where(m => m.ProjectId == projectId && m.Name == name
                && m.TimeUnixNano >= window.StartUnixNano && m.TimeUnixNano < window.EndUnixNano);
        if (serviceId is Guid svc) q = q.Where(m => m.ServiceId == svc);
        var points = await q.ToListAsync(ct);

        var width = BucketWidth(window, buckets);
        var last = new double?[buckets];
        var lastAt = new long[buckets];
        foreach (var m in points)
        {
            var b = BucketIndex(m.TimeUnixNano, window.StartUnixNano, width, buckets);
            if (last[b] is null || m.TimeUnixNano >= lastAt[b]) { last[b] = m.Value; lastAt[b] = m.TimeUnixNano; }
        }
        var series = new List<MetricBucket>(buckets);
        for (var i = 0; i < buckets; i++)
            series.Add(new MetricBucket(window.StartUnixNano + (long)i * width, last[i] ?? 0));
        return series;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // Bind the common rollup query scope: project, window, and optional service filter.
    private static void AddScope(NpgsqlCommand cmd, SpanQuery q)
    {
        cmd.Parameters.AddWithValue("p", q.ProjectId);
        cmd.Parameters.AddWithValue("s", q.Window.StartUnixNano);
        cmd.Parameters.AddWithValue("e", q.Window.EndUnixNano);
        if (q.ServiceId is Guid svc) cmd.Parameters.AddWithValue("svc", svc);
    }

    internal static long BucketWidth(TimeWindow w, int buckets) => Math.Max(1, (w.EndUnixNano - w.StartUnixNano) / buckets);
    internal static int BucketIndex(long t, long start, long width, int buckets) =>
        (int)Math.Clamp((t - start) / width, 0, buckets - 1);

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
