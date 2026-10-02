using System.Text;
using Tamp.Observer.RawBucket.Abstractions;
using Testcontainers.Redis;
using Xunit;

namespace Tamp.Observer.RawBucket.Valkey.Tests;

/// <summary>
/// Proves the Valkey Streams high tier of the raw-bucket dial (ADR 0004 section 2) behind the same
/// IRawBucketReader interface as the file-spool floor: events written by the landing writer are drained
/// in arrival order via a consumer group, and acknowledging removes them. Runs against a real Valkey
/// via Testcontainers.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ValkeyRawBucketTests : IAsyncLifetime
{
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:8-alpine").Build();
    private string _conn = null!;

    public async Task InitializeAsync()
    {
        await _valkey.StartAsync();
        _conn = _valkey.GetConnectionString();
    }

    public Task DisposeAsync() => _valkey.DisposeAsync().AsTask();

    [Fact]
    public async Task Round_trips_in_arrival_order_then_acks()
    {
        await using (var writer = new ValkeyRawBucketWriter(_conn))
        {
            await writer.WriteAsync(Envelope("r1", "traces"), Encoding.UTF8.GetBytes("payload-1"));
            await writer.WriteAsync(Envelope("r2", "logs"), Encoding.UTF8.GetBytes("payload-2"));
        }

        await using var reader = new ValkeyRawBucketReader(_conn);

        var items = await reader.ReadReadyAsync(10);
        Assert.Equal(2, items.Count);
        Assert.Equal("r1", items[0].Envelope.ReceiptId);
        Assert.Equal("traces", items[0].Envelope.Signal);
        Assert.Equal("payload-1", Encoding.UTF8.GetString(items[0].Payload));
        Assert.Equal("r2", items[1].Envelope.ReceiptId);
        Assert.Equal("payload-2", Encoding.UTF8.GetString(items[1].Payload));

        foreach (var item in items)
            await reader.AcknowledgeAsync(item);

        // After ack the entries are removed; nothing left undelivered.
        var again = await reader.ReadReadyAsync(10);
        Assert.Empty(again);
    }

    private static RawEnvelope Envelope(string receiptId, string signal) => new()
    {
        ReceiptId = receiptId,
        ReceivedAt = DateTimeOffset.UtcNow,
        Source = "otlp",
        Signal = signal,
        Format = "otlp-proto",
        PayloadBytes = 9,
    };
}
