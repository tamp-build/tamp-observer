using Marten;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Evaluator;

/// <summary>
/// Resolves OTLP resource attributes to domain entities, provisioning discovered ones on first sight
/// (ADR 0004 provision-then-admit; ADR 0007 discovery under a trusted Project). Project is the trust
/// root and is never provisioned here: an unknown project key resolves to null and the caller
/// quarantines.
///
/// Note (spike): single-threaded consumer, no resolution cache and no concurrency control yet
/// (ADR 0004/0008 flag those as evaluator work). The natural-key unique indexes are the backstop.
/// </summary>
public sealed class EntityResolver
{
    /// <summary>Find the trust-root Project by its key. Null means unknown (never auto-created).</summary>
    public static Task<Project?> FindProjectByKeyAsync(IQuerySession session, string projectKey, CancellationToken ct) =>
        session.Query<Project>().SingleOrDefaultAsync(p => p.Key == projectKey, ct);

    /// <summary>Resolve a Service by (project, name), provisioning on first sight.</summary>
    public static async Task<Service> ResolveServiceAsync(
        IDocumentSession session, Guid projectId, string serviceName, string? ns,
        DateTimeOffset firstSeen, CancellationToken ct)
    {
        var existing = await session.Query<Service>()
            .SingleOrDefaultAsync(s => s.ProjectId == projectId && s.ServiceName == serviceName, ct);
        if (existing is not null)
            return existing;

        var created = new Service
        {
            ProjectId = projectId,
            ServiceName = serviceName,
            Namespace = ns,
            FirstSeenAtUtc = firstSeen,
        };
        session.Store(created);
        return created;
    }

    /// <summary>Resolve a DeploymentEnvironment by (project, name), provisioning on first sight.</summary>
    public static async Task<DeploymentEnvironment> ResolveEnvironmentAsync(
        IDocumentSession session, Guid projectId, string name,
        DateTimeOffset firstSeen, CancellationToken ct)
    {
        var existing = await session.Query<DeploymentEnvironment>()
            .SingleOrDefaultAsync(e => e.ProjectId == projectId && e.Name == name, ct);
        if (existing is not null)
            return existing;

        var created = new DeploymentEnvironment
        {
            ProjectId = projectId,
            Name = name,
            FirstSeenAtUtc = firstSeen,
        };
        session.Store(created);
        return created;
    }

    /// <summary>
    /// Resolve a ServiceVersion by (service, version string), provisioning on first sight with a
    /// server-assigned monotonic sequence (ADR 0008). Sequence is per-Service, from first-seen order.
    /// </summary>
    public static async Task<ServiceVersion> ResolveVersionAsync(
        IDocumentSession session, Guid projectId, Guid serviceId, string? versionString,
        DateTimeOffset firstSeen, CancellationToken ct)
    {
        var isSynthetic = string.IsNullOrEmpty(versionString);
        var effective = isSynthetic ? Synthetic.UnversionedBuild : versionString!;

        var existing = await session.Query<ServiceVersion>()
            .SingleOrDefaultAsync(v => v.ServiceId == serviceId && v.VersionString == effective, ct);
        if (existing is not null)
            return existing;

        // Next per-service sequence from committed state (first-seen arrival order, ADR 0008).
        var maxSeq = await session.Query<ServiceVersion>()
            .Where(v => v.ServiceId == serviceId)
            .OrderByDescending(v => v.Sequence)
            .Select(v => v.Sequence)
            .FirstOrDefaultAsync(ct);

        var created = new ServiceVersion
        {
            ProjectId = projectId,
            ServiceId = serviceId,
            VersionString = effective,
            Sequence = maxSeq + 1,
            IsSynthetic = isSynthetic,
            FirstSeenAtUtc = firstSeen,
        };
        session.Store(created);
        return created;
    }
}
