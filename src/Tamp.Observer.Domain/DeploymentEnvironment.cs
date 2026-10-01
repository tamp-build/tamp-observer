namespace Tamp.Observer.Domain;

/// <summary>
/// Discovered from OTLP <c>deployment.environment</c> and auto-created on first sight, but
/// policy-bearing: it is the one discovered entity that carries behavior (ADR 0007). It is the scope
/// operators attach capture policy to (ADR 0012). Orthogonal to <see cref="ServiceVersion"/>.
/// </summary>
/// <remarks>
/// Named <c>DeploymentEnvironment</c> rather than <c>Environment</c> to avoid colliding with
/// <see cref="System.Environment"/> under implicit usings. The ADR calls this axis "Environment".
/// </remarks>
public class DeploymentEnvironment
{
    /// <summary>Marten document identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning Project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>OTLP <c>deployment.environment</c> (e.g. dev, qa, staging, prod).</summary>
    public required string Name { get; set; }

    /// <summary>When this environment was first seen.</summary>
    public DateTimeOffset FirstSeenAtUtc { get; set; }

    // Capture policy (baseline mode, trigger rules, detail level) binds here per ADR 0012 and is
    // added when that is built. Environment is first-class precisely so policy has one home.
}
