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
}

/// <summary>The resource attributes of one resource block within an OTLP payload.</summary>
public sealed class ResourceAttributes
{
    private readonly IReadOnlyDictionary<string, string> _attrs;

    public ResourceAttributes(IReadOnlyDictionary<string, string> attrs) => _attrs = attrs;

    /// <summary>String attribute value, or null if absent or non-string.</summary>
    public string? Get(string key) => _attrs.TryGetValue(key, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> All => _attrs;
}

/// <summary>
/// Parses OTLP payload bytes just far enough to pull resource attributes (ADR 0004 entity
/// resolution). Uses the minimal wire-compatible schema, so one path reads traces, logs, and
/// metrics. Returns one <see cref="ResourceAttributes"/> per resource block in the payload.
/// </summary>
public static class OtlpResources
{
    public static IReadOnlyList<ResourceAttributes> Extract(byte[] payload)
    {
        var data = SignalData.Parser.ParseFrom(payload);
        var result = new List<ResourceAttributes>(data.ResourceEntries.Count);

        foreach (var entry in data.ResourceEntries)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entry.Resource is not null)
            {
                foreach (var kv in entry.Resource.Attributes)
                {
                    // Only string-valued attributes matter for entity resolution.
                    if (kv.Value is { ValueCase: AnyValue.ValueOneofCase.StringValue })
                        dict[kv.Key] = kv.Value.StringValue;
                }
            }
            result.Add(new ResourceAttributes(dict));
        }

        return result;
    }
}
