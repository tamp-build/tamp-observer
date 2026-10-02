using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// Marten implementation of the replay metadata half of the split (ADR 0010 section 3). Session metadata is a
/// small queryable document in the Postgres baseline store; the payload firehose lives in the blob store, never
/// here. Unique on (Project, SessionId) so chunk upserts converge on one row per session.
/// </summary>
public sealed class MartenReplaySessionStore(IDocumentStore store) : IReplaySessionStore
{
    private readonly IDocumentStore _store = store;

    public async Task UpsertAsync(ReplaySession session, CancellationToken ct = default)
    {
        await using var s = _store.LightweightSession();
        s.Store(session);
        await s.SaveChangesAsync(ct);
    }

    public async Task<ReplaySession?> FindAsync(Guid projectId, string sessionId, CancellationToken ct = default)
    {
        await using var s = _store.QuerySession();
        return await s.Query<ReplaySession>()
            .Where(x => x.ProjectId == projectId && x.SessionId == sessionId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ReplaySessionSummary>> ListAsync(Guid projectId, int limit = 50, CancellationToken ct = default)
    {
        await using var s = _store.QuerySession();
        var rows = await s.Query<ReplaySession>()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.LastEventAtUtc)
            .Take(limit <= 0 ? 50 : limit)
            .ToListAsync(ct);

        return rows.Select(x => new ReplaySessionSummary(
            x.SessionId, x.StartedAtUtc, x.LastEventAtUtc, x.EventCount, x.PayloadBytes, x.StartUrl, x.UserAgent)).ToList();
    }
}
