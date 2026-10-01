using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;
using Tamp.Observer.Storage.ClickHouse;
using Testcontainers.ClickHouse;
using Xunit;

namespace Tamp.Observer.Storage.ClickHouse.Tests;

/// <summary>
/// Proves the ClickHouse top tier (ADR 0005) behind the same capability interface (ADR 0006): a batch
/// written via the ClickHouse sink is read back through the same IObservabilityStore intents, with the
/// analytical reductions pushed down natively (quantileExact, GROUP BY). Runs against a real ClickHouse
/// via Testcontainers.
/// </summary>
public sealed class ClickHouseTierTests : IAsyncLifetime
{
    private readonly ClickHouseContainer _clickhouse = new ClickHouseBuilder("clickhouse/clickhouse-server:24.8-alpine").Build();
    private string _conn = null!;
    private IEventSink _sink = null!;
    private IObservabilityStore _reads = null!;

    public async Task InitializeAsync()
    {
        await _clickhouse.StartAsync();
        _conn = _clickhouse.GetConnectionString();
        await ClickHouseSchema.EnsureAsync(_conn);
        _sink = new ClickHouseEventSink(_conn);
        _reads = new ClickHouseObservabilityStore(_conn);
    }

    public Task DisposeAsync() => _clickhouse.DisposeAsync().AsTask();

    [Fact]
    public async Task Write_then_read_percentiles_top_ops_and_trace()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        var version = Guid.NewGuid();

        var spans = new[]
        {
            Span(project, service, version, "trace-1", "op1", start: 1000, duration: 10, status: 0),
            Span(project, service, version, "trace-1", "op1", 1100, 20, 0),
            Span(project, service, version, "trace-2", "op1", 1200, 30, 0),
            Span(project, service, version, "trace-2", "op2", 1300, 40, 2),   // error
            Span(project, service, version, "trace-2", "op2", 1400, 50, 0),
            Span(project, Guid.NewGuid(), version, "trace-3", "other", 1500, 999, 0), // other service
        };
        var logs = new[] { Log(project, service, version, "trace-1", "boom") };

        await _sink.WriteAsync(new AdmittedBatch([], [], [], spans, logs));

        var query = new SpanQuery(project, new TimeWindow(1000, 2000), ServiceId: service);

        // Percentiles pushed down natively. Assert count + monotonic + bounds (not CH's exact algorithm).
        var p = await _reads.GetLatencyPercentilesAsync(query);
        Assert.Equal(5, p.Count); // only `service`, in-window
        Assert.InRange(p.P50, 10, 50);
        Assert.True(p.P50 <= p.P95 && p.P95 <= p.P99);
        Assert.InRange(p.P99, 40, 50);

        // Top operations via GROUP BY pushdown.
        var top = await _reads.GetTopOperationsAsync(query);
        Assert.Equal(2, top.Count);
        Assert.Equal(new OperationStat("op1", 3, 0), top[0]);
        Assert.Equal(new OperationStat("op2", 2, 1), top[1]);

        // Correlation walk.
        var view = await _reads.GetTraceAsync(project, "trace-1");
        Assert.Equal(2, view.Spans.Count);
        Assert.Single(view.Logs);
        Assert.Equal("boom", view.Logs[0].Body);
    }

    private static IngestedSpan Span(Guid p, Guid svc, Guid ver, string trace, string name, long start, long duration, int status) => new()
    {
        ProjectId = p, ServiceId = svc, VersionId = ver, EnvironmentId = Guid.NewGuid(),
        TraceId = trace, SpanId = Guid.NewGuid().ToString("N")[..16], Name = name,
        Kind = 2, StartUnixNano = start, EndUnixNano = start + duration, DurationNano = duration,
        StatusCode = status, InstanceId = "host-1", ReceiptId = Guid.NewGuid().ToString("N"), ReceivedAt = DateTimeOffset.UtcNow,
    };

    private static IngestedLog Log(Guid p, Guid svc, Guid ver, string trace, string body) => new()
    {
        ProjectId = p, ServiceId = svc, VersionId = ver, TraceId = trace,
        TimeUnixNano = 1000, SeverityNumber = 9, SeverityText = "INFO", Body = body,
        InstanceId = "host-1", ReceiptId = Guid.NewGuid().ToString("N"), ReceivedAt = DateTimeOffset.UtcNow,
    };
}
