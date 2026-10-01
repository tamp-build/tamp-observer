using System.Text.Json;
using Tamp.Observer.RawBucket.Abstractions;

namespace Tamp.Observer.RawBucket.FileSpool;

/// <summary>
/// The floor raw-bucket provider (ADR 0004 section 2): reads the spool directory the Go rawfile exporter
/// writes to. An event is ready only when its <c>.json</c> envelope exists (the exporter writes it last),
/// events are delivered oldest-first by received-at (arrival order, ADR 0008), and acknowledging deletes
/// the pair. No infra beyond the filesystem.
/// </summary>
public sealed class FileSpoolRawBucketReader(string spoolDirectory) : IRawBucketReader
{
    private readonly string _dir = spoolDirectory;

    private sealed record FileHandle(string EnvelopePath, string PayloadPath);

    public Task<IReadOnlyList<RawBucketItem>> ReadReadyAsync(int maxItems, CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir))
            return Task.FromResult<IReadOnlyList<RawBucketItem>>([]);

        var items = new List<(DateTimeOffset At, string Receipt, RawBucketItem Item)>();
        foreach (var envPath in Directory.EnumerateFiles(_dir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var env = JsonSerializer.Deserialize<RawEnvelope>(File.ReadAllBytes(envPath));
                if (env is null || string.IsNullOrEmpty(env.PayloadFile))
                    continue;

                var payloadPath = Path.Combine(_dir, env.PayloadFile);
                if (!File.Exists(payloadPath))
                    continue; // payload missing; skip (do not delete the envelope)

                var item = new RawBucketItem(env, File.ReadAllBytes(payloadPath), new FileHandle(envPath, payloadPath));
                items.Add((env.ReceivedAt, env.ReceiptId, item));
            }
            catch (JsonException)
            {
                // A malformed envelope is left for the caller's policy; skip here.
            }
        }

        IReadOnlyList<RawBucketItem> ordered = items
            .OrderBy(i => i.At)
            .ThenBy(i => i.Receipt, StringComparer.Ordinal)
            .Take(maxItems)
            .Select(i => i.Item)
            .ToList();
        return Task.FromResult(ordered);
    }

    public Task AcknowledgeAsync(RawBucketItem item, CancellationToken ct = default)
    {
        if (item.Handle is FileHandle h)
        {
            TryDelete(h.PayloadPath);
            TryDelete(h.EnvelopePath);
        }
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* best effort; a re-run will retry */ }
        catch (UnauthorizedAccessException) { }
    }
}
