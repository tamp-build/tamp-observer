using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;
using Tamp.Observer.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.Postgres.Tests;

/// <summary>
/// Proves the capability-based read interface (ADR 0006): latency percentiles, top operations, and the
/// trace correlation walk, translated by the Postgres/Marten provider. Each test scopes to a unique
/// ProjectId for isolation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReadInterfaceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private IDocumentStore _store = null!;
    private MartenObservabilityStore _reads = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var conn = _postgres.GetConnectionString();
        await RollupSchema.EnsureAsync(conn);
        _store = ObserverStore.For(conn);
        _reads = new MartenObservabilityStore(_store, conn);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Latency_percentiles_over_the_window()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        await StoreSpans(project, service, trace: "t",
            (name: "op", start: 1000, duration: 10, status: 0),
            ("op", 1100, 20, 0),
            ("op", 1200, 30, 0),
            ("op", 1300, 40, 0),
            ("op", 1400, 50, 0));

        var p = await _reads.GetLatencyPercentilesAsync(new SpanQuery(project, new TimeWindow(1000, 2000)));

        Assert.Equal(5, p.Count);
        Assert.Equal(30, p.P50);
        Assert.Equal(50, p.P95);
        Assert.Equal(50, p.P99);
    }

    [Fact]
    public async Task Window_and_service_filters_apply()
    {
        var project = Guid.NewGuid();
        var svcA = Guid.NewGuid();
        var svcB = Guid.NewGuid();
        await StoreSpans(project, svcA, "t", ("a", 1000, 10, 0), ("a", 1500, 20, 0));
        await StoreSpans(project, svcB, "t", ("b", 1200, 99, 0));
        await StoreSpans(project, svcA, "t", ("a", 5000, 77, 0)); // outside window

        var p = await _reads.GetLatencyPercentilesAsync(
            new SpanQuery(project, new TimeWindow(1000, 2000), ServiceId: svcA));

        Assert.Equal(2, p.Count); // only svcA, only in-window
    }

    [Fact]
    public async Task Top_operations_by_frequency_with_error_counts()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        await StoreSpans(project, service, "t",
            ("op1", 1000, 10, 0),
            ("op1", 1010, 10, 0),
            ("op1", 1020, 10, 0),
            ("op2", 1030, 10, 2),   // error
            ("op2", 1040, 10, 0));

        var top = await _reads.GetTopOperationsAsync(new SpanQuery(project, new TimeWindow(1000, 2000)));

        Assert.Equal(2, top.Count);
        Assert.Equal(new OperationStat("op1", 3, 0), top[0]);
        Assert.Equal(new OperationStat("op2", 2, 1), top[1]);
    }

    [Fact]
    public async Task Trace_view_returns_spans_and_logs_for_the_trace()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        await StoreSpans(project, service, "trace-1", ("root", 1000, 10, 0), ("child", 1005, 5, 0));
        await StoreSpans(project, service, "trace-2", ("other", 1000, 10, 0));
        await StoreLog(project, service, "trace-1", "boom");

        var view = await _reads.GetTraceAsync(project, "trace-1");

        Assert.Equal(2, view.Spans.Count);
        Assert.Single(view.Logs);
        Assert.Equal("boom", view.Logs[0].Body);
    }

    [Fact]
    public async Task Log_query_filters_narrow_the_result_set()
    {
        var project = Guid.NewGuid();
        var svcA = Guid.NewGuid();
        var svcB = Guid.NewGuid();
        // (time, severity, category, body, session, service)
        await StoreRichLog(project, svcA, 1000, 17, "db.query", "Timeout connecting to primary", "sess-1");
        await StoreRichLog(project, svcA, 2000, 9, "db.query", "slow query WARN", "sess-1");       // below min severity
        await StoreRichLog(project, svcB, 3000, 17, "app.auth", "login TIMEOUT for user", "sess-2");
        await StoreRichLog(project, svcA, 4000, 17, "db.query", "another timeout here", "sess-1");
        await StoreRichLog(project, svcA, 9000, 17, "db.query", "outside window", "sess-1");        // outside window

        var window = new TimeWindow(0, 5000);

        // Category filter.
        var byCategory = await _reads.GetLogsAsync(new LogQuery(project, window, Category: "db.query"));
        Assert.All(byCategory, l => Assert.Equal("db.query", l.Attributes["log.category"]));
        Assert.Equal(3, byCategory.Count); // the three in-window db.query rows (incl. the WARN)

        // Case-insensitive body search.
        var bySearch = await _reads.GetLogsAsync(new LogQuery(project, window, Search: "timeout"));
        Assert.Equal(3, bySearch.Count); // "Timeout", "TIMEOUT", "timeout" all match

        // Min severity + category together.
        var errorsOnly = await _reads.GetLogsAsync(
            new LogQuery(project, window, MinSeverityNumber: 17, Category: "db.query"));
        Assert.Equal(2, errorsOnly.Count); // drops the severity-9 WARN row

        // Session correlation key.
        var bySession = await _reads.GetLogsAsync(new LogQuery(project, window, SessionId: "sess-2"));
        Assert.Single(bySession);
        Assert.Equal(svcB, bySession[0].ServiceId);

        // Keyset "load older" paging: only rows strictly older than the cursor, newest first.
        var older = await _reads.GetLogsAsync(new LogQuery(project, window, BeforeUnixNano: 4000));
        Assert.Equal(3, older.Count);
        Assert.True(older[0].TimeUnixNano < 4000);
        Assert.Equal(3000, older[0].TimeUnixNano); // newest-first among those older than 4000
    }

    [Fact]
    public async Task Log_tail_returns_strictly_newer_rows_oldest_first()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        await StoreRichLog(project, service, 1000, 17, "db.query", "first", "s");
        await StoreRichLog(project, service, 2000, 17, "db.query", "second", "s");
        await StoreRichLog(project, service, 3000, 17, "db.query", "third", "s");

        // Tail from a cursor at 1000: only strictly-newer rows, oldest-first for appending.
        var tail = await _reads.GetLogsAsync(new LogQuery(
            project, new TimeWindow(1000, long.MaxValue), AfterUnixNano: 1000, Ascending: true));

        Assert.Equal(2, tail.Count);
        Assert.Equal(2000, tail[0].TimeUnixNano); // oldest-first
        Assert.Equal(3000, tail[1].TimeUnixNano);

        // Advancing the cursor to the newest seen returns nothing until more arrives.
        var caughtUp = await _reads.GetLogsAsync(new LogQuery(
            project, new TimeWindow(3000, long.MaxValue), AfterUnixNano: 3000, Ascending: true));
        Assert.Empty(caughtUp);
    }

    private async Task StoreRichLog(
        Guid project, Guid service, long time, int severity, string category, string body, string session)
    {
        await using var s = _store.LightweightSession();
        s.Store(new IngestedLog
        {
            ProjectId = project,
            ServiceId = service,
            VersionId = Guid.NewGuid(),
            TimeUnixNano = time,
            SeverityNumber = severity,
            SeverityText = severity >= 17 ? "ERROR" : "WARN",
            Body = body,
            Attributes = new Dictionary<string, string>
            {
                ["log.category"] = category,
                ["tamp.session.id"] = session,
            },
            ReceiptId = Guid.NewGuid().ToString("N"),
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        await s.SaveChangesAsync();
    }

    private async Task StoreSpans(Guid project, Guid service, string trace,
        params (string name, long start, long duration, int status)[] spans)
    {
        await using var s = _store.LightweightSession();
        foreach (var (name, start, duration, status) in spans)
        {
            s.Store(new IngestedSpan
            {
                ProjectId = project,
                ServiceId = service,
                VersionId = Guid.NewGuid(),
                TraceId = trace,
                SpanId = Guid.NewGuid().ToString("N")[..16],
                Name = name,
                StartUnixNano = start,
                EndUnixNano = start + duration,
                DurationNano = duration,
                StatusCode = status,
                ReceiptId = Guid.NewGuid().ToString("N"),
                ReceivedAt = DateTimeOffset.UtcNow,
            });
        }
        await s.SaveChangesAsync();
    }

    private async Task StoreLog(Guid project, Guid service, string trace, string body)
    {
        await using var s = _store.LightweightSession();
        s.Store(new IngestedLog
        {
            ProjectId = project,
            ServiceId = service,
            VersionId = Guid.NewGuid(),
            TraceId = trace,
            TimeUnixNano = 1000,
            SeverityText = "ERROR",
            Body = body,
            ReceiptId = Guid.NewGuid().ToString("N"),
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        await s.SaveChangesAsync();
    }
}
