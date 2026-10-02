using Tamp.Observer.Domain;

namespace Tamp.Observer.Symbolication;

/// <summary>
/// Symbolicates JavaScript frames via stored Source Map v3 artifacts (ADR 0017). Storage-agnostic: it
/// depends only on <see cref="ISymbolArtifactStore"/>, so it is unit-testable with an in-memory store.
/// </summary>
public sealed class SourceMapSymbolicator(ISymbolArtifactStore artifacts) : ISymbolicator
{
    public async Task<OriginalFrame?> SymbolicateAsync(
        Guid projectId, Guid serviceId, Guid versionId, MinifiedFrame frame, CancellationToken ct = default)
    {
        var artifact = await artifacts.FindAsync(projectId, serviceId, versionId, frame.File, ct);
        if (artifact is null || artifact.Kind != SymbolKind.JavaScriptSourceMap)
            return null;

        SourceMap map;
        try
        {
            map = SourceMap.Parse(artifact.Content);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null; // a corrupt map yields no symbolication, never a crash.
        }

        // Stack frames are 1-based; the source map is 0-based.
        if (!map.TryResolve(frame.Line - 1, frame.Column - 1, out var r))
            return null;

        return new OriginalFrame(r.Source, r.Line + 1, r.Column + 1, r.Name);
    }
}
