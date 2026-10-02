namespace Tamp.Observer.Domain;

/// <summary>
/// Session-replay metadata (ADR 0010 section 3): the small, queryable record that lives in the analytical
/// store and answers "which session is worth replaying?". The replay payload itself (the rrweb DOM-mutation
/// firehose) is NOT here; it is an opaque blob in blob storage, fetched by <see cref="SessionId"/> on demand.
/// </summary>
public sealed class ReplaySession
{
    /// <summary>Marten document id. Distinct from the client-minted <see cref="SessionId"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>The client-minted opaque session UUID (ADR 0010 section 4): not a user, not an IP. This is the
    /// cross-archetype correlation key a backend error carries to link to the exact replay.</summary>
    public required string SessionId { get; set; }

    /// <summary>The trust-root Project this session belongs to (ADR 0007).</summary>
    public required Guid ProjectId { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>The URL the session started on.</summary>
    public string? StartUrl { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>When the most recent chunk arrived; drives duration and "live vs ended" heuristics.</summary>
    public DateTimeOffset LastEventAtUtc { get; set; }

    /// <summary>Number of delivered chunks (ADR 0010 section 2: bursty chunked delivery).</summary>
    public int ChunkCount { get; set; }

    /// <summary>Total rrweb events across all chunks.</summary>
    public int EventCount { get; set; }

    /// <summary>Total stored payload size in bytes (the blob), for cost/retention visibility.</summary>
    public long PayloadBytes { get; set; }

    /// <summary>Searchable attributes (device, page title, custom tags).</summary>
    public Dictionary<string, string> Attributes { get; set; } = [];
}
