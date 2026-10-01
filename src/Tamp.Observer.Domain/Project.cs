namespace Tamp.Observer.Domain;

/// <summary>
/// The only administered entity and the trust root (ADR 0007). A human creates a Project; it is
/// never auto-created from telemetry. Service, DeploymentEnvironment, and ServiceVersion are all
/// discovered <em>within</em> a trusted Project. An unknown project is rejected to quarantine, not
/// provisioned.
/// </summary>
public class Project
{
    /// <summary>Marten document identity. Assigned on first store.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-facing, stable key and ingestion identity (unique across the installation). This is the
    /// trust-granting handle an operator configures agents against.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>Display name.</summary>
    public required string Name { get; set; }

    /// <summary>Optional free-text description.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Storage tier for this Project (ADR 0005). The tier is an upward-only ratchet; a downgrade is
    /// illegal in the config model. Default is the Postgres floor.
    /// </summary>
    public StorageTier StorageTier { get; set; } = StorageTier.Postgres;

    /// <summary>
    /// Optional per-project enforcement posture (ADR 0002). <c>null</c> means inherit the instance
    /// default. Resolution is Project -&gt; Instance (one layer shorter than tamp.findings, because
    /// tamp-observer has no Client entity).
    /// </summary>
    public EnforcementMode? EnforcementMode { get; set; }

    /// <summary>When the Project was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}
