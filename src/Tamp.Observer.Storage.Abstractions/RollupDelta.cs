namespace Tamp.Observer.Storage.Abstractions;

/// <summary>
/// One per-bucket signal rollup row delta produced on admit (TOBS-25): the counts/bytes and latency+freshness
/// histograms to fold into <c>observer_rollup_signal</c> at (project, service, signal, bucket). Histograms are
/// fixed-length <see cref="Tamp.Observer.Domain.LatencyHistogram.Buckets"/> arrays; the sink adds them element-wise
/// into the stored row. <c>Signal</c> is one of "span", "log", "replay".
/// </summary>
public sealed record SignalRollupRow(
    Guid ProjectId, Guid ServiceId, string Signal, long BucketStart,
    long EventCount, long ErrorCount, long Bytes, long[] LatHist, long[] FreshHist);

/// <summary>Per-operation (span name) rollup row delta: calls + errors + a latency histogram at
/// (project, service, operation, bucket). Drives top-operations and per-operation p95/calls series (TOBS-25).</summary>
public sealed record OperationRollupRow(
    Guid ProjectId, Guid ServiceId, string Operation, long BucketStart,
    long Count, long ErrorCount, long[] LatHist);

/// <summary>Per-Issue occurrence rollup row delta: the occurrence count for a fingerprint at
/// (project, service, fingerprint, bucket). Drives the per-issue sparkline (TOBS-25).</summary>
public sealed record IssueRollupRow(
    Guid ProjectId, Guid ServiceId, string Fingerprint, long BucketStart, long Count);

/// <summary>A distinct (fingerprint, session) pair seen on admit, with the latest time it was seen. A dedup set
/// so distinct-session-per-issue over a window is COUNT(DISTINCT session_id), not a sum of per-bucket counts (TOBS-25).</summary>
public sealed record IssueSessionRow(
    Guid ProjectId, Guid ServiceId, string Fingerprint, string SessionId, long LastSeenNano);

/// <summary>
/// The rollup deltas accumulated while admitting one batch (TOBS-25), carried on <see cref="AdmittedBatch"/> and
/// persisted by the sink in the same transaction as the documents.
/// </summary>
public sealed record RollupDelta(
    IReadOnlyList<SignalRollupRow> Signals,
    IReadOnlyList<OperationRollupRow> Operations,
    IReadOnlyList<IssueRollupRow> Issues,
    IReadOnlyList<IssueSessionRow> Sessions)
{
    public static RollupDelta Empty { get; } = new([], [], [], []);

    public bool IsEmpty => Signals.Count == 0 && Operations.Count == 0 && Issues.Count == 0 && Sessions.Count == 0;
}
