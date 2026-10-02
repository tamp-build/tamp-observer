namespace Tamp.Observer.Storage.Abstractions;

/// <summary>
/// A write sink that fans an admitted batch across two tiers (ADR 0005): entities and issues go to the
/// system-of-record sink (Postgres), the high-volume telemetry (spans and logs) goes to the analytical sink
/// (e.g. ClickHouse). This is what lets the ClickHouse telemetry tier be a deploy-time dial rather than a
/// fork of the admit path: the evaluator still writes one <see cref="AdmittedBatch"/> through one
/// <see cref="IEventSink"/>; the split happens here.
///
/// The two writes are not a cross-engine transaction (no engine spans both), which is consistent with
/// Postgres being the system of record for entities and the columnar tier holding only telemetry.
/// </summary>
public sealed class CompositeTieredEventSink(IEventSink entitySink, IEventSink telemetrySink) : IEventSink
{
    private readonly IEventSink _entitySink = entitySink;
    private readonly IEventSink _telemetrySink = telemetrySink;

    public async Task WriteAsync(AdmittedBatch batch, CancellationToken ct = default)
    {
        if (batch.IsEmpty)
            return;

        // Entities and issues to the system of record; nothing telemetry-shaped.
        var entities = batch with { Spans = [], Logs = [] };
        if (!entities.IsEmpty)
            await _entitySink.WriteAsync(entities, ct);

        // Telemetry to the analytical tier; no entity provisioning.
        var telemetry = batch with { NewServices = [], NewEnvironments = [], NewVersions = [], Issues = [] };
        if (!telemetry.IsEmpty)
            await _telemetrySink.WriteAsync(telemetry, ct);
    }
}
