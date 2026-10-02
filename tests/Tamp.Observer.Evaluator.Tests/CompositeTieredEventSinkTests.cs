using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;
using Xunit;

namespace Tamp.Observer.Evaluator.Tests;

/// <summary>
/// Pure unit tests for the two-tier write split (ADR 0005, TOBS-19): entities/issues go to the system of
/// record, spans/logs go to the analytical tier, and neither sink sees the other's slice. Fast lane.
/// </summary>
public sealed class CompositeTieredEventSinkTests
{
    private sealed class CapturingSink : IEventSink
    {
        public AdmittedBatch? Written { get; private set; }
        public int Calls { get; private set; }

        public Task WriteAsync(AdmittedBatch batch, CancellationToken ct = default)
        {
            Written = batch;
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static IngestedSpan Span() => new()
    {
        ProjectId = Guid.NewGuid(), TraceId = "t", SpanId = "s", Name = "GET /", ReceiptId = "r",
    };

    private static IngestedLog Log() => new() { ProjectId = Guid.NewGuid(), ReceiptId = "r" };

    private static AdmittedBatch FullBatch() => new(
        NewServices: [new Service { ProjectId = Guid.NewGuid(), ServiceName = "svc" }],
        NewEnvironments: [new DeploymentEnvironment { ProjectId = Guid.NewGuid(), Name = "prod" }],
        NewVersions: [new ServiceVersion { ServiceId = Guid.NewGuid(), VersionString = "1.0.0" }],
        Spans: [Span()],
        Logs: [Log()],
        Issues: [new Issue { ProjectId = Guid.NewGuid(), ServiceId = Guid.NewGuid(), Fingerprint = "fp", Title = "boom" }]);

    [Fact]
    public async Task Splits_entities_to_record_and_telemetry_to_analytical()
    {
        var record = new CapturingSink();
        var analytical = new CapturingSink();
        var sut = new CompositeTieredEventSink(record, analytical);

        await sut.WriteAsync(FullBatch());

        // System of record: entities and issues, no telemetry.
        Assert.NotNull(record.Written);
        Assert.Single(record.Written!.NewServices);
        Assert.Single(record.Written.NewEnvironments);
        Assert.Single(record.Written.NewVersions);
        Assert.Single(record.Written.Issues);
        Assert.Empty(record.Written.Spans);
        Assert.Empty(record.Written.Logs);

        // Analytical tier: telemetry only, no entity provisioning.
        Assert.NotNull(analytical.Written);
        Assert.Single(analytical.Written!.Spans);
        Assert.Single(analytical.Written.Logs);
        Assert.Empty(analytical.Written.NewServices);
        Assert.Empty(analytical.Written.NewEnvironments);
        Assert.Empty(analytical.Written.NewVersions);
        Assert.Empty(analytical.Written.Issues);
    }

    [Fact]
    public async Task Skips_a_tier_with_nothing_for_it()
    {
        var record = new CapturingSink();
        var analytical = new CapturingSink();
        var sut = new CompositeTieredEventSink(record, analytical);

        // Telemetry-only batch: the record sink should not be called at all.
        await sut.WriteAsync(new AdmittedBatch([], [], [], [Span()], [], []));

        Assert.Equal(0, record.Calls);
        Assert.Equal(1, analytical.Calls);
    }

    [Fact]
    public async Task Empty_batch_touches_neither_tier()
    {
        var record = new CapturingSink();
        var analytical = new CapturingSink();
        var sut = new CompositeTieredEventSink(record, analytical);

        await sut.WriteAsync(new AdmittedBatch([], [], [], [], [], []));

        Assert.Equal(0, record.Calls);
        Assert.Equal(0, analytical.Calls);
    }
}
