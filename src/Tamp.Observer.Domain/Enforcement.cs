namespace Tamp.Observer.Domain;

/// <summary>
/// The enforcement-surface loosenings governed by enforcement mode (ADR 0002). Under enforcing (and
/// non-weakenably under a locked instance) each of these is refused. The mode never touches the
/// scale/performance surface (storage tier, buffer tier); that is an orthogonal axis.
/// </summary>
public enum Loosening
{
    IdentityCaptureInProduction,
    SessionTransmitWithoutConsent,
    SubStrictPiiMasking,
    SubRbacAuth,
    ComponentReachback,
    SubFloorCapturePolicy,
    AuditDisable,
}

/// <summary>
/// Resolves the effective enforcement mode along the Project -&gt; Instance chain (ADR 0002). There is no
/// Client layer. A locked instance mode is a floor: a project may raise it, never lower it.
/// </summary>
public static class EnforcementResolver
{
    public static EnforcementMode Resolve(EnforcementMode instanceMode, bool locked, EnforcementMode? projectMode)
    {
        if (locked)
        {
            // Floor: effective is the stricter (higher) of the instance floor and the project's choice.
            var project = projectMode ?? instanceMode;
            return Stricter(instanceMode, project);
        }

        // Unlocked: most-specific wins.
        return projectMode ?? instanceMode;
    }

    public static EnforcementMode Resolve(InstanceSettings instance, EnforcementMode? projectMode)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Resolve(instance.EnforcementMode, instance.Locked, projectMode);
    }

    private static EnforcementMode Stricter(EnforcementMode a, EnforcementMode b) =>
        (EnforcementMode)Math.Max((int)a, (int)b);
}

/// <summary>
/// The single mode boundary (ADR 0002 invariant 3). All enforcement-surface code asks this gate whether a
/// loosening is permitted. Under advisory the operator's override decides; under enforcing the loosening
/// is refused WITHOUT consulting the override, so the loosening path is genuinely absent, not merely
/// defaulted strict.
/// </summary>
public interface IEnforcementGate
{
    EnforcementMode Mode { get; }

    bool IsEnforcing { get; }

    /// <summary>
    /// Whether <paramref name="loosening"/> is permitted. <paramref name="advisoryOverride"/> is the
    /// operator-configured value that applies only in advisory mode; under enforcing it is never read.
    /// </summary>
    bool Allows(Loosening loosening, bool advisoryOverride);
}

/// <summary>Effective-mode implementation of <see cref="IEnforcementGate"/>.</summary>
public sealed class EnforcementGate(EnforcementMode mode) : IEnforcementGate
{
    public EnforcementMode Mode => mode;

    public bool IsEnforcing => mode == EnforcementMode.Enforcing;

    public bool Allows(Loosening loosening, bool advisoryOverride)
    {
        // Enforcing: the loosening path is absent. We do not branch on advisoryOverride here, by design
        // (ADR 0002 invariant 3): an assessor can trust the loosening cannot happen.
        if (mode == EnforcementMode.Enforcing)
            return false;

        return advisoryOverride;
    }

    /// <summary>Build the gate for a project from the instance settings (ADR 0002 resolution).</summary>
    public static EnforcementGate Resolve(InstanceSettings instance, EnforcementMode? projectMode) =>
        new(EnforcementResolver.Resolve(instance, projectMode));
}
