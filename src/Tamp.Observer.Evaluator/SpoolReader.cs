using System.Text.Json;

namespace Tamp.Observer.Evaluator;

/// <summary>One ready raw event read from the spool: its envelope, payload bytes, and file paths.</summary>
public sealed record SpoolItem(
    RawEnvelope Envelope,
    byte[] Payload,
    string EnvelopePath,
    string PayloadPath);

/// <summary>
/// Reads completed events from the spool directory the rawfile exporter writes to (ADR 0004). An
/// event is ready only when its <c>.json</c> envelope exists (the exporter writes the envelope last),
/// so a half-written payload is never consumed.
/// </summary>
public sealed class SpoolReader(string spoolDirectory)
{
    private readonly string _dir = spoolDirectory;

    /// <summary>
    /// The ready events currently in the spool (envelope present), ordered by received-at so the
    /// evaluator processes them in first-seen arrival order. That order is what the monotonic version
    /// sequence is defined against (ADR 0008); filesystem enumeration order is OS-dependent and must
    /// not leak into sequence assignment.
    /// </summary>
    public IEnumerable<SpoolItem> ReadReady()
    {
        if (!Directory.Exists(_dir))
            return [];

        var items = new List<SpoolItem>();
        foreach (var envPath in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var env = JsonSerializer.Deserialize<RawEnvelope>(File.ReadAllBytes(envPath));
                if (env is null || string.IsNullOrEmpty(env.PayloadFile))
                    continue;

                var payloadPath = Path.Combine(_dir, env.PayloadFile);
                if (!File.Exists(payloadPath))
                    continue; // payload missing; skip (do not delete the envelope)

                items.Add(new SpoolItem(env, File.ReadAllBytes(payloadPath), envPath, payloadPath));
            }
            catch (JsonException)
            {
                // A malformed envelope is itself a reject; leave it for the caller's policy.
            }
        }

        // Ties (same received-at) fall back to receipt id for a stable, deterministic order.
        return items
            .OrderBy(i => i.Envelope.ReceivedAt)
            .ThenBy(i => i.Envelope.ReceiptId, StringComparer.Ordinal);
    }

    /// <summary>Remove an event's files from the spool once it has been consumed (admit or quarantine).</summary>
    public static void Remove(SpoolItem item)
    {
        TryDelete(item.PayloadPath);
        TryDelete(item.EnvelopePath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* best effort; a re-run will retry */ }
        catch (UnauthorizedAccessException) { }
    }
}
