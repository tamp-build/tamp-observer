using Marten;
using Tamp.Observer.Domain;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.Postgres.Tests;

/// <summary>
/// Proves the entity model (ADR 0007) and the Postgres/Marten baseline (ADR 0005/0006) actually hold
/// against a real Postgres: documents persist and round-trip, discovery-under-a-trusted-Project works,
/// natural-key queries resolve, and the natural-key unique indexes (ADR 0008) are enforced.
/// This is the spike's "prove we can do it" evidence.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EntityRoundTripTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = ObserverStore.For(_postgres.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Project_persists_and_round_trips_by_key()
    {
        var project = new Project
        {
            Key = "acme",
            Name = "Acme Corp",
            StorageTier = StorageTier.Postgres,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await using (var session = _store.LightweightSession())
        {
            session.Store(project);
            await session.SaveChangesAsync();
        }

        await using (var query = _store.QuerySession())
        {
            var loaded = await query.Query<Project>().SingleOrDefaultAsync(p => p.Key == "acme");
            Assert.NotNull(loaded);
            Assert.Equal("Acme Corp", loaded!.Name);
            Assert.Equal(StorageTier.Postgres, loaded.StorageTier);
            Assert.Null(loaded.EnforcementMode); // inherits instance default (ADR 0002)
        }
    }

    [Fact]
    public async Task Service_environment_version_are_discovered_under_a_project()
    {
        var project = new Project { Key = "shop", Name = "Shop", CreatedAtUtc = DateTimeOffset.UtcNow };

        await using (var session = _store.LightweightSession())
        {
            session.Store(project);
            await session.SaveChangesAsync();
        }

        var service = new Service
        {
            ProjectId = project.Id,
            ServiceName = "checkout-api",
            FirstSeenAtUtc = DateTimeOffset.UtcNow,
        };
        var environment = new DeploymentEnvironment
        {
            ProjectId = project.Id,
            Name = "prod",
            FirstSeenAtUtc = DateTimeOffset.UtcNow,
        };

        await using (var session = _store.LightweightSession())
        {
            session.Store(service);
            session.Store(environment);
            await session.SaveChangesAsync();

            var version = new ServiceVersion
            {
                ProjectId = project.Id,
                ServiceId = service.Id,
                VersionString = "2026.10.1+abc123",
                Sequence = 1,
                FirstSeenAtUtc = DateTimeOffset.UtcNow,
            };
            session.Store(version);
            await session.SaveChangesAsync();
        }

        await using var query = _store.QuerySession();
        var services = await query.Query<Service>().Where(s => s.ProjectId == project.Id).ToListAsync();
        var versions = await query.Query<ServiceVersion>().Where(v => v.ServiceId == service.Id).ToListAsync();

        Assert.Single(services);
        Assert.Equal("checkout-api", services[0].ServiceName);
        Assert.Single(versions);
        Assert.Equal(1, versions[0].Sequence);
    }

    [Fact]
    public async Task Duplicate_project_key_is_rejected_by_the_unique_index()
    {
        var a = new Project { Key = "dup", Name = "First", CreatedAtUtc = DateTimeOffset.UtcNow };
        var b = new Project { Key = "dup", Name = "Second", CreatedAtUtc = DateTimeOffset.UtcNow };

        await using (var session = _store.LightweightSession())
        {
            session.Store(a);
            await session.SaveChangesAsync();
        }

        await using var second = _store.LightweightSession();
        second.Store(b);
        await Assert.ThrowsAnyAsync<Exception>(async () => await second.SaveChangesAsync());
    }
}
