using System.Security.Cryptography;
using System.Text;

namespace Tamp.Observer.Domain;

/// <summary>
/// The Issue fingerprint (ADR 0015): a stable id for "the same error" within a Service. Shared so the evaluator
/// can stamp the same value onto the error occurrence (span/log) that produced it, making an Issue linkable back
/// to its latest occurrence (and thus its trace and session) for the correlation walk (ADR 0014).
/// </summary>
public static class IssueFingerprint
{
    public static string Compute(Guid serviceId, string groupingKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serviceId.ToString("N") + "\n" + groupingKey));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
