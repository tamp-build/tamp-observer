using System.Text.Json;
using StackExchange.Redis;
using Tamp.Observer.RawBucket.Abstractions;

namespace Tamp.Observer.RawBucket.Valkey;

/// <summary>
/// The high-tier raw-bucket provider (ADR 0004 section 2): drains a Valkey Stream with a consumer group
/// (<c>XREADGROUP</c>), delivering undelivered entries oldest-first (stream IDs are time-ordered, so
/// arrival order holds, ADR 0008). Acknowledging does <c>XACK</c> + <c>XDEL</c> so the stream does not
/// grow unbounded. At-least-once: an entry stays pending until acknowledged.
/// </summary>
public sealed class ValkeyRawBucketReader : IRawBucketReader, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _streamKey;
    private readonly string _group;
    private readonly string _consumer;
    private readonly bool _ownsConnection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnectionMultiplexer? _mux;
    private IDatabase? _db;

    public ValkeyRawBucketReader(
        string connectionString,
        string streamKey = ValkeyStream.DefaultStreamKey,
        string consumerGroup = ValkeyStream.DefaultConsumerGroup,
        string? consumerName = null)
    {
        _connectionString = connectionString;
        _streamKey = streamKey;
        _group = consumerGroup;
        _consumer = consumerName ?? Environment.MachineName;
        _ownsConnection = true;
    }

    public async Task<IReadOnlyList<RawBucketItem>> ReadReadyAsync(int maxItems, CancellationToken ct = default)
    {
        var db = await DatabaseAsync(ct);
        // ">" delivers entries not yet handed to this consumer group (undelivered), oldest-first.
        var entries = await db.StreamReadGroupAsync(_streamKey, _group, _consumer, position: ">", count: maxItems);
        if (entries.Length == 0)
            return [];

        var items = new List<RawBucketItem>(entries.Length);
        foreach (var entry in entries)
        {
            var envJson = (string?)entry[ValkeyStream.EnvelopeField];
            if (envJson is null)
                continue;
            var env = JsonSerializer.Deserialize<RawEnvelope>(envJson);
            if (env is null)
                continue;
            var payload = (byte[]?)entry[ValkeyStream.PayloadField] ?? [];
            items.Add(new RawBucketItem(env, payload, entry.Id));
        }
        return items;
    }

    public async Task AcknowledgeAsync(RawBucketItem item, CancellationToken ct = default)
    {
        if (item.Handle is not RedisValue id)
            return;
        var db = await DatabaseAsync(ct);
        await db.StreamAcknowledgeAsync(_streamKey, _group, id);
        await db.StreamDeleteAsync(_streamKey, [id]);
    }

    private async Task<IDatabase> DatabaseAsync(CancellationToken ct)
    {
        if (_db is not null)
            return _db;

        await _gate.WaitAsync(ct);
        try
        {
            if (_db is null)
            {
                _mux = await ConnectionMultiplexer.ConnectAsync(_connectionString);
                _db = _mux.GetDatabase();
                try
                {
                    // Create the group at the start of the stream (consume everything), making the stream if absent.
                    await _db.StreamCreateConsumerGroupAsync(_streamKey, _group, StreamPosition.Beginning, createStream: true);
                }
                catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
                {
                    // Group already exists; fine.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        return _db;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsConnection && _mux is not null)
            await _mux.DisposeAsync();
        _gate.Dispose();
    }
}
