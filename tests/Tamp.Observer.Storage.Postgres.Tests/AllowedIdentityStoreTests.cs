using System.Security.Cryptography;
using System.Text;
using Marten;
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

    /// <summary>
    /// tamp-observer#16 changed the deterministic id from MD5 of the email to SHA-256. Email carries a
    /// UniqueIndex, so a row written by an EARLIER version sits under a different id for the same
    /// address — and storing the new id for it would be a unique-index violation, meaning AddAsync
    /// would start throwing for exactly the addresses that were already admitted.
    ///
    /// This is the test the issue said actually matters: an address allow-listed before the change
    /// must still resolve after it, and re-adding it must stay idempotent rather than blow up.
    /// </summary>
    [Fact]
    public async Task An_identity_stored_under_the_old_md5_id_still_resolves_and_can_be_re_added()
    {
        const string email = "legacy@example.com";

        // Write the row exactly as a pre-#16 version would have: id = MD5 of the normalized email.
        var normalized = AllowedIdentity.Normalize(email);
        var legacyId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(normalized)));
        await using (var seed = _store.LightweightSession())
        {
            seed.Store(new AllowedIdentity
            {
                Id = legacyId,
                Email = normalized,
                Role = Role.Viewer,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        // Lookup is by email, so a legacy row must resolve untouched -- no migration required to read.
        var before = await _allow.FindAsync(email);
        Assert.NotNull(before);
        Assert.Equal(Role.Viewer, before!.Role);
        Assert.Equal(legacyId, before.Id);

        // Re-adding must not violate the unique index, and must leave exactly one row.
        await _allow.AddAsync(email, Role.Admin);

        var list = await _allow.ListAsync();
        Assert.Single(list);
        Assert.Equal(Role.Admin, list[0].Role);

        // ...and it has been migrated onto the new derivation rather than keeping the MD5 id.
        Assert.NotEqual(legacyId, list[0].Id);
        var expected = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)).AsSpan(0, 16));
        Assert.Equal(expected, list[0].Id);

        Assert.NotNull(await _allow.FindAsync(email));
    }
}
