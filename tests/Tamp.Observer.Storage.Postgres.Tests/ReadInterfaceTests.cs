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
        _store = ObserverStore.For(_postgres.GetConnectionString());
        _reads = new MartenObservabilityStore(_store);
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
