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
        // Issues are upserts (new or updated projections); Marten Store() inserts or updates by Id.
        foreach (var issue in batch.Issues) session.Store(issue);

        // Time-bucketed rollup (TOBS-25): fold the per-bucket deltas into the rollup table in the SAME
        // transaction as the documents, so a rollup bucket never drifts from the events that produced it.
        if (batch.Rollups is { IsEmpty: false } rollups)
        {
            foreach (var r in rollups.Signals)
                session.QueueSqlCommand(RollupSchema.UpsertSignalSql,
                    r.ProjectId, r.ServiceId, r.Signal, r.BucketStart,
                    r.EventCount, r.ErrorCount, r.Bytes, r.LatHist, r.FreshHist);
            foreach (var o in rollups.Operations)
                session.QueueSqlCommand(RollupSchema.UpsertOperationSql,
                    o.ProjectId, o.ServiceId, o.Operation, o.BucketStart, o.Count, o.ErrorCount, o.LatHist);
            foreach (var i in rollups.Issues)
                session.QueueSqlCommand(RollupSchema.UpsertIssueSql,
                    i.ProjectId, i.ServiceId, i.Fingerprint, i.BucketStart, i.Count);
            foreach (var se in rollups.Sessions)
                session.QueueSqlCommand(RollupSchema.UpsertSessionSql,
                    se.ProjectId, se.ServiceId, se.Fingerprint, se.SessionId, se.LastSeenNano);
        }

        await session.SaveChangesAsync(ct);
    }
}
