using Tamp.Observer.Domain;

namespace Tamp.Observer.Storage.Abstractions;

/// <summary>
/// The admission list of pre-registered identities (ADR 0013): who may use the instance at all, independent of
/// what capabilities they then have. No self-service creation; an admin adds emails out of band.
/// </summary>
public interface IAllowedIdentityStore
{
    /// <summary>The pre-registered identity for this email, or null if it was never admitted.</summary>
    Task<AllowedIdentity?> FindAsync(string email, CancellationToken ct = default);

    /// <summary>Pre-register (or update the role of) an email. Idempotent on the normalized email.</summary>
    Task AddAsync(string email, Role role, CancellationToken ct = default);

    /// <summary>All pre-registered identities.</summary>
    Task<IReadOnlyList<AllowedIdentity>> ListAsync(CancellationToken ct = default);
}
