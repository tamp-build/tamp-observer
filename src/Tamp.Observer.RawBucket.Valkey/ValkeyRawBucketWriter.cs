using System.Text.Json;
using StackExchange.Redis;
using Tamp.Observer.RawBucket.Abstractions;

namespace Tamp.Observer.RawBucket.Valkey;

/// <summary>
/// Lands a raw event into the Valkey Stream (<c>XADD</c>). This is the .NET-side counterpart of the Go
/// landing exporter and the single definition of the stream's field layout; the Go exporter writes the
/// same <see cref="ValkeyStream.EnvelopeField"/> / <see cref="ValkeyStream.PayloadField"/> fields.
/// </summary>
public sealed class ValkeyRawBucketWriter(string connectionString, string streamKey = ValkeyStream.DefaultStreamKey)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnectionMultiplexer? _mux;
    private IDatabase? _db;

    public async Task WriteAsync(RawEnvelope envelope, byte[] payload, CancellationToken ct = default)
    {
        var db = await DatabaseAsync(ct);
        await db.StreamAddAsync(streamKey,
        [
            new NameValueEntry(ValkeyStream.EnvelopeField, JsonSerializer.Serialize(envelope)),
            new NameValueEntry(ValkeyStream.PayloadField, payload),
        ]);
    }

    private async Task<IDatabase> DatabaseAsync(CancellationToken ct)
    {
        if (_db is not null)
            return _db;
        await _gate.WaitAsync(ct);
        try
        {
            _mux ??= await ConnectionMultiplexer.ConnectAsync(connectionString);
            _db ??= _mux.GetDatabase();
        }
        finally
        {
            _gate.Release();
        }
        return _db;
    }

    public async ValueTask DisposeAsync()
    {
        if (_mux is not null)
            await _mux.DisposeAsync();
        _gate.Dispose();
    }
}
