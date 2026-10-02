namespace Tamp.Observer.Domain;

/// <summary>
/// A pre-registered identity permitted to use the instance (ADR 0013). Authentication is external (the IdP
/// proves who you are); this is the admission list that decides WHETHER a proven identity may in at all. There
/// is no self-service account creation: an admin pre-registers an email, and only those emails are admitted.
/// Keyed by email because that is the stable, human-meaningful identity a GitHub login carries, where the OIDC
/// subject is an opaque per-broker id.
/// </summary>
public sealed class AllowedIdentity
{
    public Guid Id { get; set; }

    /// <summary>The permitted email, stored lowercased for case-insensitive matching. Unique per instance.</summary>
    public required string Email { get; set; }

    /// <summary>The role this identity is granted (reserved for per-role capabilities; default Viewer).</summary>
    public Role Role { get; set; } = Role.Viewer;

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Normalize an email for storage and lookup (trim + lowercase).</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
