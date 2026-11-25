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

        // Remove any row already holding this email before storing, in its own SaveChanges.
        //
        // Needed because the id derivation CHANGED (tamp-observer#16): it used to be MD5 of the email
        // and is now SHA-256, so a row written by an earlier version has a different id for the same
        // email. Email carries a UniqueIndex, so storing the new id for an address that already exists
        // under the old one is a unique-index violation, not a silent duplicate -- AddAsync would start
        // throwing for exactly the addresses that were already admitted.
        //
        // Deleting by email rather than by "what the old id would have been" means this does not need
        // to know anything about the previous algorithm, so MD5 is gone from this file entirely and the
        // same code keeps working if the derivation ever changes again. It migrates lazily: the first
        // re-add of an address moves it onto the new id. Rows never re-added keep their old id, which
        // costs nothing -- FindAsync and ListAsync go by email, and the id only exists to make this
        // upsert work.
        //
        // This does put an expression in front of Marten on the WRITE path, which the note above
        // avoids on the read path. That is an accepted trade: AddAsync is an out-of-band admin action
        // that is already doing a round trip, and correctness of the documented "idempotent on the
        // normalized email" contract outweighs a one-off codegen cost there.
        s.DeleteWhere<AllowedIdentity>(x => x.Email == normalized);
        await s.SaveChangesAsync(ct);

        s.Store(new AllowedIdentity
        {
            Id = DeterministicId(normalized),
            Email = normalized,
            Role = role,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await s.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A stable id for an email. SHA-256 truncated to 16 bytes — this is an identifier, not a digest
    /// anyone verifies, so truncation is fine.
    /// </summary>
    /// <remarks>
    /// Was MD5 (csharpsquid:S4790). MD5 here was never password hashing or a security boundary — it
    /// derived a namespaced id — so the CRITICAL severity overstated it. It was still worth removing:
    /// this is the <em>allowed-identity</em> store, and MD5 sitting in the file a reader checks to see
    /// how admission works costs that reader time on every visit, however benign it turns out to be.
    /// A collision would also have merged two admission rows, which is an access-control correctness
    /// failure even without being exploitable.
    /// </remarks>
    private static Guid DeterministicId(string normalizedEmail) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)).AsSpan(0, 16));

    public async Task<IReadOnlyList<AllowedIdentity>> ListAsync(CancellationToken ct = default)
    {
        await using var s = _store.QuerySession();
        return await s.Query<AllowedIdentity>().OrderBy(x => x.Email).ToListAsync(ct);
    }
}
