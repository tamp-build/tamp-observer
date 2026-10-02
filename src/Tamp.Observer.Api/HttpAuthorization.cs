using System.Security.Claims;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Api;

/// <summary>
/// The HTTP-boundary adapter over the single authorization chokepoint (ADR 0013): every read endpoint
/// routes through <see cref="RequireAsync"/>, so audit later becomes "emit from the chokepoint" rather than
/// a retrofit into each endpoint. It maps the chokepoint's allow/deny to HTTP 401/403 and nothing else, so
/// the decision logic stays in <see cref="IAuthorizationService"/> and is not duplicated per route.
/// </summary>
public static class HttpAuthorization
{
    /// <summary>The OIDC subject claim (ADR 0013: authN is always external; the IdP owns the subject id).</summary>
    public static string? SubjectId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");

    /// <summary>
    /// Enforce a capability at a scope for the current principal. Returns <c>null</c> when allowed (the
    /// caller proceeds), a 401 result when there is no authenticated subject, or a 403 result when the
    /// chokepoint denies. Keeping the "allowed" signal as null lets endpoints read as a guard clause.
    /// </summary>
    public static async Task<IResult?> RequireAsync(
        HttpContext http,
        IAuthorizationService authz,
        Capability capability,
        ResourceScope scope,
        CancellationToken ct)
    {
        var subject = http.User.SubjectId();
        if (string.IsNullOrEmpty(subject))
            return Results.Unauthorized();

        return await authz.CheckAsync(subject, capability, scope, ct)
            ? null
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
    }
}
