using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Postgres;
using Tamp.Observer.Symbolication;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Storage.Postgres.Tests;

/// <summary>
/// Proves symbol artifacts persist and symbolicate end to end over Marten (ADR 0017): a stored source map
/// resolves a minified frame to the original source.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SymbolArtifactStoreTests : IAsyncLifetime
{
    private const string Map = """{"version":3,"file":"app.min.js","sources":["app.js"],"names":["greet"],"mappings":"AAAAA"}""";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private Marten.IDocumentStore _store = null!;
    private MartenSymbolArtifactStore _artifacts = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = ObserverStore.For(_postgres.GetConnectionString());
        _artifacts = new MartenSymbolArtifactStore(_store);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Stored_source_map_symbolicates_a_frame()
    {
        var p = Guid.NewGuid();
        var s = Guid.NewGuid();
        var v = Guid.NewGuid();
        await _artifacts.SaveAsync(new SymbolArtifact
        {
            ProjectId = p,
            ServiceId = s,
            VersionId = v,
            Kind = SymbolKind.JavaScriptSourceMap,
            GeneratedFile = "app.min.js",
            Content = Map,
        });

        var symbolicator = new SourceMapSymbolicator(_artifacts);
        var original = await symbolicator.SymbolicateAsync(p, s, v, new MinifiedFrame("app.min.js", 1, 1));

        Assert.NotNull(original);
        Assert.Equal("app.js", original!.Source);
        Assert.Equal("greet", original.Name);
    }
}
