using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>Marten implementation of the Issue read/write surface (ADR 0015). Issues are Postgres-canonical.</summary>
public sealed class MartenIssueStore(IDocumentStore store) : IIssueStore
{
    private readonly IDocumentStore _store = store;

    public async Task<IReadOnlyList<Issue>> ListAsync(
        Guid projectId, IssueStatus? status = null, Guid? serviceId = null, int limit = 100, CancellationToken ct = default)
    {
        await using var s = _store.QuerySession();
        var q = s.Query<Issue>().Where(i => i.ProjectId == projectId);
        if (status is { } st)
            q = q.Where(i => i.Status == st);
        if (serviceId is { } svc)
            q = q.Where(i => i.ServiceId == svc);
        return await q.OrderByDescending(i => i.LastSeenAtUtc).Take(limit <= 0 ? 100 : limit).ToListAsync(ct);
    }

    public async Task<Issue?> GetAsync(Guid projectId, Guid issueId, CancellationToken ct = default)
    {
        await using var s = _store.QuerySession();
        return await s.Query<Issue>().FirstOrDefaultAsync(i => i.ProjectId == projectId && i.Id == issueId, ct);
    }

    public async Task<Issue?> GetByFingerprintAsync(Guid projectId, string fingerprint, CancellationToken ct = default)
    {
        await using var s = _store.QuerySession();
        return await s.Query<Issue>()
            .FirstOrDefaultAsync(i => i.ProjectId == projectId && i.Fingerprint == fingerprint, ct);
    }

    public async Task<bool> SetStatusAsync(
        Guid projectId, Guid issueId, IssueStatus status, long? resolvedInVersionSequence = null, CancellationToken ct = default)
    {
        await using var s = _store.LightweightSession();
        var issue = await s.Query<Issue>().FirstOrDefaultAsync(i => i.ProjectId == projectId && i.Id == issueId, ct);
        if (issue is null)
            return false;

        issue.Status = status;
        if (status == IssueStatus.Resolved)
            issue.ResolvedInVersionSequence = resolvedInVersionSequence ?? issue.LastSeenVersionSequence;
        else if (status == IssueStatus.Unresolved)
            issue.ResolvedInVersionSequence = null;

        s.Store(issue);
        await s.SaveChangesAsync(ct);
        return true;
    }
}
