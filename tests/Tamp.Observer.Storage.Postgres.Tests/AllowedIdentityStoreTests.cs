using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.Postgres.Tests;

/// <summary>
/// Proves the admission list persists over Marten (ADR 0013): emails are matched case-insensitively, adding is
/// idempotent (role updates in place), and an unregistered email is not found.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AllowedIdentityStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private Marten.IDocumentStore _store = null!;
    private MartenAllowedIdentityStore _allow = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = ObserverStore.For(_postgres.GetConnectionString());
        _allow = new MartenAllowedIdentityStore(_store);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Pre_registration_matches_case_insensitively_and_is_idempotent()
    {
        await _allow.AddAsync("Scott@Example.com", Role.Viewer);

        var found = await _allow.FindAsync("scott@example.com");
        Assert.NotNull(found);
        Assert.Equal(Role.Viewer, found!.Role);

        // Re-add with a different role updates in place, not a second row.
        await _allow.AddAsync("scott@example.com", Role.Admin);
        var list = await _allow.ListAsync();
        Assert.Single(list);
        Assert.Equal(Role.Admin, list[0].Role);

        Assert.Null(await _allow.FindAsync("nobody@example.com"));
    }
}
