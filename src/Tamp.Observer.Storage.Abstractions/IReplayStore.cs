using Tamp.Observer.Domain;

namespace Tamp.Observer.Storage.Abstractions;

/// <summary>A row in the session list: the metadata needed to decide what to replay, without the payload.</summary>
public sealed record ReplaySessionSummary(
    string SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastEventAtUtc,
    int EventCount,
    long PayloadBytes,
    string? StartUrl,
    string? UserAgent);

/// <summary>
/// Read/write of session-replay metadata in the analytical store (ADR 0010 section 3). This is the queryable
/// half of the metadata/blob split; it never holds the DOM firehose. Kept separate from
/// <see cref="IObservabilityStore"/> because the replay session is its own archetype (ADR 0009), not a span
/// or log query.
/// </summary>
public interface IReplaySessionStore
{
    /// <summary>Record or advance a session's metadata as chunks arrive. Idempotent on (project, session).</summary>
    Task UpsertAsync(ReplaySession session, CancellationToken ct = default);

    /// <summary>Fetch one session's metadata, or null.</summary>
    Task<ReplaySession?> FindAsync(Guid projectId, string sessionId, CancellationToken ct = default);

    /// <summary>List a project's sessions, most recent first.</summary>
    Task<IReadOnlyList<ReplaySessionSummary>> ListAsync(Guid projectId, int limit = 50, CancellationToken ct = default);
}

/// <summary>
/// Storage for the replay payload (ADR 0010 section 3): the opaque DOM-mutation firehose, chunked, written as
/// it arrives and read back by session id to replay. Deliberately NOT the analytical store. The floor
/// implementation is the local filesystem; higher tiers (object storage) slot in behind the same interface.
/// Each chunk is one rrweb-events JSON array exactly as the client sent it; the reader returns them in order.
/// </summary>
public interface IReplayBlobStore
{
    /// <summary>Append one delivered chunk (a JSON array of rrweb events) to a session's blob.</summary>
    Task AppendChunkAsync(Guid projectId, string sessionId, ReadOnlyMemory<byte> chunk, CancellationToken ct = default);

    /// <summary>Read back every chunk for a session, in arrival order. Empty if the session has no blob.</summary>
    Task<IReadOnlyList<byte[]>> ReadChunksAsync(Guid projectId, string sessionId, CancellationToken ct = default);
}
