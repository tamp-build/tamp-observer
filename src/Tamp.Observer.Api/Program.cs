using System.Text;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Observer.Api;
using Tamp.Observer.Connector.AspNetCore;
using Tamp.Observer.Domain;
using Tamp.Observer.Replay.FileBlob;
using Tamp.Observer.Storage.Abstractions;
using Tamp.Observer.Storage.ClickHouse;
using Tamp.Observer.Storage.DuckDb;
using Tamp.Observer.Storage.Postgres;
using IAuthorizationService = Tamp.Observer.Domain.IAuthorizationService;

// The read-facing HTTP API (ADR 0014): a plain .NET OpenAPI surface that a generated typed TS client (the
// Svelte frontend) consumes. Every read routes through the RBAC chokepoint (ADR 0013). Floor config is env
// vars only; no cloud anything, and authN is always external (no local credential store).

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("OBSERVER_DB")
    ?? "Host=localhost;Port=5432;Database=observer;Username=observer;Password=observer";

// Read interface + authorization chokepoint come from the Postgres baseline tier (ADR 0005/0006/0013).
builder.Services.AddTampObserverStore(connectionString);

// Storage-tier read dial (ADR 0005/0006, TOBS-19): the floor reads from Postgres. DuckDB is an in-process
// accelerator that reads the same Postgres (no extra service); ClickHouse reads its own columnar tier (and
// requires the evaluator to be writing there via OBSERVER_SINK=clickhouse). Absent OBSERVER_STORE = postgres
// floor; only the Postgres read path is on by default, the analytical tiers are opt-in.
var storeTier = Environment.GetEnvironmentVariable("OBSERVER_STORE") ?? "postgres";
switch (storeTier)
{
    case "postgres":
        break;
    case "duckdb":
        builder.Services.AddSingleton<IObservabilityStore>(new DuckDbObservabilityStore(connectionString));
        break;
    case "clickhouse":
        var clickHouse = Environment.GetEnvironmentVariable("OBSERVER_CLICKHOUSE")
            ?? throw new InvalidOperationException("OBSERVER_STORE=clickhouse requires OBSERVER_CLICKHOUSE (connection string).");
        builder.Services.AddSingleton<IObservabilityStore>(new ClickHouseObservabilityStore(clickHouse));
        break;
    default:
        throw new InvalidOperationException($"OBSERVER_STORE '{storeTier}' is not one of: postgres, duckdb, clickhouse.");
}

// AuthN: always external OIDC (ADR 0013). A configurable authority serves both the GitHub-OIDC MVP
// (connected/dev) and the definable in-enclave IdP (Keycloak/AD) that air-gapped installs require. When no
// authority is configured the scheme still registers, so every protected route answers 401 rather than
// starting up insecure.
var authority = Environment.GetEnvironmentVariable("OBSERVER_OIDC_AUTHORITY");
var audience = Environment.GetEnvironmentVariable("OBSERVER_OIDC_AUDIENCE");
var requireHttps = Environment.GetEnvironmentVariable("OBSERVER_OIDC_ALLOW_HTTP") != "1";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        if (!string.IsNullOrWhiteSpace(authority))
            options.Authority = authority;
        if (!string.IsNullOrWhiteSpace(audience))
        {
            options.Audience = audience;
            options.TokenValidationParameters.ValidateAudience = true;
        }
        options.RequireHttpsMetadata = requireHttps;
    });

// AuthZ: every /api route requires an authenticated user whose email is on the admission list (ADR 0013).
// Authentication proves identity; the allowlist decides admission (no self-service accounts).
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AllowlistedHandler.PolicyName, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new AllowlistedRequirement());
    });
builder.Services.AddSingleton<IAuthorizationHandler, AllowlistedHandler>();

// Replay payload blob store (ADR 0010): the filesystem floor; object storage slots in behind the interface.
var replayBlobDir = Environment.GetEnvironmentVariable("OBSERVER_REPLAY_BLOB") ?? "./_replay";
builder.Services.AddSingleton<IReplayBlobStore>(new FileReplayBlobStore(replayBlobDir));

// Self-telemetry (ADR 0018, TOBS-21): dogfood the API's own runtime telemetry into tamp-observer via the .NET
// connector. Off unless a collector endpoint is configured, so the floor is unaffected.
var selfOtlp = Environment.GetEnvironmentVariable("OBSERVER_SELF_OTLP");
if (!string.IsNullOrWhiteSpace(selfOtlp))
{
    builder.Services.AddTampObserver(o =>
    {
        o.ProjectKey = Environment.GetEnvironmentVariable("OBSERVER_SELF_PROJECT") ?? "tamp-observer";
        o.ServiceName = "tamp-observer-api";
        o.ServiceNamespace = "backend";
        o.ServiceVersion = "0.1.0-alpha";
        o.DeploymentEnvironment = Environment.GetEnvironmentVariable("OBSERVER_SELF_ENV") ?? "dev";
        o.CollectorEndpoint = selfOtlp;
    });
}

// OpenAPI document (ADR 0014): this is the contract the typed TS client is generated from.
builder.Services.AddOpenApi();

var app = builder.Build();

// Diagnostic one-shot: run a single read through whichever store the dial selected (OBSERVER_STORE), so an
// operator can confirm a storage-tier variant actually works in this environment (e.g. DuckDB installing its
// postgres extension, or ClickHouse reachability) without needing an IdP token to call the HTTP API. Usage:
//   dotnet Tamp.Observer.Api.dll selftest-store [projectId]
if (args.Length >= 1 && args[0] == "selftest-store")
{
    var store = app.Services.GetRequiredService<IObservabilityStore>();
    var projectId = args.Length >= 2 && Guid.TryParse(args[1], out var p) ? p : Guid.Empty;
    var result = await store.GetLatencyPercentilesAsync(
        new SpanQuery(projectId, new TimeWindow(0, long.MaxValue)));
    Console.WriteLine($"store selftest ok: engine={storeTier} project={projectId} span_count={result.Count}");
    return;
}

// Serve the built Svelte SPA (ADR 0014) from wwwroot so one host serves UI and API (the single-host floor,
// ADR 0001). In dev there is no wwwroot and these are no-ops; the Vite dev server proxies to the API instead.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Liveness: anonymous on purpose so an orchestrator can probe without a token.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .WithName("Health");

// The OpenAPI spec the frontend's typed client generates from.
app.MapOpenApi();

// The session/replay front door (ADR 0009/0010): a separate ingestion path from OTLP, authed by the project
// key the browser SDK holds (not an OIDC user token), accepting bursty rrweb event chunks over the session's
// life. Metadata advances in the analytical store; the event firehose goes to the blob store.
app.MapPost("/ingest/replay", async (
        ReplayChunkRequest body,
        HttpContext http,
        IDocumentStore docs,
        IReplayBlobStore blobs,
        IReplaySessionStore sessions,
        CancellationToken ct) =>
    {
        var key = http.Request.Headers["X-Tamp-Project-Key"].ToString();
        if (string.IsNullOrEmpty(key))
            return Results.Unauthorized();
        if (!Guid.TryParse(body.SessionId, out _))
            return Results.BadRequest("sessionId must be a UUID");
        if (body.Events.ValueKind != JsonValueKind.Array)
            return Results.BadRequest("events must be a JSON array");

        await using var qs = docs.QuerySession();
        var project = await qs.Query<Project>().Where(p => p.Key == key).FirstOrDefaultAsync(ct);
        if (project is null)
            return Results.Unauthorized();

        var raw = Encoding.UTF8.GetBytes(body.Events.GetRawText());
        await blobs.AppendChunkAsync(project.Id, body.SessionId, raw, ct);

        var now = DateTimeOffset.UtcNow;
        var session = await sessions.FindAsync(project.Id, body.SessionId, ct)
            ?? new ReplaySession
            {
                Id = Guid.NewGuid(),
                SessionId = body.SessionId,
                ProjectId = project.Id,
                StartedAtUtc = now,
                StartUrl = body.StartUrl,
                UserAgent = body.UserAgent,
            };
        session.LastEventAtUtc = now;
        session.ChunkCount += 1;
        session.EventCount += body.Events.GetArrayLength();
        session.PayloadBytes += raw.LongLength;
        await sessions.UpsertAsync(session, ct);

        return Results.Accepted();
    })
    .AllowAnonymous()
    .WithName("IngestReplay");

var api = app.MapGroup("/api").RequireAuthorization(AllowlistedHandler.PolicyName);

// Whoami: proves external authN end to end without touching the store. No capability needed beyond being
// authenticated; it reports the subject the IdP asserted.
api.MapGet("/me", (HttpContext http) =>
        Results.Ok(new MeResponse(http.User.SubjectId() ?? string.Empty, true)))
    .WithName("Me")
    .Produces<MeResponse>();

// Latency percentiles for a project window (read interface, ADR 0006). Guarded by ViewTraces at the project
// scope through the chokepoint (ADR 0013).
api.MapGet("/projects/{projectId:guid}/latency", async (
        Guid projectId,
        long start,
        long end,
        Guid? service,
        HttpContext http,
        IAuthorizationService authz,
        IObservabilityStore store,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(
            http, authz, Capability.ViewTraces, ResourceScope.ForProject(projectId), ct);
        if (denied is not null)
            return denied;

        var result = await store.GetLatencyPercentilesAsync(
            new SpanQuery(projectId, new TimeWindow(start, end), service), ct);
        return Results.Ok(result);
    })
    .WithName("ProjectLatency")
    .Produces<LatencyPercentiles>()
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden);

// Top operations by frequency with error counts over the window (read interface, ADR 0006).
api.MapGet("/projects/{projectId:guid}/operations", async (
        Guid projectId,
        long start,
        long end,
        Guid? service,
        int limit,
        HttpContext http,
        IAuthorizationService authz,
        IObservabilityStore store,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(
            http, authz, Capability.ViewTraces, ResourceScope.ForProject(projectId), ct);
        if (denied is not null)
            return denied;

        var result = await store.GetTopOperationsAsync(
            new SpanQuery(projectId, new TimeWindow(start, end), service), limit <= 0 ? 10 : limit, ct);
        return Results.Ok(result);
    })
    .WithName("ProjectOperations")
    .Produces<IReadOnlyList<OperationStat>>()
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden);

// The correlation walk: all spans and logs sharing a trace id within a project (read interface, ADR 0006).
api.MapGet("/projects/{projectId:guid}/traces/{traceId}", async (
        Guid projectId,
        string traceId,
        HttpContext http,
        IAuthorizationService authz,
        IObservabilityStore store,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(
            http, authz, Capability.ViewTraces, ResourceScope.ForProject(projectId), ct);
        if (denied is not null)
            return denied;

        var result = await store.GetTraceAsync(projectId, traceId, ct);
        return Results.Ok(result);
    })
    .WithName("ProjectTrace")
    .Produces<TraceView>()
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden);

// Session list: the metadata index that answers "which session is worth replaying?" (ADR 0010). ViewReplay.
api.MapGet("/projects/{projectId:guid}/sessions", async (
        Guid projectId,
        int? limit,
        HttpContext http,
        IAuthorizationService authz,
        IReplaySessionStore sessions,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(
            http, authz, Capability.ViewReplay, ResourceScope.ForProject(projectId), ct);
        if (denied is not null)
            return denied;

        return Results.Ok(await sessions.ListAsync(projectId, limit ?? 50, ct));
    })
    .WithName("ListReplaySessions")
    .Produces<IReadOnlyList<ReplaySessionSummary>>()
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden);

// Replay payload: merge the session's stored chunks into one rrweb-events array for the player (ADR 0010).
api.MapGet("/projects/{projectId:guid}/sessions/{sessionId}/events", async (
        Guid projectId,
        string sessionId,
        HttpContext http,
        IAuthorizationService authz,
        IReplayBlobStore blobs,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(
            http, authz, Capability.ViewReplay, ResourceScope.ForProject(projectId), ct);
        if (denied is not null)
            return denied;

        var chunks = await blobs.ReadChunksAsync(projectId, sessionId, ct);
        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var chunk in chunks)
            {
                using var doc = JsonDocument.Parse(chunk);
                foreach (var element in doc.RootElement.EnumerateArray())
                    element.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        return Results.Bytes(ms.ToArray(), "application/json");
    })
    .WithName("ReplaySessionEvents")
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden);

// Client-side routes (deep links into the SPA) fall back to index.html. API/health/openapi routes are
// already matched above, so this only catches unmatched GETs; it is a no-op when wwwroot is absent.
app.MapFallbackToFile("index.html");

await app.RunAsync();

/// <summary>The authenticated subject, as asserted by the external IdP (ADR 0013).</summary>
public sealed record MeResponse(string SubjectId, bool Authenticated);

/// <summary>One delivered replay chunk from the browser SDK (ADR 0010): a batch of rrweb events plus the
/// session identity and first-chunk context. <see cref="Events"/> is the raw rrweb events JSON array.</summary>
public sealed record ReplayChunkRequest(string SessionId, string? StartUrl, string? UserAgent, JsonElement Events);

/// <summary>Exposed so the test host (WebApplicationFactory) can boot the real pipeline.</summary>
public partial class Program;
