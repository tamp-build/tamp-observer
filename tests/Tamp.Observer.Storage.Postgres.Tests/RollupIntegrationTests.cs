using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;
using Tamp.Observer.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.Postgres.Tests;

/// <summary>
/// Proves the materialized signal rollup (TOBS-25): the sink folds per-bucket deltas into the rollup table via
/// an atomic upsert, and GetSpanSeriesAsync serves counts/errors/p95/bytes from it, summed over the window.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RollupIntegrationTests : IAsyncLifetime
{
    private const long Bucket = 60_000_000_000L; // 60s
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private IDocumentStore _store = null!;
    private MartenEventSink _sink = null!;
    private MartenObservabilityStore _reads = null!;
    private string _conn = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _conn = _postgres.GetConnectionString();
        await RollupSchema.EnsureAsync(_conn);
        _store = ObserverStore.For(_conn);
        _sink = new MartenEventSink(_store);
        _reads = new MartenObservabilityStore(_store, _conn);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    private static long[] Hist(long durationNano, long count)
    {
        var h = LatencyHistogram.Empty();
        for (var i = 0; i < count; i++) LatencyHistogram.Observe(h, durationNano);
        return h;
    }

    private async Task WriteSignal(Guid project, Guid service, long bucketStart, long events, long errors, long bytes, long durationNano)
    {
        var row = new SignalRollupRow(project, service, "span", bucketStart, events, errors, bytes,
            Hist(durationNano, events), LatencyHistogram.Empty());
        await _sink.WriteAsync(new AdmittedBatch([], [], [], [], [], [], new RollupDelta([row], [], [], [])));
    }

    private async Task WriteRollup(RollupDelta delta)
        => await _sink.WriteAsync(new AdmittedBatch([], [], [], [], [], [], delta));

    [Fact]
    public async Task Series_sums_buckets_and_upsert_increments()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        var b0 = Bucket * 100;

        // Two writes to the SAME (service, signal, bucket) must add via ON CONFLICT, not overwrite.
        await WriteSignal(project, service, b0, events: 3, errors: 1, bytes: 300, durationNano: 2_000_000);
        await WriteSignal(project, service, b0, events: 2, errors: 0, bytes: 200, durationNano: 2_000_000);
        // A second bucket one minute later.
        await WriteSignal(project, service, b0 + Bucket, events: 4, errors: 2, bytes: 400, durationNano: 2_000_000);

        // One output bucket covering both rollup buckets: totals sum.
        var whole = await _reads.GetSpanSeriesAsync(
            new SpanQuery(project, new TimeWindow(b0, b0 + 2 * Bucket)), buckets: 1);
        Assert.Single(whole);
        Assert.Equal(9, whole[0].Count);       // 3 + 2 + 4
        Assert.Equal(3, whole[0].ErrorCount);  // 1 + 0 + 2
        Assert.Equal(900, whole[0].Bytes);     // 300 + 200 + 400
        // All samples are 2ms (2000µs) -> p95 in the [1024µs, 2048µs) band.
        Assert.InRange(whole[0].P95Nano, 1024L * 1000, 2048L * 1000);

        // Two output buckets: the per-bucket split is preserved.
        var split = await _reads.GetSpanSeriesAsync(
            new SpanQuery(project, new TimeWindow(b0, b0 + 2 * Bucket)), buckets: 2);
        Assert.Equal(2, split.Count);
        Assert.Equal(5, split[0].Count);  // first bucket: 3 + 2
        Assert.Equal(4, split[1].Count);  // second bucket
    }

    [Fact]
    public async Task Operation_and_issue_and_session_rollups_read_back()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        var b0 = Bucket * 300;
        var window = new TimeWindow(b0, b0 + Bucket);

        // Two operations across two writes to the same bucket (must sum via upsert).
        await WriteRollup(new RollupDelta(
            [],
            [new OperationRollupRow(project, service, "GET /a", b0, 6, 1, Hist(2_000_000, 6)),
             new OperationRollupRow(project, service, "GET /b", b0, 2, 0, Hist(2_000_000, 2))],
            [new IssueRollupRow(project, service, "fp1", b0, 3)],
            [new IssueSessionRow(project, service, "fp1", "sess-1", b0 + 1),
             new IssueSessionRow(project, service, "fp1", "sess-2", b0 + 2)]));
        await WriteRollup(new RollupDelta(
            [],
            [new OperationRollupRow(project, service, "GET /a", b0, 4, 2, Hist(2_000_000, 4))],
            [new IssueRollupRow(project, service, "fp1", b0, 1)],
            [new IssueSessionRow(project, service, "fp1", "sess-1", b0 + 9)])); // duplicate session, GREATEST last_seen

        var ops = await _reads.GetTopOperationsAsync(new SpanQuery(project, window));
        Assert.Equal("GET /a", ops[0].Operation);
        Assert.Equal(10, ops[0].Count);      // 6 + 4
        Assert.Equal(3, ops[0].ErrorCount);  // 1 + 2

        var opSeries = await _reads.GetOperationSeriesAsync(new SpanQuery(project, window), buckets: 1);
        var a = opSeries.Single(o => o.Operation == "GET /a");
        Assert.Equal(10, a.Count);
        Assert.InRange(a.P95Nano, 1024L * 1000, 2048L * 1000); // 2ms band

        var issues = await _reads.GetIssueSeriesAsync(project, window, buckets: 1);
        var fp1 = issues.Single(i => i.Fingerprint == "fp1");
        Assert.Equal(4, fp1.Buckets[0]);  // 3 + 1 occurrences
        Assert.Equal(2, fp1.Sessions);    // sess-1 (deduped) + sess-2
    }

    private static IngestedMetric Metric(Guid p, Guid s, string name, double v, long t) =>
        new() { ProjectId = p, ServiceId = s, Name = name, Value = v, TimeUnixNano = t, ReceiptId = "r", ReceivedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task Metrics_latest_and_series_read_back()
    {
        var project = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var t0 = Bucket * 700;
        await _sink.WriteAsync(new AdmittedBatch([], [], [], [], [], [], null,
            [Metric(project, a, "sample.active_count", 10, t0), Metric(project, a, "sample.active_count", 25, t0 + Bucket)]));
        await _sink.WriteAsync(new AdmittedBatch([], [], [], [], [], [], null,
            [Metric(project, a, "up", 1, t0 + Bucket), Metric(project, b, "up", 0, t0 + Bucket)]));

        var latest = await _reads.GetLatestMetricsAsync(project, new TimeWindow(t0, t0 + 2 * Bucket));
        Assert.Equal(25, latest.Single(m => m.Name == "sample.active_count").Value); // newest wins
        Assert.Equal(2, latest.Count(m => m.Name == "up"));                      // per service
        Assert.Equal(1, latest.Single(m => m.Name == "up" && m.ServiceId == a).Value);
        Assert.Equal(0, latest.Single(m => m.Name == "up" && m.ServiceId == b).Value);

        var series = await _reads.GetMetricSeriesAsync(project, "sample.active_count", new TimeWindow(t0, t0 + 2 * Bucket), buckets: 2);
        Assert.Equal(2, series.Count);
        Assert.Equal(10, series[0].Value);
        Assert.Equal(25, series[1].Value);
    }

    [Fact]
    public async Task Telemetry_prune_removes_old_spans_logs_metrics_keeps_new()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        var oldT = Bucket * 10;
        var newT = Bucket * 1000;
        IngestedSpan Span(long start) => new() { ProjectId = project, ServiceId = service, TraceId = "t", SpanId = Guid.NewGuid().ToString(), Name = "op", StartUnixNano = start, EndUnixNano = start + 1, DurationNano = 1, ReceiptId = "r", ReceivedAt = DateTimeOffset.UtcNow };
        IngestedLog Log(long time) => new() { ProjectId = project, ServiceId = service, TimeUnixNano = time, Body = "b", ReceiptId = "r", ReceivedAt = DateTimeOffset.UtcNow };

        await _sink.WriteAsync(new AdmittedBatch([], [], [], [Span(oldT), Span(newT)], [Log(oldT), Log(newT)], [], null,
            [Metric(project, service, "m", 1, oldT), Metric(project, service, "m", 2, newT)]));

        var removed = await TelemetryRetention.PruneAsync(_conn, cutoffNano: Bucket * 500);
        Assert.Equal(3, removed); // 1 span + 1 log + 1 metric older than the cutoff

        await using var q = _store.QuerySession();
        Assert.Equal(1, await q.Query<IngestedSpan>().Where(s => s.ProjectId == project).CountAsync());
        Assert.Equal(1, await q.Query<IngestedLog>().Where(l => l.ProjectId == project).CountAsync());
        Assert.Equal(1, await q.Query<IngestedMetric>().Where(m => m.ProjectId == project).CountAsync());
    }

    [Fact]
    public async Task Prune_removes_rows_before_cutoff()
    {
        var project = Guid.NewGuid();
        var service = Guid.NewGuid();
        var oldBucket = Bucket * 10;
        var newBucket = Bucket * 1000;
        await WriteSignal(project, service, oldBucket, events: 3, errors: 0, bytes: 0, durationNano: 1_000_000);
        await WriteSignal(project, service, newBucket, events: 4, errors: 0, bytes: 0, durationNano: 1_000_000);

        await RollupSchema.PruneAsync(_conn, cutoffNano: Bucket * 500);

        var oldWindow = await _reads.GetSpanSeriesAsync(new SpanQuery(project, new TimeWindow(oldBucket, oldBucket + Bucket)), 1);
        var newWindow = await _reads.GetSpanSeriesAsync(new SpanQuery(project, new TimeWindow(newBucket, newBucket + Bucket)), 1);
        Assert.Equal(0, oldWindow[0].Count); // pruned
        Assert.Equal(4, newWindow[0].Count); // retained
    }

    [Fact]
    public async Task Service_filter_scopes_the_series()
    {
        var project = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var b0 = Bucket * 200;
        await WriteSignal(project, a, b0, events: 5, errors: 0, bytes: 0, durationNano: 1_000_000);
        await WriteSignal(project, b, b0, events: 7, errors: 0, bytes: 0, durationNano: 1_000_000);

        var onlyA = await _reads.GetSpanSeriesAsync(
            new SpanQuery(project, new TimeWindow(b0, b0 + Bucket), a), buckets: 1);
        Assert.Equal(5, onlyA[0].Count);
    }
}
