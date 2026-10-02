using System.Security.Cryptography;
using System.Text;
using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>Marten implementation of the admission list (ADR 0013). Unique on the normalized email.</summary>
public sealed class MartenAllowedIdentityStore(IDocumentStore store) : IAllowedIdentityStore
{
    private readonly IDocumentStore _store = store;

    public async Task<AllowedIdentity?> FindAsync(string email, CancellationToken ct = default)
    {
        var normalized = AllowedIdentity.Normalize(email);
        await using var s = _store.QuerySession();
        return await s.Query<AllowedIdentity>().FirstOrDefaultAsync(x => x.Email == normalized, ct);
    }

    public async Task AddAsync(string email, Role role, CancellationToken ct = default)
    {
        // Upsert by a deterministic id derived from the email: re-adding the same email updates the same row,
        // and we avoid a read-before-write. The read path (a Marten LINQ query) triggers cold runtime codegen
        // that is pathologically slow in a short-lived one-off process (the admin CLI); a pure Store does not.
        var normalized = AllowedIdentity.Normalize(email);
        await using var s = _store.LightweightSession();
        s.Store(new AllowedIdentity
        {
            Id = DeterministicId(normalized),
            Email = normalized,
            Role = role,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await s.SaveChangesAsync(ct);
    }

    private static Guid DeterministicId(string normalizedEmail) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));

    public async Task<IReadOnlyList<AllowedIdentity>> ListAsync(CancellationToken ct = default)
    {
        await using var s = _store.QuerySession();
        return await s.Query<AllowedIdentity>().OrderBy(x => x.Email).ToListAsync(ct);
    }
}
