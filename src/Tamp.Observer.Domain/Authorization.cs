namespace Tamp.Observer.Domain;

/// <summary>Capability verbs, modelled around tamp-observer resources (ADR 0013), not IdP roles.</summary>
public enum Capability
{
    ViewErrors,
    ViewTraces,
    ViewLogs,
    ViewReplay,
    EditCapturePolicy,
    ManageUsers,
    AdministerInstance,
}

/// <summary>Built-in roles. The IdP authenticates and may supply groups; groups map to these as inputs,
/// never as the role model itself (ADR 0013).</summary>
public enum Role
{
    Viewer,
    Editor,
    Admin,
}

/// <summary>Identity capture mode (ADR 0013), a capture-policy field owned here. Default off.</summary>
public enum IdentityMode
{
    Off = 0,
    PseudonymousHash = 1,
    Full = 2,
}

/// <summary>Maps built-in roles to their capability sets.</summary>
public static class Roles
{
    private static readonly HashSet<Capability> ViewerCaps =
        [Capability.ViewErrors, Capability.ViewTraces, Capability.ViewLogs, Capability.ViewReplay];

    private static readonly HashSet<Capability> EditorCaps =
        [.. ViewerCaps, Capability.EditCapturePolicy];

    private static readonly HashSet<Capability> AdminCaps =
        [.. Enum.GetValues<Capability>()];

    public static IReadOnlySet<Capability> Capabilities(Role role) => role switch
    {
        Role.Viewer => ViewerCaps,
        Role.Editor => EditorCaps,
        Role.Admin => AdminCaps,
        _ => new HashSet<Capability>(),
    };
}

/// <summary>What a grant or a check targets.</summary>
public enum ScopeKind { Instance, Project, Environment }

/// <summary>
/// A resource scope for RBAC (ADR 0013). A broader grant covers narrower targets: an Instance grant
/// covers everything; a Project grant covers that project and its environments; an Environment grant
/// covers only that environment.
/// </summary>
public sealed record ResourceScope(ScopeKind Kind, Guid? ProjectId = null, Guid? EnvironmentId = null)
{
    public static ResourceScope Instance { get; } = new(ScopeKind.Instance);
    public static ResourceScope ForProject(Guid projectId) => new(ScopeKind.Project, projectId);
    public static ResourceScope ForEnvironment(Guid projectId, Guid environmentId) =>
        new(ScopeKind.Environment, projectId, environmentId);

    /// <summary>Whether this (grant) scope covers the given target scope.</summary>
    public bool Covers(ResourceScope target) => Kind switch
    {
        ScopeKind.Instance => true,
        ScopeKind.Project => target.ProjectId == ProjectId,
        ScopeKind.Environment => target.Kind == ScopeKind.Environment
                                 && target.ProjectId == ProjectId
                                 && target.EnvironmentId == EnvironmentId,
        _ => false,
    };
}

/// <summary>
/// The pure RBAC decision (ADR 0013). A subject is allowed if any of their role assignments grants the
/// capability at a scope that covers the target. With no matching grant, the bootstrap case (no grants
/// exist anywhere yet) is a loosening gated by enforcement: permitted in advisory, refused in enforcing
/// (ADR 0002). Otherwise denied.
/// </summary>
public static class AuthorizationDecider
{
    public static bool Decide(
        IReadOnlyList<RoleAssignment> subjectAssignments,
        Capability capability,
        ResourceScope target,
        bool anyGrantsExistInInstance,
        IEnforcementGate gate)
    {
        foreach (var assignment in subjectAssignments)
            if (Roles.Capabilities(assignment.Role).Contains(capability) && assignment.ToScope().Covers(target))
                return true;

        if (!anyGrantsExistInInstance)
            return gate.Allows(Loosening.SubRbacAuth, advisoryOverride: true);

        return false;
    }
}

/// <summary>
/// The single authorization chokepoint (ADR 0013). Every authz check routes through here so audit becomes
/// "emit from the chokepoint" later, not a retrofit into many call sites.
/// </summary>
public interface IAuthorizationService
{
    Task<bool> CheckAsync(string subjectId, Capability capability, ResourceScope target, CancellationToken ct = default);
}
