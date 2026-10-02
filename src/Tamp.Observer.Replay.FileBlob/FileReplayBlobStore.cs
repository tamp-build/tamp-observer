using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Replay.FileBlob;

/// <summary>
/// Filesystem implementation of the replay blob store (ADR 0010 section 3), the floor tier. Each delivered
/// chunk is written as its own uniquely-named file under &lt;root&gt;/&lt;projectId&gt;/&lt;sessionId&gt;/, named by
/// arrival time so a lexicographic sort reconstructs chunk order. One file per chunk means bursty concurrent
/// deliveries never contend on a lock or a shared file, and there is no newline-framing concern.
/// </summary>
public sealed class FileReplayBlobStore(string rootDirectory) : IReplayBlobStore
{
    private readonly string _root = rootDirectory;

    public async Task AppendChunkAsync(Guid projectId, string sessionId, ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        var dir = SessionDirectory(projectId, sessionId);
        Directory.CreateDirectory(dir);
        // Ticks prefix orders chunks; the guid suffix makes concurrent arrivals in the same tick collision-free.
        var file = Path.Combine(dir, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(file, chunk.ToArray(), ct);
    }

    public async Task<IReadOnlyList<byte[]>> ReadChunksAsync(Guid projectId, string sessionId, CancellationToken ct = default)
    {
        var dir = SessionDirectory(projectId, sessionId);
        if (!Directory.Exists(dir))
            return [];

        var files = Directory.GetFiles(dir, "*.json");
        Array.Sort(files, StringComparer.Ordinal);

        var chunks = new List<byte[]>(files.Length);
        foreach (var file in files)
            chunks.Add(await File.ReadAllBytesAsync(file, ct));
        return chunks;
    }

    private string SessionDirectory(Guid projectId, string sessionId)
    {
        // The client mints the session id as a UUID (ADR 0010 section 4). Enforce that so it is safe as a path
        // segment: never trust a client string as a directory name.
        if (!Guid.TryParse(sessionId, out _))
            throw new ArgumentException("sessionId must be a UUID.", nameof(sessionId));
        return Path.Combine(_root, projectId.ToString("N"), sessionId);
    }
}
