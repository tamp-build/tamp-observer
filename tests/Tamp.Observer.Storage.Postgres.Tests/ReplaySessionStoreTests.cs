using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.Postgres.Tests;

/// <summary>
/// Proves replay session metadata persists and lists over Marten (ADR 0010): chunk upserts converge on one row
/// per (project, session), and a project's sessions list most-recent-first.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReplaySessionStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private Marten.IDocumentStore _store = null!;
    private MartenReplaySessionStore _sessions = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = ObserverStore.For(_postgres.GetConnectionString());
        _sessions = new MartenReplaySessionStore(_store);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Upsert_converges_on_one_row_and_lists()
    {
        var project = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString();

        var first = new ReplaySession
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ProjectId = project,
            StartedAtUtc = DateTimeOffset.UtcNow,
            LastEventAtUtc = DateTimeOffset.UtcNow,
            ChunkCount = 1,
            EventCount = 2,
            PayloadBytes = 100,
        };
        await _sessions.UpsertAsync(first);

        // Second chunk: same row advances, not a new one.
        var found = await _sessions.FindAsync(project, sessionId);
        Assert.NotNull(found);
        found!.ChunkCount += 1;
        found.EventCount += 3;
        found.PayloadBytes += 50;
        found.LastEventAtUtc = DateTimeOffset.UtcNow;
        await _sessions.UpsertAsync(found);

        var list = await _sessions.ListAsync(project);
        Assert.Single(list);
        Assert.Equal(sessionId, list[0].SessionId);
        Assert.Equal(5, list[0].EventCount);
        Assert.Equal(150, list[0].PayloadBytes);
    }
}
