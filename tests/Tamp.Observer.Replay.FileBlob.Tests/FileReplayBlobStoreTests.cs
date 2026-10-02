using System.Text;
using Tamp.Observer.Replay.FileBlob;
using Xunit;

namespace Tamp.Observer.Replay.FileBlob.Tests;

/// <summary>
/// Fast unit tests for the filesystem replay blob store (ADR 0010): chunks round-trip in arrival order, and a
/// non-UUID session id is rejected so a client string can never escape the storage root. Fast lane.
/// </summary>
public sealed class FileReplayBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tamp-replay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Chunks_round_trip_in_arrival_order()
    {
        var store = new FileReplayBlobStore(_root);
        var project = Guid.NewGuid();
        var session = Guid.NewGuid().ToString();

        await store.AppendChunkAsync(project, session, Encoding.UTF8.GetBytes("[1,2]"));
        await store.AppendChunkAsync(project, session, Encoding.UTF8.GetBytes("[3]"));
        await store.AppendChunkAsync(project, session, Encoding.UTF8.GetBytes("[4,5,6]"));

        var chunks = await store.ReadChunksAsync(project, session);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("[1,2]", Encoding.UTF8.GetString(chunks[0]));
        Assert.Equal("[3]", Encoding.UTF8.GetString(chunks[1]));
        Assert.Equal("[4,5,6]", Encoding.UTF8.GetString(chunks[2]));
    }

    [Fact]
    public async Task Unknown_session_reads_empty()
    {
        var store = new FileReplayBlobStore(_root);
        Assert.Empty(await store.ReadChunksAsync(Guid.NewGuid(), Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task Non_uuid_session_is_rejected()
    {
        var store = new FileReplayBlobStore(_root);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendChunkAsync(Guid.NewGuid(), "../escape", Encoding.UTF8.GetBytes("[]")));
    }
}
