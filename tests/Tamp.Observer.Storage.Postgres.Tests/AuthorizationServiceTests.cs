using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.Postgres.Tests;

/// <summary>
/// Proves the authorization chokepoint (ADR 0013) over Marten, including the enforcement tie-in (ADR 0002):
/// grants are honored, non-grants denied, and the zero-assignment bootstrap depends on enforcement mode.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthorizationServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private IDocumentStore _store = null!;
    private MartenAuthorizationService _auth = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = ObserverStore.For(_postgres.GetConnectionString());
        _auth = new MartenAuthorizationService(_store);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Honors_grants_and_denies_the_rest()
    {
        var project = Guid.NewGuid();
        await using (var s = _store.LightweightSession())
        {
            s.Store(new RoleAssignment { SubjectId = "alice", Role = Role.Editor, ScopeKind = ScopeKind.Project, ProjectId = project });
            await s.SaveChangesAsync();
        }

        Assert.True(await _auth.CheckAsync("alice", Capability.EditCapturePolicy, ResourceScope.ForProject(project)));
        Assert.False(await _auth.CheckAsync("alice", Capability.ManageUsers, ResourceScope.ForProject(project)));
        Assert.False(await _auth.CheckAsync("bob", Capability.ViewErrors, ResourceScope.ForProject(project)));
    }

    [Fact]
    public async Task Bootstrap_allows_in_advisory_but_locked_enforcing_denies()
    {
        // Fresh install, no role assignments: advisory bootstrap lets the first user in.
        Assert.True(await _auth.CheckAsync("anyone", Capability.ViewErrors, ResourceScope.Instance));

        // Platform team locks the instance to enforcing: the bootstrap loosening is now absent.
        await using (var s = _store.LightweightSession())
        {
            s.Store(new InstanceSettings { EnforcementMode = EnforcementMode.Enforcing, Locked = true });
            await s.SaveChangesAsync();
        }

        Assert.False(await _auth.CheckAsync("anyone", Capability.ViewErrors, ResourceScope.Instance));
    }
}
