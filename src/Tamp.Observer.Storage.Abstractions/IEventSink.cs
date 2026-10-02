using Tamp.Observer.Domain;

namespace Tamp.Observer.Storage.Abstractions;

/// <summary>
/// A fully-resolved unit of admitted work: the entities discovered on this event that need persisting
/// (Project is never here; it is the human-granted trust root, ADR 0007) plus the promoted telemetry,
/// already stamped with resolved entity ids. Resolution (the reads) happens before the sink; the sink
/// only persists.
/// </summary>
public sealed record AdmittedBatch(
    IReadOnlyList<Service> NewServices,
    IReadOnlyList<DeploymentEnvironment> NewEnvironments,
    IReadOnlyList<ServiceVersion> NewVersions,
    IReadOnlyList<IngestedSpan> Spans,
    IReadOnlyList<IngestedLog> Logs,
    IReadOnlyList<Issue> Issues)
{
    public bool IsEmpty =>
        NewServices.Count == 0 && NewEnvironments.Count == 0 && NewVersions.Count == 0
        && Spans.Count == 0 && Logs.Count == 0 && Issues.Count == 0;
}

/// <summary>
/// The single durable WRITE contract into the store (ADR 0006). The contract is identical whether the
/// caller is the evaluator's admit path or a backfill/parallel-run consumer during a tier cutover
/// (ADR 0005 section 6). Buffering slots in FRONT of the sink; it is never a rewrite of it, which is what
/// lets the raw-bucket tier and cutover be config, not a fork.
/// </summary>
public interface IEventSink
{
    /// <summary>Persist an admitted batch atomically.</summary>
    Task WriteAsync(AdmittedBatch batch, CancellationToken ct = default);
}
