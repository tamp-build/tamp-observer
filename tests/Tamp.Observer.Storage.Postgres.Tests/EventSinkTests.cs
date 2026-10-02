using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;
using Tamp.Observer.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.Postgres.Tests;

/// <summary>Proves the write contract (ADR 0006): a batch persists atomically, and an empty batch is a no-op.</summary>
[Trait("Category", "Integration")]
public sealed class EventSinkTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private IDocumentStore _store = null!;
    private MartenEventSink _sink = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = ObserverStore.For(_postgres.GetConnectionString());
        _sink = new MartenEventSink(_store);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Write_persists_new_entities_and_telemetry()
    {
        var projectId = Guid.NewGuid();
        var service = new Service { Id = Guid.NewGuid(), ProjectId = projectId, ServiceName = "api", FirstSeenAtUtc = DateTimeOffset.UtcNow };
        var version = new ServiceVersion { Id = Guid.NewGuid(), ProjectId = projectId, ServiceId = service.Id, VersionString = "v1", Sequence = 1, FirstSeenAtUtc = DateTimeOffset.UtcNow };
        var span = new IngestedSpan
        {
            ProjectId = projectId, ServiceId = service.Id, VersionId = version.Id,
            TraceId = "t", SpanId = "s", Name = "op", StartUnixNano = 1, EndUnixNano = 6, DurationNano = 5,
            ReceiptId = "r", ReceivedAt = DateTimeOffset.UtcNow,
        };

        await _sink.WriteAsync(new AdmittedBatch([service], [], [version], [span], [], []));

        await using var q = _store.QuerySession();
        Assert.Single(await q.Query<Service>().ToListAsync());
        Assert.Single(await q.Query<ServiceVersion>().ToListAsync());
        Assert.Single(await q.Query<IngestedSpan>().ToListAsync());
    }

    [Fact]
    public async Task Empty_batch_is_a_no_op()
    {
        await _sink.WriteAsync(new AdmittedBatch([], [], [], [], [], []));

        await using var q = _store.QuerySession();
        Assert.Empty(await q.Query<Service>().ToListAsync());
    }
}
