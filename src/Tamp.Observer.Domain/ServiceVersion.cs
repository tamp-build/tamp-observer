namespace Tamp.Observer.Domain;

/// <summary>
/// Discovered from OTLP <c>service.version</c>, scoped per-Service, auto-created on first sight (ADR
/// 0007). The correctness/partition axis that makes "resolved in version N / regressed in N+2"
/// computable, and a strong candidate for a physical partition key (ADR 0005).
/// </summary>
/// <remarks>
/// Named <c>ServiceVersion</c> rather than <c>Version</c> to avoid colliding with
/// <see cref="System.Version"/> under implicit usings.
/// </remarks>
public class ServiceVersion
{
    /// <summary>Marten document identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning Project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Owning Service (version is scoped per-Service).</summary>
    public Guid ServiceId { get; set; }

    /// <summary>
    /// OTLP <c>service.version</c>, free-form (git sha, CI number, semver, ...). Never sorted on
    /// directly; ordering comes from <see cref="Sequence"/> (ADR 0008). Semver is parsed
    /// opportunistically for display only.
    /// </summary>
    public required string VersionString { get; set; }

    /// <summary>
    /// Server-controlled monotonic sequence assigned from first-seen arrival order at the collector
    /// (ADR 0008). This, not the version string, is the ordering key for the resolved/regressed state
    /// machine. Agent clocks and version strings are untrusted.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// True when this is the synthetic <see cref="Synthetic.UnversionedBuild"/> bucket for telemetry
    /// that arrived with no <c>service.version</c> (bucket, do not crash or merge; ADR 0008).
    /// </summary>
    public bool IsSynthetic { get; set; }

    /// <summary>When this version was first seen at the collector.</summary>
    public DateTimeOffset FirstSeenAtUtc { get; set; }
}
