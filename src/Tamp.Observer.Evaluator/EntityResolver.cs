using Marten;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Evaluator;

/// <summary>
/// Per-event entity resolution (ADR 0004 provision-then-admit; ADR 0007 discovery under a trusted
/// Project). Reads existing entities through a query session and assigns ids to newly discovered ones,
/// collecting them so the evaluator can hand a resolved batch to the write sink (ADR 0006). An in-event
/// cache dedupes repeated resolution across resources in the same event.
///
/// Project is the trust root and is never provisioned here: an unknown key resolves to null and the
/// caller quarantines. Single-threaded, no cross-event cache yet; the natural-key unique indexes are the
/// backstop against races (ADR 0004/0008 flag resolution caching/concurrency as later work).
/// </summary>
public sealed class BatchResolver(IQuerySession read)
{
    private readonly List<Service> _newServices = [];
    private readonly List<DeploymentEnvironment> _newEnvironments = [];
    private readonly List<ServiceVersion> _newVersions = [];

    private readonly Dictionary<(Guid Project, string Name), Service> _serviceCache = [];
    private readonly Dictionary<(Guid Project, string Name), DeploymentEnvironment> _environmentCache = [];
    private readonly Dictionary<(Guid Service, string Version), ServiceVersion> _versionCache = [];

    public IReadOnlyList<Service> NewServices => _newServices;
    public IReadOnlyList<DeploymentEnvironment> NewEnvironments => _newEnvironments;
    public IReadOnlyList<ServiceVersion> NewVersions => _newVersions;

    /// <summary>Find the trust-root Project by key. Null means unknown (never auto-created).</summary>
    public Task<Project?> FindProjectByKeyAsync(string projectKey, CancellationToken ct) =>
        read.Query<Project>().SingleOrDefaultAsync(p => p.Key == projectKey, ct);

    public async Task<Service> ResolveServiceAsync(Guid projectId, string serviceName, string? ns, DateTimeOffset firstSeen, CancellationToken ct)
    {
        var key = (projectId, serviceName);
        if (_serviceCache.TryGetValue(key, out var cached))
            return cached;

        var existing = await read.Query<Service>()
            .SingleOrDefaultAsync(s => s.ProjectId == projectId && s.ServiceName == serviceName, ct);
        if (existing is not null)
        {
            _serviceCache[key] = existing;
            return existing;
        }

        var created = new Service
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ServiceName = serviceName,
            Namespace = ns,
            FirstSeenAtUtc = firstSeen,
        };
        _serviceCache[key] = created;
        _newServices.Add(created);
        return created;
    }

    public async Task<DeploymentEnvironment> ResolveEnvironmentAsync(Guid projectId, string name, DateTimeOffset firstSeen, CancellationToken ct)
    {
        var key = (projectId, name);
        if (_environmentCache.TryGetValue(key, out var cached))
            return cached;

        var existing = await read.Query<DeploymentEnvironment>()
            .SingleOrDefaultAsync(e => e.ProjectId == projectId && e.Name == name, ct);
        if (existing is not null)
        {
            _environmentCache[key] = existing;
            return existing;
        }

        var created = new DeploymentEnvironment
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            FirstSeenAtUtc = firstSeen,
        };
        _environmentCache[key] = created;
        _newEnvironments.Add(created);
        return created;
    }

    public async Task<ServiceVersion> ResolveVersionAsync(Guid projectId, Guid serviceId, string? versionString, DateTimeOffset firstSeen, CancellationToken ct)
    {
        var isSynthetic = string.IsNullOrEmpty(versionString);
        var effective = isSynthetic ? Synthetic.UnversionedBuild : versionString!;

        var key = (serviceId, effective);
        if (_versionCache.TryGetValue(key, out var cached))
            return cached;

        var existing = await read.Query<ServiceVersion>()
            .SingleOrDefaultAsync(v => v.ServiceId == serviceId && v.VersionString == effective, ct);
        if (existing is not null)
        {
            _versionCache[key] = existing;
            return existing;
        }

        // Next per-service sequence (ADR 0008): the max of committed state and versions already created
        // for this service within the current event.
        var dbMax = await read.Query<ServiceVersion>()
            .Where(v => v.ServiceId == serviceId)
            .OrderByDescending(v => v.Sequence)
            .Select(v => v.Sequence)
            .FirstOrDefaultAsync(ct);
        var batchMax = _newVersions.Where(v => v.ServiceId == serviceId).Select(v => v.Sequence).DefaultIfEmpty(0).Max();

        var created = new ServiceVersion
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ServiceId = serviceId,
            VersionString = effective,
            Sequence = Math.Max(dbMax, batchMax) + 1,
            IsSynthetic = isSynthetic,
            FirstSeenAtUtc = firstSeen,
        };
        _versionCache[key] = created;
        _newVersions.Add(created);
        return created;
    }
}
