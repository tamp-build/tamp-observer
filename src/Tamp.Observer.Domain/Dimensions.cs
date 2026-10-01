namespace Tamp.Observer.Domain;

/// <summary>
/// Dimensions stamped on events, not structural entities (ADR 0007). They are slice/triage and
/// correlation keys, not part of entity identity, so they live as value types rather than documents.
/// </summary>
public static class Synthetic
{
    /// <summary>Bucket for telemetry with no <c>service.version</c> (ADR 0008).</summary>
    public const string UnversionedBuild = "unversioned";

    /// <summary>Bucket for telemetry with no <c>service.instance.id</c> (ADR 0008).</summary>
    public const string UnknownInstance = "unknown-instance";
}

/// <summary>
/// Backend process/host instance from OTLP <c>service.instance.id</c> (ADR 0007). A slice/triage
/// dimension, not part of identity; synthesized as <see cref="Synthetic.UnknownInstance"/> and flagged
/// when missing, rather than fabricating distinct instances we cannot distinguish.
/// </summary>
public readonly record struct InstanceId(string Value, bool IsSynthesized = false)
{
    /// <summary>The <see cref="Synthetic.UnknownInstance"/> value, flagged as synthesized.</summary>
    public static InstanceId Unknown { get; } = new(Synthetic.UnknownInstance, IsSynthesized: true);
}

/// <summary>
/// Frontend session key: a client-minted opaque UUID (ADR 0007). Not a user, not an IP. The key for
/// replay events and for stitching a frontend session to backend errors (ADR 0010).
/// </summary>
public readonly record struct SessionId(Guid Value);
