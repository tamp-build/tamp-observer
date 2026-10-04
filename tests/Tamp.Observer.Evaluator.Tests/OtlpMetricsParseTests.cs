using Google.Protobuf;
using Tamp.Observer.Evaluator;
using Tamp.Observer.Evaluator.Otlp;
using Xunit;

namespace Tamp.Observer.Evaluator.Tests;

/// <summary>Unit tests for OTLP metric gauge/sum data-point parsing (TOBS-43). Fast lane.</summary>
public sealed class OtlpMetricsParseTests
{
    private static byte[] Metrics(params (string Name, double? AsDouble, long? AsInt, ulong Time)[] points)
    {
        var data = new MetricsData();
        var rm = new ResourceMetrics { Resource = new Resource() };
        rm.Resource.Attributes.Add(new KeyValue { Key = "tamp.project.key", Value = new AnyValue { StringValue = "p" } });
        var sm = new ScopeMetrics();
        foreach (var (name, asDouble, asInt, time) in points)
        {
            var dp = new NumberDataPoint { TimeUnixNano = time };
            if (asInt is long i) dp.AsInt = i; else dp.AsDouble = asDouble ?? 0;
            var metric = new Metric { Name = name, Gauge = new Gauge() };
            metric.Gauge.DataPoints.Add(dp);
            sm.Metrics.Add(metric);
        }
        rm.ScopeMetrics.Add(sm);
        data.ResourceMetrics.Add(rm);
        return data.ToByteArray();
    }

    [Fact]
    public void Parses_gauge_double_and_int_points()
    {
        var parsed = OtlpParser.Parse("metrics",
            Metrics(("sample.active_count", 42.0, null, 1000), ("up", null, 1, 2000)));

        Assert.Single(parsed);
        var pts = parsed[0].Metrics;
        Assert.Equal(2, pts.Count);

        var players = Assert.Single(pts, p => p.Name == "sample.active_count");
        Assert.Equal(42.0, players.Value);
        Assert.Equal(1000, players.TimeUnixNano);

        var up = Assert.Single(pts, p => p.Name == "up");
        Assert.Equal(1.0, up.Value); // int point widened to double
        Assert.Equal(2000, up.TimeUnixNano);
    }

    [Fact]
    public void Resource_attributes_still_parse_for_admit()
    {
        var parsed = OtlpParser.Parse("metrics", Metrics(("m", 1.0, null, 1)));
        Assert.Equal("p", parsed[0].Attributes.Get("tamp.project.key"));
    }

    [Fact]
    public void Sum_data_points_are_parsed()
    {
        var data = new MetricsData();
        var rm = new ResourceMetrics { Resource = new Resource() };
        var sm = new ScopeMetrics();
        var m = new Metric { Name = "requests", Sum = new Sum() };
        m.Sum.DataPoints.Add(new NumberDataPoint { TimeUnixNano = 5, AsInt = 7 });
        sm.Metrics.Add(m);
        rm.ScopeMetrics.Add(sm);
        data.ResourceMetrics.Add(rm);

        var parsed = OtlpParser.Parse("metrics", data.ToByteArray());
        var pt = Assert.Single(parsed[0].Metrics);
        Assert.Equal("requests", pt.Name);
        Assert.Equal(7.0, pt.Value);
    }
}
