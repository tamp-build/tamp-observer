using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Api;

/// <summary>
/// The admission gate (ADR 0013): a valid OIDC token proves who you are, but only a pre-registered email may in.
/// This is distinct from capability RBAC; it is the "no self-service accounts, no randos" fence. Applied to the
/// whole /api surface, so an authenticated-but-unregistered user gets 403 everywhere.
/// </summary>
public sealed class AllowlistedRequirement : IAuthorizationRequirement;

public sealed class AllowlistedHandler(IAllowedIdentityStore allowlist) : AuthorizationHandler<AllowlistedRequirement>
{
    public const string PolicyName = "Allowlisted";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AllowlistedRequirement requirement)
    {
        // The IdP asserts the email; we decide admission. GitHub-via-Dex carries the GitHub email here.
        var email = context.User.FindFirstValue("email") ?? context.User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrEmpty(email))
            return; // no email to match -> requirement unmet -> 403

        var allowed = await allowlist.FindAsync(email);
        if (allowed is not null)
            context.Succeed(requirement);
    }
}
