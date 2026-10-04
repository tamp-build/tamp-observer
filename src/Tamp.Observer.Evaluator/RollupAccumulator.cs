using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Evaluator;

/// <summary>
/// Accumulates per-(project, service, signal, bucket) rollup deltas while admitting one event (TOBS-25),
/// mirroring <see cref="IssueProjector"/>'s per-event statefulness. Spans and logs fold into fixed-width
/// (default 60s) buckets with count/error/bytes plus a latency and a freshness histogram. <see cref="Build"/>
/// emits one <see cref="SignalRollupRow"/> per (key, bucket) so the sink does one upsert per bucket rather
/// than one per span.
/// </summary>
public sealed class RollupAccumulator(long bucketNanos = RollupAccumulator.DefaultBucketNanos)
{
    public const long DefaultBucketNanos = 60_000_000_000L; // 60s

    private sealed class Agg
    {
        public long Event;
        public long Error;
        public long Bytes;
        public readonly long[] Lat = LatencyHistogram.Empty();
        public readonly long[] Fresh = LatencyHistogram.Empty();
    }

    private readonly Dictionary<(Guid Project, Guid Service, string Signal, long Bucket), Agg> _rows = [];
    private readonly long _bucket = bucketNanos > 0 ? bucketNanos : DefaultBucketNanos;

    private Agg Row(Guid projectId, Guid serviceId, string signal, long eventNano)
    {
        var start = eventNano - (eventNano % _bucket);
        var key = (projectId, serviceId, signal, start);
        if (!_rows.TryGetValue(key, out var agg))
            _rows[key] = agg = new Agg();
        return agg;
    }

    public void AddSpan(Guid projectId, Guid serviceId, long startUnixNano, long durationNano, bool isError, long receivedAtNano)
    {
        var a = Row(projectId, serviceId, "span", startUnixNano);
        a.Event++;
        if (isError) a.Error++;
        LatencyHistogram.Observe(a.Lat, Math.Max(0, durationNano));
        LatencyHistogram.Observe(a.Fresh, Math.Max(0, receivedAtNano - startUnixNano));
    }

    public void AddLog(Guid projectId, Guid serviceId, long timeUnixNano, bool isError, long receivedAtNano)
    {
        var a = Row(projectId, serviceId, "log", timeUnixNano);
        a.Event++;
        if (isError) a.Error++;
        LatencyHistogram.Observe(a.Fresh, Math.Max(0, receivedAtNano - timeUnixNano));
    }

    /// <summary>Distribute one event's payload bytes across the accumulated rows by event-count share
    /// (approximate; an OTLP export item is single-signal so this attributes its size to that signal).</summary>
    public void AttributeBytes(long totalBytes)
    {
        if (totalBytes <= 0) return;
        long totalEvents = 0;
        foreach (var a in _rows.Values) totalEvents += a.Event;
        if (totalEvents == 0) return;
        foreach (var a in _rows.Values) a.Bytes += totalBytes * a.Event / totalEvents;
    }

    public RollupDelta Build()
    {
        if (_rows.Count == 0) return RollupDelta.Empty;
        var signals = new List<SignalRollupRow>(_rows.Count);
        foreach (var (key, a) in _rows)
            signals.Add(new SignalRollupRow(key.Project, key.Service, key.Signal, key.Bucket, a.Event, a.Error, a.Bytes, a.Lat, a.Fresh));
        return new RollupDelta(signals);
    }
}
