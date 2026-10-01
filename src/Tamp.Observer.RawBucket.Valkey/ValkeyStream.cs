namespace Tamp.Observer.RawBucket.Valkey;

/// <summary>
/// Shared wire contract for the Valkey Streams raw-bucket tier (ADR 0004 section 2). The Go landing
/// exporter (XADD) and the .NET reader (XREADGROUP) must agree on these field names and defaults.
/// </summary>
public static class ValkeyStream
{
    /// <summary>Stream field holding the JSON envelope.</summary>
    public const string EnvelopeField = "envelope";

    /// <summary>Stream field holding the opaque OTLP payload bytes.</summary>
    public const string PayloadField = "payload";

    /// <summary>Default stream key.</summary>
    public const string DefaultStreamKey = "tamp.observer.raw";

    /// <summary>Default consumer group drained by the evaluator.</summary>
    public const string DefaultConsumerGroup = "evaluator";
}
