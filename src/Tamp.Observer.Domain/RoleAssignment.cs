namespace Tamp.Observer.Domain;

/// <summary>
/// Grants a <see cref="Role"/> to a subject (an IdP-authenticated user) at a resource scope (ADR 0013).
/// Native to tamp-observer; the IdP never defines these.
/// </summary>
public class RoleAssignment
{
    public Guid Id { get; set; }

    /// <summary>The IdP subject identifier (user).</summary>
    public required string SubjectId { get; set; }

    public Role Role { get; set; }

    public ScopeKind ScopeKind { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? EnvironmentId { get; set; }

    public ResourceScope ToScope() => new(ScopeKind, ProjectId, EnvironmentId);
}
