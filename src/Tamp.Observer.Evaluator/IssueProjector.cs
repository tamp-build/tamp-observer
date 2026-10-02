using System.Security.Cryptography;
using System.Text;
using Marten;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Evaluator;

/// <summary>
/// Projects admitted error signals into Issues (ADR 0015): resolve-or-create by
/// (Project, Service, Fingerprint), increment count, track timing and affected versions, and flip a
/// resolved issue to regressed when it recurs in a later version (ADR 0008 sequence). Stateful per event
/// so repeated occurrences of one fingerprint aggregate into a single upsert, mirroring the entity
/// resolver (ADR 0004). The collected issues (new or updated) go into the admit batch for the sink.
/// </summary>
public sealed class IssueProjector(IQuerySession read)
{
    private readonly Dictionary<string, Issue> _touched = [];

    public IReadOnlyCollection<Issue> Touched => _touched.Values;

    public async Task ProjectAsync(
        Guid projectId, Guid serviceId, long versionSequence,
        string? errorType, string title, string groupingKey, DateTimeOffset at, CancellationToken ct)
    {
        var fingerprint = Fingerprint(serviceId, groupingKey);

        if (!_touched.TryGetValue(fingerprint, out var issue))
        {
            issue = await read.Query<Issue>().SingleOrDefaultAsync(
                        i => i.ProjectId == projectId && i.ServiceId == serviceId && i.Fingerprint == fingerprint, ct)
                    ?? new Issue
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = projectId,
                        ServiceId = serviceId,
                        Fingerprint = fingerprint,
                        Title = title,
                        ErrorType = errorType,
                        Status = IssueStatus.Unresolved,
                        Count = 0,
                        FirstSeenAtUtc = at,
                        LastSeenAtUtc = at,
                        FirstSeenVersionSequence = versionSequence,
                        LastSeenVersionSequence = versionSequence,
                    };
            _touched[fingerprint] = issue;
        }

        issue.Count += 1;
        if (at > issue.LastSeenAtUtc) issue.LastSeenAtUtc = at;
        if (at < issue.FirstSeenAtUtc) issue.FirstSeenAtUtc = at;
        issue.LastSeenVersionSequence = Math.Max(issue.LastSeenVersionSequence, versionSequence);
        if (!issue.AffectedVersionSequences.Contains(versionSequence))
            issue.AffectedVersionSequences.Add(versionSequence);

        // Regression: a resolved issue recurs in a version later than the one it was resolved in.
        if (issue.Status == IssueStatus.Resolved
            && issue.ResolvedInVersionSequence is long resolvedSeq
            && versionSequence > resolvedSeq)
        {
            issue.Status = IssueStatus.Regressed;
        }
    }

    private static string Fingerprint(Guid serviceId, string groupingKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serviceId.ToString("N") + "\n" + groupingKey));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
