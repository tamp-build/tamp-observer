using Tamp.Observer.Domain;
using Xunit;

namespace Tamp.Observer.Domain.Tests;

/// <summary>Unit tests for the rollup latency histogram (TOBS-25): bucketization and summable p95. Fast lane.</summary>
public sealed class LatencyHistogramTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(500, 0)]          // < 1µs -> bucket 0
    [InlineData(1_000, 1)]        // 1µs
    [InlineData(1_500, 1)]        // [1,2)µs
    [InlineData(2_000, 2)]        // 2µs
    [InlineData(3_000, 2)]        // [2,4)µs
    [InlineData(4_000, 3)]        // 4µs
    [InlineData(1_000_000, 10)]   // 1ms = 1000µs -> floor(log2(1000))+1 = 10
    public void Bucketize_places_durations_in_exponential_bands(long durationNano, int expectedIndex) =>
        Assert.Equal(expectedIndex, LatencyHistogram.Bucketize(durationNano));

    [Fact]
    public void Bucketize_clamps_huge_durations_to_overflow()
    {
        Assert.Equal(LatencyHistogram.Buckets - 1, LatencyHistogram.Bucketize(long.MaxValue));
    }

    [Fact]
    public void Empty_percentile_is_zero()
    {
        Assert.Equal(0, LatencyHistogram.Percentile(LatencyHistogram.Empty(), 0.95));
    }

    [Fact]
    public void Percentile_is_within_the_containing_power_of_two_band()
    {
        // 1000 samples all at 5ms (5000µs): p95 must land in 5000µs's band, i.e. [4096µs, 8192µs).
        var h = LatencyHistogram.Empty();
        for (var i = 0; i < 1000; i++) LatencyHistogram.Observe(h, 5_000_000);
        var p95 = LatencyHistogram.Percentile(h, 0.95);
        Assert.InRange(p95, 4096.0 * 1000, 8192.0 * 1000);
    }

    [Fact]
    public void Percentile_picks_the_tail_band_for_a_skewed_mix()
    {
        // 90 fast (1ms) + 10 slow (1s): the 95th percentile falls past the fast samples, into the slow band.
        var h = LatencyHistogram.Empty();
        for (var i = 0; i < 90; i++) LatencyHistogram.Observe(h, 1_000_000);      // 1ms
        for (var i = 0; i < 10; i++) LatencyHistogram.Observe(h, 1_000_000_000);  // 1s
        var p95 = LatencyHistogram.Percentile(h, 0.95);
        Assert.True(p95 >= 100_000_000, $"expected p95 in the slow tail, got {p95}ns");
    }

    [Fact]
    public void AddInto_sums_element_wise()
    {
        var a = LatencyHistogram.Empty();
        var b = LatencyHistogram.Empty();
        LatencyHistogram.Observe(a, 1_000);   // bucket 1
        LatencyHistogram.Observe(b, 1_500);   // bucket 1
        LatencyHistogram.Observe(b, 1_000_000); // bucket 10
        LatencyHistogram.AddInto(a, b);
        Assert.Equal(2, a[1]);
        Assert.Equal(1, a[10]);
    }
}
