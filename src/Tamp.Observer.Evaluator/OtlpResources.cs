using Google.Protobuf;
using Tamp.Observer.Evaluator.Otlp;

namespace Tamp.Observer.Evaluator;

/// <summary>OTLP / tamp-observer resource attribute keys the evaluator reads.</summary>
public static class ResourceKeys
{
    /// <summary>Our convention for naming the trust-root Project on the resource (ADR 0007).</summary>
    public const string ProjectKey = "tamp.project.key";
    public const string ServiceName = "service.name";
    public const string ServiceNamespace = "service.namespace";
    public const string ServiceVersion = "service.version";
    public const string DeploymentEnvironment = "deployment.environment";
    public const string ServiceInstanceId = "service.instance.id";

    /// <summary>OTLP exception attribute; the preferred Issue fingerprint source (ADR 0015).</summary>
    public const string ExceptionType = "exception.type";

    /// <summary>The browser-minted session id, stamped on spans/logs for the correlation walk (ADR 0010/0014).</summary>
    public const string SessionId = "tamp.session.id";
}

/// <summary>The resource attributes of one resource block within an OTLP payload.</summary>
public sealed class ResourceAttributes(IReadOnlyDictionary<string, string> attrs)
{
    public string? Get(string key) => attrs.TryGetValue(key, out var v) ? v : null;
    public IReadOnlyDictionary<string, string> All => attrs;
}

/// <summary>A span extracted from a trace payload.</summary>
public sealed record ParsedSpan(
    string TraceId, string SpanId, string? ParentSpanId, string Name, int Kind,
    long StartUnixNano, long EndUnixNano, int StatusCode, string? StatusMessage,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>A log record extracted from a logs payload.</summary>
public sealed record ParsedLog(
    long TimeUnixNano, int SeverityNumber, string? SeverityText, string? Body,
    string? TraceId, string? SpanId, IReadOnlyDictionary<string, string> Attributes);

/// <summary>One resource block with its resolved attributes and the signal bodies under it.</summary>
public sealed record ParsedResource(
    ResourceAttributes Attributes,
    IReadOnlyList<ParsedSpan> Spans,
    IReadOnlyList<ParsedLog> Logs);

/// <summary>
/// Parses OTLP payload bytes into resources plus the span/log bodies the admit path stores (ADR 0004).
/// Uses the trimmed wire-compatible schema; metrics resolve to resources only (data points deferred).
/// </summary>
public static class OtlpParser
{
    public static IReadOnlyList<ParsedResource> Parse(string signal, byte[] payload) => signal switch
    {
        "traces" => ParseTraces(payload),
        "logs" => ParseLogs(payload),
        "metrics" => ParseMetrics(payload),
        _ => [],
    };

    private static List<ParsedResource> ParseTraces(byte[] payload)
    {
        var data = TracesData.Parser.ParseFrom(payload);
        var result = new List<ParsedResource>(data.ResourceSpans.Count);
        foreach (var rs in data.ResourceSpans)
        {
            var spans = new List<ParsedSpan>();
            foreach (var ss in rs.ScopeSpans)
                foreach (var s in ss.Spans)
                    spans.Add(new ParsedSpan(
                        Hex(s.TraceId), Hex(s.SpanId), NullableHex(s.ParentSpanId), s.Name, s.Kind,
                        (long)s.StartTimeUnixNano, (long)s.EndTimeUnixNano,
                        s.Status?.Code ?? 0, s.Status?.Message, StringAttrs(s.Attributes)));
            result.Add(new ParsedResource(Attrs(rs.Resource), spans, []));
        }
        return result;
    }

    private static List<ParsedResource> ParseLogs(byte[] payload)
    {
        var data = LogsData.Parser.ParseFrom(payload);
        var result = new List<ParsedResource>(data.ResourceLogs.Count);
        foreach (var rl in data.ResourceLogs)
        {
            var logs = new List<ParsedLog>();
            foreach (var sl in rl.ScopeLogs)
                foreach (var lr in sl.LogRecords)
                    logs.Add(new ParsedLog(
                        (long)lr.TimeUnixNano, lr.SeverityNumber, EmptyToNull(lr.SeverityText),
                        BodyString(lr.Body), NullableHex(lr.TraceId), NullableHex(lr.SpanId),
                        StringAttrs(lr.Attributes)));
            result.Add(new ParsedResource(Attrs(rl.Resource), [], logs));
        }
        return result;
    }

    private static List<ParsedResource> ParseMetrics(byte[] payload)
    {
        var data = MetricsData.Parser.ParseFrom(payload);
        var result = new List<ParsedResource>(data.ResourceMetrics.Count);
        foreach (var rm in data.ResourceMetrics)
            result.Add(new ParsedResource(Attrs(rm.Resource), [], []));
        return result;
    }

    private static ResourceAttributes Attrs(Resource? resource)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (resource is not null)
            foreach (var kv in resource.Attributes)
                if (kv.Value is { ValueCase: AnyValue.ValueOneofCase.StringValue })
                    dict[kv.Key] = kv.Value.StringValue;
        return new ResourceAttributes(dict);
    }

    private static Dictionary<string, string> StringAttrs(IEnumerable<KeyValue> attrs)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in attrs)
            if (kv.Value is { ValueCase: AnyValue.ValueOneofCase.StringValue })
                dict[kv.Key] = kv.Value.StringValue;
        return dict;
    }

    private static string? BodyString(AnyValue? body) =>
        body is { ValueCase: AnyValue.ValueOneofCase.StringValue } ? body.StringValue : null;

    private static string Hex(ByteString b) => Convert.ToHexStringLower(b.Span);

    private static string? NullableHex(ByteString b) => b.Length == 0 ? null : Convert.ToHexStringLower(b.Span);

    private static string? EmptyToNull(string s) => string.IsNullOrEmpty(s) ? null : s;
}
