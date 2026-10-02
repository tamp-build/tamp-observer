using Tamp.Observer.Domain;
using Xunit;

namespace Tamp.Observer.Domain.Tests;

/// <summary>
/// Pure unit tests for enforcement-mode resolution and the gate (ADR 0002). No infrastructure; these run
/// in the fast unit lane.
/// </summary>
public sealed class EnforcementTests
{
    // ----- resolution: Project -> Instance, with the locked floor -----

    [Theory]
    [InlineData(EnforcementMode.Advisory, null, EnforcementMode.Advisory)]    // unlocked, inherit
    [InlineData(EnforcementMode.Advisory, EnforcementMode.Enforcing, EnforcementMode.Enforcing)] // project most-specific
    [InlineData(EnforcementMode.Enforcing, EnforcementMode.Advisory, EnforcementMode.Advisory)]  // unlocked: project may lower
    public void Unlocked_most_specific_wins(EnforcementMode instance, EnforcementMode? project, EnforcementMode expected)
    {
        Assert.Equal(expected, EnforcementResolver.Resolve(instance, locked: false, project));
    }

    [Theory]
    [InlineData(EnforcementMode.Enforcing, null, EnforcementMode.Enforcing)]   // floor applies when inheriting
    [InlineData(EnforcementMode.Enforcing, EnforcementMode.Advisory, EnforcementMode.Enforcing)] // cannot weaken below floor
    [InlineData(EnforcementMode.Advisory, EnforcementMode.Enforcing, EnforcementMode.Enforcing)] // may exceed the floor
    [InlineData(EnforcementMode.Advisory, null, EnforcementMode.Advisory)]
    public void Locked_is_a_non_weakenable_floor(EnforcementMode instance, EnforcementMode? project, EnforcementMode expected)
    {
        Assert.Equal(expected, EnforcementResolver.Resolve(instance, locked: true, project));
    }

    // ----- gate: advisory honors the override, enforcing refuses without reading it -----

    [Fact]
    public void Advisory_gate_honors_the_operator_override()
    {
        var gate = new EnforcementGate(EnforcementMode.Advisory);
        Assert.True(gate.Allows(Loosening.IdentityCaptureInProduction, advisoryOverride: true));
        Assert.False(gate.Allows(Loosening.IdentityCaptureInProduction, advisoryOverride: false));
    }

    [Fact]
    public void Enforcing_gate_refuses_every_loosening_regardless_of_override()
    {
        var gate = new EnforcementGate(EnforcementMode.Enforcing);
        Assert.True(gate.IsEnforcing);
        foreach (var loosening in Enum.GetValues<Loosening>())
        {
            // Even with the override set to true, enforcing refuses: the loosening path is absent.
            Assert.False(gate.Allows(loosening, advisoryOverride: true));
        }
    }

    [Fact]
    public void Resolve_builds_a_gate_from_instance_settings()
    {
        var instance = new InstanceSettings { EnforcementMode = EnforcementMode.Enforcing, Locked = true };
        var gate = EnforcementGate.Resolve(instance, projectMode: EnforcementMode.Advisory);
        Assert.Equal(EnforcementMode.Enforcing, gate.Mode); // project could not weaken the locked floor
        Assert.False(gate.Allows(Loosening.AuditDisable, advisoryOverride: true));
    }

    [Fact]
    public void Instance_settings_default_to_advisory_unlocked()
    {
        var instance = new InstanceSettings();
        Assert.Equal(EnforcementMode.Advisory, instance.EnforcementMode);
        Assert.False(instance.Locked);
        Assert.Equal(InstanceSettings.SingletonId, instance.Id);
    }
}
