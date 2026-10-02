using Marten;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// The authorization chokepoint (ADR 0013) over Marten. Resolves the enforcement gate for the target
/// (ADR 0002: Instance settings plus the target project's mode) and decides via the pure
/// <see cref="AuthorizationDecider"/>. Audit later becomes "emit from here".
/// </summary>
public sealed class MartenAuthorizationService(IDocumentStore store) : IAuthorizationService
{
    public async Task<bool> CheckAsync(string subjectId, Capability capability, ResourceScope target, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();

        var instance = await session.LoadAsync<InstanceSettings>(InstanceSettings.SingletonId, ct)
                       ?? new InstanceSettings();

        EnforcementMode? projectMode = null;
        if (target.ProjectId is Guid projectId)
            projectMode = (await session.LoadAsync<Project>(projectId, ct))?.EnforcementMode;

        var gate = EnforcementGate.Resolve(instance, projectMode);

        var assignments = await session.Query<RoleAssignment>()
            .Where(a => a.SubjectId == subjectId)
            .ToListAsync(ct);
        var anyGrants = await session.Query<RoleAssignment>().AnyAsync(ct);

        return AuthorizationDecider.Decide(assignments, capability, target, anyGrants, gate);
    }
}
