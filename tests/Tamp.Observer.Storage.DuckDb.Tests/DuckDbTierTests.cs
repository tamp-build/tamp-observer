using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;
using Tamp.Observer.Storage.DuckDb;
using Tamp.Observer.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.DuckDb.Tests;

/// <summary>
/// Proves the DuckDB on-demand accelerator (ADR 0005 middle tier) as a third IObservabilityStore
/// translator: spans written to the Postgres system of record (via Marten) are aggregated by an embedded
/// DuckDB attaching Postgres, with native quantile_cont / GROUP BY. Same interface as the Postgres and
/// ClickHouse providers.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DuckDbTierTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private IDocumentStore _store = null!;
    private DuckDbObservabilityStore _reads = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = ObserverStore.For(_postgres.GetConnectionString());
        _reads = new DuckDbObservabilityStore(_postgres.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Aggregates_over_postgres_via_duckdb()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        var version = Guid.NewGuid();

        await StoreSpans(project, service, version, "trace-1",
            ("op1", 1000, 10, 0), ("op1", 1010, 20, 0), ("op1", 1020, 30, 0),
            ("op2", 1030, 40, 2), ("op2", 1040, 50, 0));
        await StoreSpans(project, Guid.NewGuid(), version, "trace-x", ("other", 5000, 999, 0)); // out of window / other service
        await StoreLog(project, service, version, "trace-1", "boom");

        var query = new SpanQuery(project, new TimeWindow(1000, 2000), ServiceId: service);

        var p = await _reads.GetLatencyPercentilesAsync(query);
        Assert.Equal(5, p.Count);
        Assert.InRange(p.P50, 10, 50);
        Assert.True(p.P50 <= p.P95 && p.P95 <= p.P99);
        Assert.InRange(p.P99, 40, 50);

        var top = await _reads.GetTopOperationsAsync(query);
        Assert.Equal(2, top.Count);
        Assert.Equal(new OperationStat("op1", 3, 0), top[0]);
        Assert.Equal(new OperationStat("op2", 2, 1), top[1]);

        var view = await _reads.GetTraceAsync(project, "trace-1");
        Assert.Equal(5, view.Spans.Count);
        Assert.Single(view.Logs);
        Assert.Equal("boom", view.Logs[0].Body);
    }

    [Fact]
    public async Task Span_series_has_native_p95_and_operation_series()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        var version = Guid.NewGuid();
        await StoreSpans(project, service, version, "trace-s",
            ("op1", 1000, 10, 0), ("op1", 1010, 20, 0), ("op1", 1020, 30, 0),
            ("op2", 1030, 40, 2), ("op2", 1040, 50, 0));
        var query = new SpanQuery(project, new TimeWindow(1000, 2000), ServiceId: service);

        var series = await _reads.GetSpanSeriesAsync(query, buckets: 1);
        Assert.Single(series);
        Assert.Equal(5, series[0].Count);
        Assert.InRange(series[0].P95Nano, 30, 50); // exact quantile_cont over [10..50]

        var ops = await _reads.GetOperationSeriesAsync(query, buckets: 1);
        Assert.Equal(2, ops.Count);
        var op1 = ops.Single(o => o.Operation == "op1");
        Assert.Equal(3, op1.Count);
        Assert.Equal(3, op1.Buckets[0].Count);
        var op2 = ops.Single(o => o.Operation == "op2");
        Assert.Equal(2, op2.Count);
        Assert.Equal(1, op2.ErrorCount);
        Assert.True(op2.P95Nano >= op1.P95Nano); // op2 is slower
    }

    [Fact]
    public async Task Metrics_latest_and_series_via_duckdb()
    {
        var project = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using (var s = _store.LightweightSession())
        {
            s.Store(new IngestedMetric { ProjectId = project, ServiceId = a, Name = "sample.active_count", Value = 10, TimeUnixNano = 1000, ReceiptId = "r", ReceivedAt = DateTimeOffset.UtcNow });
            s.Store(new IngestedMetric { ProjectId = project, ServiceId = a, Name = "sample.active_count", Value = 25, TimeUnixNano = 2000, ReceiptId = "r", ReceivedAt = DateTimeOffset.UtcNow });
            s.Store(new IngestedMetric { ProjectId = project, ServiceId = a, Name = "up", Value = 1, TimeUnixNano = 2000, ReceiptId = "r", ReceivedAt = DateTimeOffset.UtcNow });
            s.Store(new IngestedMetric { ProjectId = project, ServiceId = b, Name = "up", Value = 0, TimeUnixNano = 2000, ReceiptId = "r", ReceivedAt = DateTimeOffset.UtcNow });
            await s.SaveChangesAsync();
        }

        var latest = await _reads.GetLatestMetricsAsync(project, new TimeWindow(1000, 3000));
        Assert.Equal(25, latest.Single(m => m.Name == "sample.active_count").Value);
        Assert.Equal(2, latest.Count(m => m.Name == "up"));
        Assert.Equal(0, latest.Single(m => m.Name == "up" && m.ServiceId == b).Value);

        var series = await _reads.GetMetricSeriesAsync(project, "sample.active_count", new TimeWindow(1000, 3000), buckets: 2);
        Assert.Equal(2, series.Count);
        Assert.Equal(10, series[0].Value);
        Assert.Equal(25, series[1].Value);
    }

    private async Task StoreSpans(Guid project, Guid service, Guid version, string trace,
        params (string name, long start, long duration, int status)[] spans)
    {
        await using var s = _store.LightweightSession();
        foreach (var (name, start, duration, status) in spans)
        {
            s.Store(new IngestedSpan
            {
                ProjectId = project,
                ServiceId = service,
                VersionId = version,
                TraceId = trace,
                SpanId = Guid.NewGuid().ToString("N")[..16],
                Name = name,
                Kind = 2,
                StartUnixNano = start,
                EndUnixNano = start + duration,
                DurationNano = duration,
                StatusCode = status,
                InstanceId = "host-1",
                ReceiptId = Guid.NewGuid().ToString("N"),
                ReceivedAt = DateTimeOffset.UtcNow,
            });
        }
        await s.SaveChangesAsync();
    }

    private async Task StoreLog(Guid project, Guid service, Guid version, string trace, string body)
    {
        await using var s = _store.LightweightSession();
        s.Store(new IngestedLog
        {
            ProjectId = project,
            ServiceId = service,
            VersionId = version,
            TraceId = trace,
            TimeUnixNano = 1000,
            SeverityNumber = 9,
            SeverityText = "INFO",
            Body = body,
            InstanceId = "host-1",
            ReceiptId = Guid.NewGuid().ToString("N"),
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        await s.SaveChangesAsync();
    }
}
