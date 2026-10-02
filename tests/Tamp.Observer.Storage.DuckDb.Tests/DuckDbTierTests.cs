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
