namespace Tamp.Observer.Domain;

/// <summary>
/// Privacy gating for captured attributes (TOBS-28, the "not captured" half of design 7.3; ADR 0012 capture
/// policy, baseline default). Telemetry routinely carries credential-bearing attributes (auth headers, cookies,
/// tokens, passwords). Rather than silently DROPPING them -- which leaves the UI unable to distinguish "the
/// producer never sent this" from "we gated it" -- we keep the KEY and replace the VALUE with a sentinel. The
/// surface then renders a gated attribute as "not captured", which is honest about what existed.
/// </summary>
/// <remarks>
/// This is the always-on baseline redaction. Per-Environment capture policy (ADR 0012) can layer stricter or
/// looser rules on top later; its home is <see cref="DeploymentEnvironment"/>. The matcher is a case-insensitive
/// substring test against a small set of universally sensitive tokens, deliberately conservative so correlation
/// keys (e.g. <c>tamp.session.id</c>) and diagnostic attributes (e.g. <c>exception.*</c>) are never touched.
/// </remarks>
public static class AttributeRedaction
{
    /// <summary>The value stored in place of a gated attribute. A distinctive, unambiguous token the surface
    /// matches exactly to render "not captured"; chosen so it cannot collide with a real attribute value.</summary>
    public const string NotCaptured = "[tamp:not-captured]";

    // Case-insensitive substrings that mark an attribute key as credential-bearing. Conservative on purpose.
    private static readonly string[] SensitiveTokens =
    [
        "authorization", "proxy-authorization", "cookie", "set-cookie",
        "password", "passwd", "secret", "token", "api-key", "apikey", "api_key",
        "x-api-key", "credential", "private-key", "privatekey", "access-key", "access_key",
    ];

    /// <summary>True when an attribute key names a credential-bearing value that must be gated.</summary>
    public static bool IsSensitive(string key)
    {
        foreach (var t in SensitiveTokens)
            if (key.Contains(t, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Replace the value of every sensitive-keyed attribute with <see cref="NotCaptured"/>, in place.
    /// Returns the same dictionary for convenient chaining at the call site.</summary>
    public static Dictionary<string, string> Redact(Dictionary<string, string> attributes)
    {
        foreach (var key in attributes.Keys.ToArray())
            if (IsSensitive(key))
                attributes[key] = NotCaptured;
        return attributes;
    }
}
