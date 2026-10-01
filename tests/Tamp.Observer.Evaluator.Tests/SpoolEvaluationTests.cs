using System.Text.Json;
using Google.Protobuf;
using Marten;
using Tamp.Observer.Domain;
using Tamp.Observer.Evaluator;
using Tamp.Observer.Evaluator.Otlp;
using Tamp.Observer.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Observer.Evaluator.Tests;

/// <summary>
/// Proves the evaluator (ADR 0004) consumes the spool end to end: OTLP resource attributes are
/// parsed from landed bytes, a trusted Project gates admission, Service/Environment/Version are
/// discovered and provisioned, and unknown projects are quarantined. Uses OTLP bytes built with the
/// same wire-compatible schema the evaluator reads (field numbers match real collector output).
/// </summary>
public sealed class SpoolEvaluationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private IDocumentStore _store = null!;
    private string _spoolDir = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = ObserverStore.For(_postgres.GetConnectionString());
        _spoolDir = Path.Combine(Path.GetTempPath(), "tobs-spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_spoolDir);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
        try { Directory.Delete(_spoolDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Admits_and_discovers_entities_under_a_trusted_project()
    {
        await SeedProjectAsync("acme");

        LandTraces("acme", service: "checkout-api", version: "2026.10.1+abc123", environment: "prod");

        var evaluator = new IngestEvaluator(_store, new SpoolReader(_spoolDir));
        var stats = await evaluator.DrainAsync();

        Assert.Equal(new DrainStats(Admitted: 1, Quarantined: 0), stats);
        Assert.Empty(Directory.GetFiles(_spoolDir)); // spool consumed

        await using var q = _store.QuerySession();
        var project = await q.Query<Project>().SingleAsync(p => p.Key == "acme");
        var services = await q.Query<Service>().Where(s => s.ProjectId == project.Id).ToListAsync();
        var envs = await q.Query<DeploymentEnvironment>().Where(e => e.ProjectId == project.Id).ToListAsync();
        var versions = await q.Query<ServiceVersion>().ToListAsync();

        Assert.Single(services);
        Assert.Equal("checkout-api", services[0].ServiceName);
        Assert.Single(envs);
        Assert.Equal("prod", envs[0].Name);
        Assert.Single(versions);
        Assert.Equal("2026.10.1+abc123", versions[0].VersionString);
        Assert.Equal(1, versions[0].Sequence); // first version gets sequence 1 (ADR 0008)
        Assert.False(versions[0].IsSynthetic);
    }

    [Fact]
    public async Task Quarantines_unknown_project()
    {
        // No project seeded named "ghost".
        LandTraces("ghost", service: "whatever", version: "1.0", environment: "prod");

        var evaluator = new IngestEvaluator(_store, new SpoolReader(_spoolDir));
        var stats = await evaluator.DrainAsync();

        Assert.Equal(new DrainStats(Admitted: 0, Quarantined: 1), stats);
        Assert.Empty(Directory.GetFiles(_spoolDir));

        await using var q = _store.QuerySession();
        var quarantined = await q.Query<QuarantinedEvent>().ToListAsync();
        Assert.Single(quarantined);
        Assert.Contains("ghost", quarantined[0].Reason);
        Assert.Equal("ghost", quarantined[0].ClaimedProjectKey);
        Assert.Empty(await q.Query<Service>().ToListAsync()); // nothing provisioned
    }

    [Fact]
    public async Task Missing_project_key_is_quarantined()
    {
        LandTraces(projectKey: null, service: "svc", version: "1.0", environment: "prod");

        var evaluator = new IngestEvaluator(_store, new SpoolReader(_spoolDir));
        var stats = await evaluator.DrainAsync();

        Assert.Equal(1, stats.Quarantined);
        await using var q = _store.QuerySession();
        var quarantined = await q.Query<QuarantinedEvent>().SingleAsync();
        Assert.Contains(ResourceKeys.ProjectKey, quarantined.Reason);
    }

    [Fact]
    public async Task Repeated_versions_get_monotonic_sequences_per_service()
    {
        await SeedProjectAsync("acme");
        LandTraces("acme", "api", "v1", "prod");
        LandTraces("acme", "api", "v2", "prod");
        LandTraces("acme", "api", "v1", "prod"); // repeat: should not create a third version

        var evaluator = new IngestEvaluator(_store, new SpoolReader(_spoolDir));
        var stats = await evaluator.DrainAsync();

        Assert.Equal(3, stats.Admitted);
        await using var q = _store.QuerySession();
        var versions = (await q.Query<ServiceVersion>().OrderBy(v => v.Sequence).ToListAsync());
        Assert.Equal(2, versions.Count);
        Assert.Equal(new long[] { 1, 2 }, versions.Select(v => v.Sequence).ToArray());
        Assert.Equal(new[] { "v1", "v2" }, versions.Select(v => v.VersionString).ToArray());
    }

    [Fact]
    public async Task Admit_stores_spans_and_logs_stamped_with_entity_ids()
    {
        await SeedProjectAsync("acme");
        LandTraces("acme", "api", "v1", "prod");
        LandLogs("acme", "api", "v1", "prod", body: "hello from the admit path");

        var evaluator = new IngestEvaluator(_store, new SpoolReader(_spoolDir));
        var stats = await evaluator.DrainAsync();
        Assert.Equal(2, stats.Admitted);

        await using var q = _store.QuerySession();
        var project = await q.Query<Project>().SingleAsync(p => p.Key == "acme");
        var service = await q.Query<Service>().SingleAsync(s => s.ProjectId == project.Id);
        var version = await q.Query<ServiceVersion>().SingleAsync(v => v.ServiceId == service.Id);

        var span = await q.Query<IngestedSpan>().SingleAsync();
        Assert.Equal(service.Id, span.ServiceId);
        Assert.Equal(version.Id, span.VersionId);
        Assert.NotNull(span.EnvironmentId);
        Assert.Equal("GET /checkout", span.Name);
        Assert.Equal(250, span.DurationNano);
        Assert.Equal(1, span.StatusCode);

        var log = await q.Query<IngestedLog>().SingleAsync();
        Assert.Equal(service.Id, log.ServiceId);
        Assert.Equal(version.Id, log.VersionId);
        Assert.Equal("hello from the admit path", log.Body);
        Assert.Equal("INFO", log.SeverityText);
    }

    private async Task SeedProjectAsync(string key)
    {
        await using var s = _store.LightweightSession();
        s.Store(new Project { Key = key, Name = key, CreatedAtUtc = DateTimeOffset.UtcNow });
        await s.SaveChangesAsync();
    }

    private void LandTraces(string? projectKey, string service, string version, string environment)
    {
        var resource = BuildResource(projectKey, service, version, environment);
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(new byte[16]),
            SpanId = ByteString.CopyFrom(new byte[8]),
            Name = "GET /checkout",
            Kind = 2,
            StartTimeUnixNano = 1000,
            EndTimeUnixNano = 1250,
            Status = new Status { Code = 1 },
        };
        var data = new TracesData
        {
            ResourceSpans = { new ResourceSpans { Resource = resource, ScopeSpans = { new ScopeSpans { Spans = { span } } } } },
        };
        Land("traces", data.ToByteArray());
    }

    private void LandLogs(string projectKey, string service, string version, string environment, string body)
    {
        var resource = BuildResource(projectKey, service, version, environment);
        var record = new LogRecord
        {
            TimeUnixNano = 2000,
            SeverityNumber = 9,
            SeverityText = "INFO",
            Body = new AnyValue { StringValue = body },
        };
        var data = new LogsData
        {
            ResourceLogs = { new ResourceLogs { Resource = resource, ScopeLogs = { new ScopeLogs { LogRecords = { record } } } } },
        };
        Land("logs", data.ToByteArray());
    }

    private static Resource BuildResource(string? projectKey, string service, string version, string environment)
    {
        var resource = new Resource();
        if (projectKey is not null)
            resource.Attributes.Add(Attr(ResourceKeys.ProjectKey, projectKey));
        resource.Attributes.Add(Attr(ResourceKeys.ServiceName, service));
        resource.Attributes.Add(Attr(ResourceKeys.ServiceVersion, version));
        resource.Attributes.Add(Attr(ResourceKeys.DeploymentEnvironment, environment));
        return resource;
    }

    private static KeyValue Attr(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };

    private void Land(string signal, byte[] payload)
    {
        var id = Guid.NewGuid().ToString("N");
        var payloadName = $"{id}.{signal}.otlp";
        File.WriteAllBytes(Path.Combine(_spoolDir, payloadName), payload);

        var env = new RawEnvelope
        {
            ReceiptId = id,
            ReceivedAt = DateTimeOffset.UtcNow,
            Source = "otlp",
            Signal = signal,
            Format = "otlp-proto",
            PayloadFile = payloadName,
            PayloadBytes = payload.Length,
        };
        // Envelope last, mirroring the exporter's completeness ordering.
        File.WriteAllBytes(Path.Combine(_spoolDir, $"{id}.{signal}.json"), JsonSerializer.SerializeToUtf8Bytes(env));
    }
}
