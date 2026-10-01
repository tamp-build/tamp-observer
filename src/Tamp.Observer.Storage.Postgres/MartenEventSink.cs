using Marten;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// The Postgres/Marten implementation of the write contract (ADR 0006). Persists an admitted batch in a
/// single session so entity provisioning and telemetry land atomically (ADR 0004: an event is admitted
/// or not, never half).
/// </summary>
public sealed class MartenEventSink(IDocumentStore store) : IEventSink
{
    private readonly IDocumentStore _store = store;

    public async Task WriteAsync(AdmittedBatch batch, CancellationToken ct = default)
    {
        if (batch.IsEmpty)
            return;

        await using var session = _store.LightweightSession();

        foreach (var service in batch.NewServices) session.Store(service);
        foreach (var environment in batch.NewEnvironments) session.Store(environment);
        foreach (var version in batch.NewVersions) session.Store(version);
        foreach (var span in batch.Spans) session.Store(span);
        foreach (var log in batch.Logs) session.Store(log);

        await session.SaveChangesAsync(ct);
    }
}
