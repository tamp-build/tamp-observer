using Google.Protobuf;
using Tamp.Observer.Evaluator;
using Tamp.Observer.Evaluator.Otlp;
using Xunit;

namespace Tamp.Observer.Evaluator.Tests;

/// <summary>
/// Pure unit tests for OTLP parsing (no containers): resource-attribute and span extraction from the
/// trimmed OTLP schema, and signal routing. Runs in the fast unit lane (not tagged Integration).
/// </summary>
public sealed class OtlpParserTests
{
    [Fact]
    public void Parses_resource_attributes_and_spans_from_traces()
    {
        var resource = new Resource();
        resource.Attributes.Add(Attr(ResourceKeys.ProjectKey, "acme"));
        resource.Attributes.Add(Attr(ResourceKeys.ServiceName, "checkout-api"));

        var span = new Span
        {
            TraceId = ByteString.CopyFrom(new byte[16]),
            SpanId = ByteString.CopyFrom(new byte[8]),
            Name = "GET /checkout",
            Kind = 2,
            StartTimeUnixNano = 100,
            EndTimeUnixNano = 250,
            Status = new Status { Code = 1 },
        };
        var data = new TracesData
        {
            ResourceSpans = { new ResourceSpans { Resource = resource, ScopeSpans = { new ScopeSpans { Spans = { span } } } } },
        };

        var resources = OtlpParser.Parse("traces", data.ToByteArray());

        Assert.Single(resources);
        Assert.Equal("acme", resources[0].Attributes.Get(ResourceKeys.ProjectKey));
        Assert.Equal("checkout-api", resources[0].Attributes.Get(ResourceKeys.ServiceName));
        Assert.Single(resources[0].Spans);
        Assert.Equal("GET /checkout", resources[0].Spans[0].Name);
        Assert.Equal(1, resources[0].Spans[0].StatusCode);
        Assert.Empty(resources[0].Logs);
    }

    [Fact]
    public void Unknown_signal_returns_empty()
    {
        Assert.Empty(OtlpParser.Parse("something-else", [1, 2, 3]));
    }

    private static KeyValue Attr(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };
}
