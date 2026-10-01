namespace Tamp.Observer.RawBucket.Abstractions;

/// <summary>
/// One ready raw event drained from the bucket: its envelope, payload bytes, and an opaque
/// provider handle the reader uses to acknowledge consumption.
/// </summary>
public sealed record RawBucketItem(RawEnvelope Envelope, byte[] Payload, object Handle);

/// <summary>
/// The drain side of the raw-bucket tier dial (ADR 0004 section 2). The evaluator consumes through this
/// interface; providers (file spool for the floor, Valkey Streams for the high tier) are selected by
/// config, never a code fork. Every provider must deliver events in arrival order (the version sequence
/// depends on it, ADR 0008) and support at-least-once semantics: an item is removed from the bucket only
/// when acknowledged.
/// </summary>
public interface IRawBucketReader
{
    /// <summary>Read up to <paramref name="maxItems"/> ready events, oldest first. Empty when none.</summary>
    Task<IReadOnlyList<RawBucketItem>> ReadReadyAsync(int maxItems, CancellationToken ct = default);

    /// <summary>Acknowledge an item as consumed, removing it from the bucket (delete / XACK).</summary>
    Task AcknowledgeAsync(RawBucketItem item, CancellationToken ct = default);
}
