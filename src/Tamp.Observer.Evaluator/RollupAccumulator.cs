using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Evaluator;

/// <summary>
/// Accumulates per-bucket rollup deltas while admitting one event (TOBS-25), mirroring
/// <see cref="IssueProjector"/>'s per-event statefulness. Spans and logs fold into fixed-width (default 60s)
/// buckets across four rollups: signal (count/error/bytes + latency+freshness histograms), operation (span
/// name: calls/errors + latency), issue (per-fingerprint occurrence count), and an issue-session dedup set.
/// <see cref="Build"/> emits one row per (key, bucket) so the sink does one upsert per bucket, not per event.
/// </summary>
public sealed class RollupAccumulator(long bucketNanos = RollupAccumulator.DefaultBucketNanos)
{
    public const long DefaultBucketNanos = 60_000_000_000L; // 60s

    private sealed class SignalAgg
    {
        public long Event, Error, Bytes;
        public readonly long[] Lat = LatencyHistogram.Empty();
        public readonly long[] Fresh = LatencyHistogram.Empty();
    }

    private sealed class OpAgg
    {
        public long Count, Error;
        public readonly long[] Lat = LatencyHistogram.Empty();
    }

    private readonly Dictionary<(Guid P, Guid S, string Signal, long Bucket), SignalAgg> _signals = [];
    private readonly Dictionary<(Guid P, Guid S, string Op, long Bucket), OpAgg> _ops = [];
    private readonly Dictionary<(Guid P, Guid S, string Fp, long Bucket), long> _issues = [];
    private readonly Dictionary<(Guid P, Guid S, string Fp, string Sid), long> _sessions = [];
    private readonly long _bucket = bucketNanos > 0 ? bucketNanos : DefaultBucketNanos;

    private long BucketOf(long eventNano) => eventNano - (eventNano % _bucket);

    public void AddSpan(Guid projectId, Guid serviceId, string name, long startUnixNano, long durationNano,
        bool isError, string? fingerprint, string? sessionId, long receivedAtNano)
    {
        var start = BucketOf(startUnixNano);
        var sig = Signal(projectId, serviceId, "span", start);
        sig.Event++;
        if (isError) sig.Error++;
        LatencyHistogram.Observe(sig.Lat, Math.Max(0, durationNano));
        LatencyHistogram.Observe(sig.Fresh, Math.Max(0, receivedAtNano - startUnixNano));

        var opKey = (projectId, serviceId, name, start);
        if (!_ops.TryGetValue(opKey, out var op)) _ops[opKey] = op = new OpAgg();
        op.Count++;
        if (isError) op.Error++;
        LatencyHistogram.Observe(op.Lat, Math.Max(0, durationNano));

        Occurrence(projectId, serviceId, fingerprint, start, sessionId, startUnixNano);
    }

    public void AddLog(Guid projectId, Guid serviceId, long timeUnixNano, bool isError,
        string? fingerprint, string? sessionId, long receivedAtNano)
    {
        var start = BucketOf(timeUnixNano);
        var sig = Signal(projectId, serviceId, "log", start);
        sig.Event++;
        if (isError) sig.Error++;
        LatencyHistogram.Observe(sig.Fresh, Math.Max(0, receivedAtNano - timeUnixNano));

        Occurrence(projectId, serviceId, fingerprint, start, sessionId, timeUnixNano);
    }

    private SignalAgg Signal(Guid p, Guid s, string signal, long bucket)
    {
        var key = (p, s, signal, bucket);
        if (!_signals.TryGetValue(key, out var agg)) _signals[key] = agg = new SignalAgg();
        return agg;
    }

    private void Occurrence(Guid p, Guid s, string? fingerprint, long bucket, string? sessionId, long eventNano)
    {
        if (string.IsNullOrEmpty(fingerprint)) return;
        var key = (p, s, fingerprint, bucket);
        _issues[key] = _issues.GetValueOrDefault(key) + 1;
        if (!string.IsNullOrEmpty(sessionId))
        {
            var sk = (p, s, fingerprint, sessionId);
            var prev = _sessions.GetValueOrDefault(sk);
            if (eventNano > prev) _sessions[sk] = eventNano;
        }
    }

    /// <summary>Distribute one event's payload bytes across the accumulated SIGNAL rows by event-count share
    /// (approximate; an OTLP export item is single-signal so this attributes its size to that signal).</summary>
    public void AttributeBytes(long totalBytes)
    {
        if (totalBytes <= 0) return;
        long totalEvents = 0;
        foreach (var a in _signals.Values) totalEvents += a.Event;
        if (totalEvents == 0) return;
        foreach (var a in _signals.Values) a.Bytes += totalBytes * a.Event / totalEvents;
    }

    public RollupDelta Build()
    {
        if (_signals.Count == 0 && _ops.Count == 0 && _issues.Count == 0 && _sessions.Count == 0)
            return RollupDelta.Empty;

        var signals = new List<SignalRollupRow>(_signals.Count);
        foreach (var (k, a) in _signals)
            signals.Add(new SignalRollupRow(k.P, k.S, k.Signal, k.Bucket, a.Event, a.Error, a.Bytes, a.Lat, a.Fresh));

        var ops = new List<OperationRollupRow>(_ops.Count);
        foreach (var (k, a) in _ops)
            ops.Add(new OperationRollupRow(k.P, k.S, k.Op, k.Bucket, a.Count, a.Error, a.Lat));

        var issues = new List<IssueRollupRow>(_issues.Count);
        foreach (var (k, c) in _issues)
            issues.Add(new IssueRollupRow(k.P, k.S, k.Fp, k.Bucket, c));

        var sessions = new List<IssueSessionRow>(_sessions.Count);
        foreach (var (k, lastSeen) in _sessions)
            sessions.Add(new IssueSessionRow(k.P, k.S, k.Fp, k.Sid, lastSeen));

        return new RollupDelta(signals, ops, issues, sessions);
    }
}
