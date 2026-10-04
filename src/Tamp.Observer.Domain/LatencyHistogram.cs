namespace Tamp.Observer.Domain;

/// <summary>
/// A compact fixed-length latency histogram for the time-bucketed rollup (ADR 0015, TOBS-25). Durations land
/// in 32 base-2 exponential buckets anchored at 1µs, counts only. Histograms are summable (element-wise add),
/// so a percentile over an arbitrary query window is derived by summing the per-rollup-bucket arrays and
/// walking the cumulative counts. The reported value is accurate to the containing power-of-two band, which is
/// what tiles and sparklines need; an exact percentile still comes from a raw span scan. The same layout backs
/// the freshness/replay-lag histogram (receivedAt - eventTime).
/// </summary>
public static class LatencyHistogram
{
    /// <summary>Number of buckets. Index 0 = [0,1µs); index i = [2^(i-1)µs, 2^i µs); index 31 = overflow.</summary>
    public const int Buckets = 32;

    private const long MicroNano = 1000; // 1µs in nanoseconds

    /// <summary>A fresh all-zero histogram.</summary>
    public static long[] Empty() => new long[Buckets];

    /// <summary>The bucket index a duration (nanoseconds) falls into: floor(log2(µs)) clamped to [0,31].</summary>
    public static int Bucketize(long durationNano)
    {
        if (durationNano < MicroNano) return 0;
        var micros = durationNano / MicroNano;
        var idx = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)micros); // floor(log2(micros)) + 1
        return Math.Clamp(idx, 0, Buckets - 1);
    }

    /// <summary>Add <paramref name="durationNano"/> into <paramref name="hist"/> (mutating).</summary>
    public static void Observe(long[] hist, long durationNano) => hist[Bucketize(durationNano)]++;

    /// <summary>Element-wise add <paramref name="src"/> into <paramref name="dst"/> (mutating dst).</summary>
    public static void AddInto(long[] dst, IReadOnlyList<long> src)
    {
        var n = Math.Min(dst.Length, src.Count);
        for (var i = 0; i < n; i++) dst[i] += src[i];
    }

    /// <summary>
    /// The p-th percentile (0..1) in nanoseconds over a summed histogram: find the bucket where the cumulative
    /// count first crosses p*total, then linearly interpolate within that bucket's [lo,hi) band. Returns 0 for empty.
    /// </summary>
    public static double Percentile(IReadOnlyList<long> counts, double p)
    {
        long total = 0;
        for (var i = 0; i < counts.Count; i++) total += counts[i];
        if (total == 0) return 0;

        var target = p * total;
        long cumulative = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            if (counts[i] == 0) continue;
            var prev = cumulative;
            cumulative += counts[i];
            if (cumulative < target) continue;

            // Bucket i covers [lo, hi) nanoseconds.
            double lo = i == 0 ? 0 : (double)(1L << (i - 1)) * MicroNano;
            double hi = i == 0 ? MicroNano : (double)(1L << i) * MicroNano;
            if (i == Buckets - 1) return lo; // overflow bucket: report its floor
            var within = (target - prev) / counts[i]; // 0..1 position inside this bucket
            return lo + within * (hi - lo);
        }
        return 0;
    }
}
