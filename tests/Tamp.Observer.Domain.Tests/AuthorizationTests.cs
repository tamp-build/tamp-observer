using Tamp.Observer.Domain;
using Xunit;

namespace Tamp.Observer.Domain.Tests;

/// <summary>Pure unit tests for the RBAC model and decider (ADR 0013). Fast lane.</summary>
public sealed class AuthorizationTests
{
    [Fact]
    public void Role_capability_sets()
    {
        Assert.Contains(Capability.ViewErrors, Roles.Capabilities(Role.Viewer));
        Assert.DoesNotContain(Capability.EditCapturePolicy, Roles.Capabilities(Role.Viewer));
        Assert.Contains(Capability.EditCapturePolicy, Roles.Capabilities(Role.Editor));
        Assert.DoesNotContain(Capability.ManageUsers, Roles.Capabilities(Role.Editor));
        Assert.Contains(Capability.AdministerInstance, Roles.Capabilities(Role.Admin));
    }

    [Fact]
    public void Scope_covers_follows_the_hierarchy()
    {
        var p = Guid.NewGuid();
        var e = Guid.NewGuid();
        var other = Guid.NewGuid();

        Assert.True(ResourceScope.Instance.Covers(ResourceScope.ForEnvironment(p, e)));
        Assert.True(ResourceScope.ForProject(p).Covers(ResourceScope.ForProject(p)));
        Assert.True(ResourceScope.ForProject(p).Covers(ResourceScope.ForEnvironment(p, e)));
        Assert.False(ResourceScope.ForProject(p).Covers(ResourceScope.ForProject(other)));
        Assert.False(ResourceScope.ForProject(p).Covers(ResourceScope.Instance));
        Assert.True(ResourceScope.ForEnvironment(p, e).Covers(ResourceScope.ForEnvironment(p, e)));
        Assert.False(ResourceScope.ForEnvironment(p, e).Covers(ResourceScope.ForProject(p)));
    }

    [Fact]
    public void Grant_allows_capability_within_scope_only()
    {
        var p = Guid.NewGuid();
        var assignments = new List<RoleAssignment>
        {
            new() { SubjectId = "u", Role = Role.Editor, ScopeKind = ScopeKind.Project, ProjectId = p },
        };
        var advisory = new EnforcementGate(EnforcementMode.Advisory);

        Assert.True(AuthorizationDecider.Decide(assignments, Capability.EditCapturePolicy, ResourceScope.ForProject(p), anyGrantsExistInInstance: true, advisory));
        Assert.False(AuthorizationDecider.Decide(assignments, Capability.ManageUsers, ResourceScope.ForProject(p), true, advisory));
        Assert.False(AuthorizationDecider.Decide(assignments, Capability.ViewErrors, ResourceScope.ForProject(Guid.NewGuid()), true, advisory));
    }

    [Fact]
    public void No_grant_denies_once_grants_exist()
    {
        var advisory = new EnforcementGate(EnforcementMode.Advisory);
        Assert.False(AuthorizationDecider.Decide([], Capability.ViewErrors, ResourceScope.Instance, anyGrantsExistInInstance: true, advisory));
    }

    [Fact]
    public void Bootstrap_allows_in_advisory_but_not_enforcing()
    {
        // No assignments and no grants anywhere: the zero-assignment bootstrap.
        Assert.True(AuthorizationDecider.Decide([], Capability.ViewErrors, ResourceScope.Instance, anyGrantsExistInInstance: false, new EnforcementGate(EnforcementMode.Advisory)));
        Assert.False(AuthorizationDecider.Decide([], Capability.ViewErrors, ResourceScope.Instance, anyGrantsExistInInstance: false, new EnforcementGate(EnforcementMode.Enforcing)));
    }

    [Fact]
    public void Identity_mode_defaults_off()
    {
        Assert.Equal(IdentityMode.Off, default(IdentityMode));
    }
}
