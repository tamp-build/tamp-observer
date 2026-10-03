using System.Security.Claims;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Api;

/// <summary>
/// Capability checks at the HTTP boundary (ADR 0013). A valid OIDC token proves identity; the pre-registered
/// identity's role (from the admission list) is the source of truth for what it may do. The admission policy
/// (<see cref="AllowlistedHandler"/>) has already confirmed the caller is admitted before any of this runs; this
/// adds the per-capability check and maps allow/deny to 200/403. Audit later becomes "emit from here".
///
/// Roles are instance-scoped in this MVP; per-resource (project/environment) grants are future work layered on
/// the same seam. The richer RoleAssignment model exists in the domain for that.
/// </summary>
public static class HttpAuthorization
{
    public static string? SubjectId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");

    public static string? Email(this ClaimsPrincipal user) =>
        user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email);

    /// <summary>The caller's role and capability set, resolved from the admission list by email.</summary>
    public static async Task<(Role? Role, IReadOnlySet<Capability> Capabilities)> AccessAsync(
        HttpContext http, IAllowedIdentityStore allowlist, CancellationToken ct = default)
    {
        var email = http.User.Email();
        if (string.IsNullOrEmpty(email))
            return (null, new HashSet<Capability>());
        var identity = await allowlist.FindAsync(email, ct);
        return identity is null
            ? (null, new HashSet<Capability>())
            : (identity.Role, new HashSet<Capability>(Roles.Capabilities(identity.Role)));
    }

    /// <summary>Null when the caller has <paramref name="capability"/>; a 403 result otherwise.</summary>
    public static async Task<IResult?> RequireAsync(
        HttpContext http, IAllowedIdentityStore allowlist, Capability capability, CancellationToken ct = default)
    {
        var (_, capabilities) = await AccessAsync(http, allowlist, ct);
        return capabilities.Contains(capability)
            ? null
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
    }
}
