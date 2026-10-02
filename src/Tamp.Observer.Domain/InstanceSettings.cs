namespace Tamp.Observer.Domain;

/// <summary>
/// Installation-wide settings (ADR 0002). A single document per installation. Enforcement posture lives
/// here; the platform team sets it once. Ships advisory / unlocked so a fresh install never blocks anyone.
/// </summary>
public class InstanceSettings
{
    /// <summary>Singleton identity; there is one InstanceSettings per installation.</summary>
    public string Id { get; set; } = SingletonId;

    public const string SingletonId = "instance";

    /// <summary>The instance enforcement mode. Default advisory.</summary>
    public EnforcementMode EnforcementMode { get; set; } = EnforcementMode.Advisory;

    /// <summary>
    /// When true, <see cref="EnforcementMode"/> becomes a non-weakenable floor: a project may match or
    /// exceed it, never drop below it (ADR 0002). The platform lever for a locked-down enclave.
    /// </summary>
    public bool Locked { get; set; }
}
