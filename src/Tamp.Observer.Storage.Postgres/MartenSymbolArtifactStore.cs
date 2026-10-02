using Marten;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>Marten-backed symbol artifact store (ADR 0017): artifacts keyed by project/service/version/file.</summary>
public sealed class MartenSymbolArtifactStore(IDocumentStore store) : ISymbolArtifactStore
{
    public async Task<SymbolArtifact?> FindAsync(Guid projectId, Guid serviceId, Guid versionId, string generatedFile, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<SymbolArtifact>()
            .SingleOrDefaultAsync(
                a => a.ProjectId == projectId && a.ServiceId == serviceId && a.VersionId == versionId && a.GeneratedFile == generatedFile,
                ct);
    }

    public async Task SaveAsync(SymbolArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Id == Guid.Empty)
            artifact.Id = Guid.NewGuid();
        if (artifact.UploadedAtUtc == default)
            artifact.UploadedAtUtc = DateTimeOffset.UtcNow;

        await using var session = store.LightweightSession();
        session.Store(artifact);
        await session.SaveChangesAsync(ct);
    }
}
