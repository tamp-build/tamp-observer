using StackExchange.Redis;

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

    /// <summary>
    /// Parse a connection string into options that retry quietly rather than throw when Valkey is not yet
    /// reachable. On a cold start (k8s or <c>docker compose up</c>) the evaluator reliably comes up before
    /// Valkey is accepting connections; <c>AbortOnConnectFail=false</c> lets the multiplexer reconnect in the
    /// background instead of surfacing a connection exception that looks like a real fault.
    /// </summary>
    public static ConfigurationOptions ConnectionOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        return options;
    }
}
