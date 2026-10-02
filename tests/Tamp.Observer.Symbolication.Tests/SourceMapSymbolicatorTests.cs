using Tamp.Observer.Domain;
using Tamp.Observer.Symbolication;
using Xunit;

namespace Tamp.Observer.Symbolication.Tests;

/// <summary>
/// Pure unit tests for source-map symbolication (ADR 0017) with an in-memory artifact store. Fast lane.
/// The map has two generated lines; mappings decode to deterministic positions:
///   line 0 segment [0,0,0,0,0] -> source app.js (0,0) name "greet"
///   line 1 segment [0,0,1,0]   -> source app.js (1,0) no name
/// </summary>
public sealed class SourceMapSymbolicatorTests
{
    private const string Map = """{"version":3,"file":"app.min.js","sources":["app.js"],"names":["greet"],"mappings":"AAAAA;AACA"}""";

    private sealed class InMemoryArtifacts : ISymbolArtifactStore
    {
        public SymbolArtifact? Artifact { get; init; }
        public Task<SymbolArtifact?> FindAsync(Guid p, Guid s, Guid v, string file, CancellationToken ct = default) =>
            Task.FromResult(Artifact is not null && Artifact.GeneratedFile == file ? Artifact : null);
        public Task SaveAsync(SymbolArtifact artifact, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static SymbolArtifact Artifact(string content = Map) => new()
    {
        ProjectId = Guid.NewGuid(),
        ServiceId = Guid.NewGuid(),
        VersionId = Guid.NewGuid(),
        Kind = SymbolKind.JavaScriptSourceMap,
        GeneratedFile = "app.min.js",
        Content = content,
    };

    [Fact]
    public async Task Resolves_first_line_frame_with_name()
    {
        var art = Artifact();
        var sut = new SourceMapSymbolicator(new InMemoryArtifacts { Artifact = art });

        var original = await sut.SymbolicateAsync(art.ProjectId, art.ServiceId, art.VersionId, new MinifiedFrame("app.min.js", 1, 1));

        Assert.NotNull(original);
        Assert.Equal("app.js", original!.Source);
        Assert.Equal(1, original.Line);
        Assert.Equal(1, original.Column);
        Assert.Equal("greet", original.Name);
    }

    [Fact]
    public async Task Resolves_second_line_frame_without_name()
    {
        var art = Artifact();
        var sut = new SourceMapSymbolicator(new InMemoryArtifacts { Artifact = art });

        var original = await sut.SymbolicateAsync(art.ProjectId, art.ServiceId, art.VersionId, new MinifiedFrame("app.min.js", 2, 1));

        Assert.NotNull(original);
        Assert.Equal("app.js", original!.Source);
        Assert.Equal(2, original.Line);
        Assert.Null(original.Name);
    }

    [Fact]
    public async Task Returns_null_when_no_artifact()
    {
        var sut = new SourceMapSymbolicator(new InMemoryArtifacts { Artifact = null });
        Assert.Null(await sut.SymbolicateAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new MinifiedFrame("missing.js", 1, 1)));
    }

    [Fact]
    public async Task Returns_null_on_corrupt_map()
    {
        var art = Artifact(content: "not a source map");
        var sut = new SourceMapSymbolicator(new InMemoryArtifacts { Artifact = art });
        Assert.Null(await sut.SymbolicateAsync(art.ProjectId, art.ServiceId, art.VersionId, new MinifiedFrame("app.min.js", 1, 1)));
    }
}
