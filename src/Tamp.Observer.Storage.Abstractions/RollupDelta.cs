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

/// <summary>
/// The rollup deltas accumulated while admitting one batch (TOBS-25), carried on <see cref="AdmittedBatch"/> and
/// persisted by the sink in the same transaction as the documents. Only the signal rollup exists today; the
/// operation / issue-occurrence / session rollups are added in later stages.
/// </summary>
public sealed record RollupDelta(IReadOnlyList<SignalRollupRow> Signals)
{
    public static RollupDelta Empty { get; } = new([]);

    public bool IsEmpty => Signals.Count == 0;
}
