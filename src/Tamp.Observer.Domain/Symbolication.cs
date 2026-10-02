namespace Tamp.Observer.Domain;

/// <summary>Kind of symbol artifact (ADR 0017). Source maps first; PDB/native are future.</summary>
public enum SymbolKind
{
    JavaScriptSourceMap = 0,
}

/// <summary>
/// An uploaded symbol artifact (ADR 0017), scoped to the exact release it belongs to. For JS this is a
/// Source Map v3; <see cref="Content"/> holds the map JSON inline (a blob tier is the future escape hatch).
/// </summary>
public class SymbolArtifact
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid VersionId { get; set; }

    public SymbolKind Kind { get; set; }

    /// <summary>The generated file the artifact maps, e.g. "app.min.js".</summary>
    public required string GeneratedFile { get; set; }

    /// <summary>The artifact content (for JS, the source map JSON).</summary>
    public required string Content { get; set; }

    public DateTimeOffset UploadedAtUtc { get; set; }
}

/// <summary>A minified stack frame (1-based line/column, as stack traces report).</summary>
public readonly record struct MinifiedFrame(string File, int Line, int Column);

/// <summary>A resolved original frame (1-based line/column).</summary>
public sealed record OriginalFrame(string Source, int Line, int Column, string? Name);

/// <summary>Loads and stores symbol artifacts (ADR 0017).</summary>
public interface ISymbolArtifactStore
{
    Task<SymbolArtifact?> FindAsync(Guid projectId, Guid serviceId, Guid versionId, string generatedFile, CancellationToken ct = default);
    Task SaveAsync(SymbolArtifact artifact, CancellationToken ct = default);
}

/// <summary>Resolves a minified frame to its original source (ADR 0017).</summary>
public interface ISymbolicator
{
    Task<OriginalFrame?> SymbolicateAsync(Guid projectId, Guid serviceId, Guid versionId, MinifiedFrame frame, CancellationToken ct = default);
}
