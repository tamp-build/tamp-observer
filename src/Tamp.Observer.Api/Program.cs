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
// The SPA's OIDC client id (the IdP's public PKCE client). Distinct from the audience; served to the browser.
var oidcClientId = Environment.GetEnvironmentVariable("OBSERVER_OIDC_CLIENT_ID") ?? "tamp-observer";
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
var selfProject = Environment.GetEnvironmentVariable("OBSERVER_SELF_PROJECT") ?? "tamp-observer";
var selfEnv = Environment.GetEnvironmentVariable("OBSERVER_SELF_ENV") ?? "dev";
// OTLP/HTTP endpoint for relaying browser telemetry (ADR 0018): browsers cannot carry the Access-gated OTLP
// endpoint, so the API relays client events to the collector's HTTP receiver. Default: the gRPC collector host
// on the HTTP port (collectors expose both), overridable explicitly.
var selfOtlpHttp = Environment.GetEnvironmentVariable("OBSERVER_SELF_OTLP_HTTP")
    ?? (!string.IsNullOrWhiteSpace(selfOtlp) ? selfOtlp.Replace(":4317", ":4318") : null);
if (!string.IsNullOrWhiteSpace(selfOtlp))
{
    builder.Services.AddTampObserver(o =>
    {
        o.ProjectKey = selfProject;
        o.ServiceName = "tamp-observer-api";
        o.ServiceNamespace = "backend";
        o.ServiceVersion = "0.1.0-alpha";
        o.DeploymentEnvironment = selfEnv;
        o.CollectorEndpoint = selfOtlp;
    });
}

// HttpClient for the browser-telemetry relay (POST /ingest/client -> collector OTLP/HTTP).
builder.Services.AddHttpClient();

// Read-only Valkey address for the Storage & health page (raw-bucket introspection). Absent on the floor tier.
var valkeyConn = Environment.GetEnvironmentVariable("OBSERVER_VALKEY");

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

// Stamp the browser-minted session id (ADR 0010/0014) onto the server span so a server error correlates back
// to the user's session replay. Runs inside the ASP.NET Core instrumentation Activity, so it exports as a span
// attribute and lands on the stored span.
app.Use(async (ctx, next) =>
{
    var sid = ctx.Request.Headers["X-Tamp-Session-Id"].ToString();
    if (!string.IsNullOrEmpty(sid))
        System.Diagnostics.Activity.Current?.SetTag("tamp.session.id", sid);
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// Liveness: anonymous on purpose so an orchestrator can probe without a token.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .WithName("Health");

// Runtime SPA config (ADR 0013/0014): the frontend fetches its OIDC settings here instead of baking them at
// build time, so one image works across deployments (dev Dex, GitHub-via-Dex, any in-enclave IdP). Anonymous;
// none of these values are secret (they ride in redirects and tokens anyway).
app.MapGet("/config.json", () =>
        Results.Ok(new SpaConfig(
            authority ?? string.Empty,
            audience ?? string.Empty,
            oidcClientId,
            // The project the SPA reports its own errors/replay under; empty disables the client sink (no relay).
            ClientProjectKey: selfOtlpHttp is not null ? selfProject : string.Empty,
            Environment: selfEnv)))
    .AllowAnonymous()
    .WithName("SpaConfig");

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

// Browser telemetry relay (ADR 0018): the SPA captures its own errors and ships them here; browsers cannot hold
// the Access-gated OTLP endpoint, so the API relays them to the collector as OTLP logs (service
// tamp-observer-web) under the project key the browser carries. Correlates to replay by tamp.session.id.
app.MapPost("/ingest/client", async (
        ClientTelemetryRequest body,
        HttpContext http,
        IDocumentStore docs,
        IHttpClientFactory httpFactory,
        CancellationToken ct) =>
    {
        var key = http.Request.Headers["X-Tamp-Project-Key"].ToString();
        if (string.IsNullOrEmpty(key))
            return Results.Unauthorized();
        if (body.Events is null || body.Events.Length == 0)
            return Results.NoContent();
        if (selfOtlpHttp is null)
            return Results.Accepted(); // relay not configured; drop quietly so the floor is unaffected

        await using var qs = docs.QuerySession();
        var project = await qs.Query<Project>().Where(p => p.Key == key).FirstOrDefaultAsync(ct);
        if (project is null)
            return Results.Unauthorized();

        var payload = ClientTelemetry.BuildOtlpLogs(key, "tamp-observer-web", selfEnv, body);
        var client = httpFactory.CreateClient();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            var res = await client.PostAsync($"{selfOtlpHttp.TrimEnd('/')}/v1/logs", content, ct);
            return res.IsSuccessStatusCode ? Results.Accepted() : Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    })
    .AllowAnonymous()
    .WithName("IngestClient");

var api = app.MapGroup("/api").RequireAuthorization(AllowlistedHandler.PolicyName);

// Whoami + access: who you are and what you can do. Drives the UI's capability gating (ADR 0013) so the
// frontend never shows an action that would 403.
api.MapGet("/me", async (HttpContext http, IAllowedIdentityStore allow, CancellationToken ct) =>
    {
        var (role, capabilities) = await HttpAuthorization.AccessAsync(http, allow, ct);
        return Results.Ok(new MeResponse(
            http.User.SubjectId() ?? string.Empty,
            http.User.Email() ?? string.Empty,
            role is not null,
            role?.ToString(),
            capabilities.Select(c => c.ToString()).OrderBy(x => x).ToArray()));
    })
    .WithName("Me")
    .Produces<MeResponse>();

// Projects the viewer can access (project switcher). Instance-role MVP: an admitted user sees all projects;
// per-project scoping is future work on the same seam.
api.MapGet("/projects", async (IDocumentStore docs, CancellationToken ct) =>
    {
        await using var session = docs.QuerySession();
        var projects = await session.Query<Project>().OrderBy(p => p.Name).ToListAsync(ct);
        return projects.Select(p => new ProjectSummary(p.Id, p.Key, p.Name)).ToList();
    })
    .WithName("ListProjects")
    .Produces<IReadOnlyList<ProjectSummary>>();

// Services discovered within a project (ADR 0007). Reference data the UI resolves ServiceId -> name against
// for logs, traces and operations. Admission is enough; no extra capability beyond seeing the project.
api.MapGet("/projects/{projectId:guid}/services", async (Guid projectId, IDocumentStore docs, CancellationToken ct) =>
    {
        await using var session = docs.QuerySession();
        var services = await session.Query<Service>()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.ServiceName)
            .ToListAsync(ct);
        return services.Select(s => new ServiceSummary(s.Id, s.ServiceName, s.Namespace)).ToList();
    })
    .WithName("ListServices")
    .Produces<IReadOnlyList<ServiceSummary>>();

// Enforcement posture (the mode badge + explainer, ADR 0002).
api.MapGet("/enforcement", async (IDocumentStore docs, CancellationToken ct) =>
    {
        await using var session = docs.QuerySession();
        var settings = await session.LoadAsync<InstanceSettings>(InstanceSettings.SingletonId, ct) ?? new InstanceSettings();
        return new EnforcementView(settings.EnforcementMode.ToString(), settings.Locked);
    })
    .WithName("Enforcement")
    .Produces<EnforcementView>();

// Notification channel catalog + policy gating (ADR 0016). Enablement/config is managed on the evaluator today;
// this reports the supported channels, their reachback nature, and whether the current mode permits them.
api.MapGet("/channels", async (HttpContext http, IAllowedIdentityStore allow, IDocumentStore docs, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.AdministerInstance, ct);
        if (denied is not null)
            return denied;
        await using var session = docs.QuerySession();
        var settings = await session.LoadAsync<InstanceSettings>(InstanceSettings.SingletonId, ct) ?? new InstanceSettings();
        var enforcing = settings.EnforcementMode == EnforcementMode.Enforcing;
        ChannelView Channel(string type, bool reachback) => new(type, reachback, !(reachback && enforcing));
        return Results.Ok(new[] { Channel("smtp", false), Channel("telegram", true), Channel("slack", true) });
    })
    .WithName("Channels")
    .Produces<ChannelView[]>()
    .Produces(StatusCodes.Status403Forbidden);

// Admission list (Users & roles). ManageUsers only.
api.MapGet("/users", async (HttpContext http, IAllowedIdentityStore allow, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ManageUsers, ct);
        if (denied is not null)
            return denied;
        var users = await allow.ListAsync(ct);
        return Results.Ok(users.Select(u => new UserView(u.Email, u.Role.ToString(), u.CreatedAtUtc)).ToList());
    })
    .WithName("ListUsers")
    .Produces<IReadOnlyList<UserView>>()
    .Produces(StatusCodes.Status403Forbidden);

api.MapPost("/users/allow", async (AllowUserRequest body, HttpContext http, IAllowedIdentityStore allow, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ManageUsers, ct);
        if (denied is not null)
            return denied;
        if (string.IsNullOrWhiteSpace(body.Email))
            return Results.BadRequest("email required");
        var role = Enum.TryParse<Role>(body.Role, ignoreCase: true, out var r) ? r : Role.Viewer;
        await allow.AddAsync(body.Email, role, ct);
        return Results.Ok(new UserView(AllowedIdentity.Normalize(body.Email), role.ToString(), DateTimeOffset.UtcNow));
    })
    .WithName("AllowUser")
    .Produces<UserView>()
    .Produces(StatusCodes.Status403Forbidden);

// Storage/health (what tiers this instance runs). AdministerInstance only. The API knows its read store and
// whether ClickHouse is configured; the write-side tiers live on the evaluator.
api.MapGet("/health/storage", async (HttpContext http, IAllowedIdentityStore allow, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.AdministerInstance, ct);
        if (denied is not null)
            return denied;
        var readStore = Environment.GetEnvironmentVariable("OBSERVER_STORE") ?? "postgres";
        var clickHouse = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OBSERVER_CLICKHOUSE"));
        return Results.Ok(new StorageHealth("postgres", readStore, clickHouse));
    })
    .WithName("StorageHealth")
    .Produces<StorageHealth>()
    .Produces(StatusCodes.Status403Forbidden);

// Full Storage & health aggregation (ADR 0014, README 7.8): live Valkey + Postgres + pipeline + components.
// AdministerInstance only. Poll from the UI while the tab is visible.
api.MapGet("/health/overview", async (HttpContext http, IAllowedIdentityStore allow, IDocumentStore docs, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.AdministerInstance, ct);
        if (denied is not null)
            return denied;
        var clickHouse = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OBSERVER_CLICKHOUSE"));
        var view = await HealthReport.BuildAsync(connectionString, valkeyConn, storeTier, clickHouse, docs, ct);
        return Results.Ok(view);
    })
    .WithName("HealthOverview")
    .Produces<HealthView>()
    .Produces(StatusCodes.Status403Forbidden);

// Latency percentiles for a project window (read interface, ADR 0006). Guarded by ViewTraces at the project
// scope through the chokepoint (ADR 0013).
api.MapGet("/projects/{projectId:guid}/latency", async (
        Guid projectId,
        long start,
        long end,
        Guid? service,
        HttpContext http,
        IAllowedIdentityStore allow,
        IObservabilityStore store,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewTraces, ct);
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

// Span volume + error time series over the window, bucketed (TOBS-25). Drives the overview error-rate chart
// and request/error sparklines. ViewTraces.
api.MapGet("/projects/{projectId:guid}/series", async (
        Guid projectId, long start, long end, Guid? service, int? buckets,
        HttpContext http, IAllowedIdentityStore allow, IObservabilityStore store, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewTraces, ct);
        if (denied is not null)
            return denied;
        var series = await store.GetSpanSeriesAsync(
            new SpanQuery(projectId, new TimeWindow(start, end), service), buckets ?? 48, ct);
        return Results.Ok(series);
    })
    .WithName("ProjectSeries")
    .Produces<IReadOnlyList<SeriesBucket>>()
    .Produces(StatusCodes.Status403Forbidden);

// Per-Issue occurrence series + session counts over the window (TOBS-25). Drives per-issue sparklines and
// session counts on the Issues list and Overview. ViewErrors.
api.MapGet("/projects/{projectId:guid}/issues/series", async (
        Guid projectId, long start, long end, int? buckets,
        HttpContext http, IAllowedIdentityStore allow, IObservabilityStore store, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewErrors, ct);
        if (denied is not null)
            return denied;
        var series = await store.GetIssueSeriesAsync(projectId, new TimeWindow(start, end), buckets ?? 24, ct);
        return Results.Ok(series);
    })
    .WithName("IssueSeries")
    .Produces<IReadOnlyList<IssueSeries>>()
    .Produces(StatusCodes.Status403Forbidden);

// Top operations by frequency with error counts over the window (read interface, ADR 0006).
api.MapGet("/projects/{projectId:guid}/operations", async (
        Guid projectId,
        long start,
        long end,
        Guid? service,
        int limit,
        HttpContext http,
        IAllowedIdentityStore allow,
        IObservabilityStore store,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewTraces, ct);
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
        IAllowedIdentityStore allow,
        IObservabilityStore store,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewTraces, ct);
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
        IAllowedIdentityStore allow,
        IReplaySessionStore sessions,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewReplay, ct);
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
        IAllowedIdentityStore allow,
        IReplayBlobStore blobs,
        CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewReplay, ct);
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

// Issues: the error-grouping surface (ADR 0015). ViewErrors.
api.MapGet("/projects/{projectId:guid}/issues", async (
        Guid projectId, string? status, Guid? service, int? limit,
        HttpContext http, IAllowedIdentityStore allow, IIssueStore issues, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewErrors, ct);
        if (denied is not null)
            return denied;
        IssueStatus? st = Enum.TryParse<IssueStatus>(status, ignoreCase: true, out var parsed) ? parsed : null;
        var list = await issues.ListAsync(projectId, st, service, limit ?? 100, ct);
        return Results.Ok(list);
    })
    .WithName("ListIssues")
    .Produces<IReadOnlyList<Issue>>()
    .Produces(StatusCodes.Status403Forbidden);

api.MapGet("/projects/{projectId:guid}/issues/{issueId:guid}", async (
        Guid projectId, Guid issueId,
        HttpContext http, IAllowedIdentityStore allow, IIssueStore issues, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewErrors, ct);
        if (denied is not null)
            return denied;
        var issue = await issues.GetAsync(projectId, issueId, ct);
        return issue is null ? Results.NotFound() : Results.Ok(issue);
    })
    .WithName("GetIssue")
    .Produces<Issue>()
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status403Forbidden);

// Issue counts by status (the Issues tab badges + Overview open-issue tile). ViewErrors.
api.MapGet("/projects/{projectId:guid}/issues/counts", async (
        Guid projectId, HttpContext http, IAllowedIdentityStore allow, IDocumentStore docs, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewErrors, ct);
        if (denied is not null)
            return denied;
        await using var s = docs.QuerySession();
        var statuses = await s.Query<Issue>().Where(i => i.ProjectId == projectId).Select(i => i.Status).ToListAsync(ct);
        return Results.Ok(new IssueCounts(
            statuses.Count(x => x == IssueStatus.Unresolved),
            statuses.Count(x => x == IssueStatus.Regressed),
            statuses.Count(x => x == IssueStatus.Resolved),
            statuses.Count(x => x == IssueStatus.Ignored),
            statuses.Count));
    })
    .WithName("IssueCounts")
    .Produces<IssueCounts>()
    .Produces(StatusCodes.Status403Forbidden);

// Correlation walk (ADR 0014): from an Issue to its latest occurrence's trace, logs-on-trace and session
// replay. The product centerpiece; the occurrence is linked by the fingerprint stamped at ingest.
api.MapGet("/projects/{projectId:guid}/issues/{issueId:guid}/correlation", async (
        Guid projectId, Guid issueId,
        HttpContext http, IAllowedIdentityStore allow,
        IIssueStore issues, IObservabilityStore store, IReplaySessionStore replays, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewErrors, ct);
        if (denied is not null)
            return denied;
        var issue = await issues.GetAsync(projectId, issueId, ct);
        if (issue is null)
            return Results.NotFound();

        var occ = await store.GetLatestOccurrenceAsync(projectId, issue.Fingerprint, ct);
        var view = await CorrelationBuilder.BuildAsync(
            projectId, occ, issue.Id, issue.ErrorType ?? issue.Title, store, replays, ct);
        return Results.Ok(view);
    })
    .WithName("IssueCorrelation")
    .Produces<CorrelationView>()
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status403Forbidden);

// Correlation anchored on a trace (the trace page's reverse link back to the issue + session). ViewTraces.
api.MapGet("/projects/{projectId:guid}/traces/{traceId}/correlation", async (
        Guid projectId, string traceId,
        HttpContext http, IAllowedIdentityStore allow,
        IIssueStore issues, IObservabilityStore store, IReplaySessionStore replays, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewTraces, ct);
        if (denied is not null)
            return denied;
        var occ = await store.GetLatestOccurrenceByTraceAsync(projectId, traceId, ct);
        var (issueId, errorType) = await ResolveIssue(issues, projectId, occ, ct);
        var view = await CorrelationBuilder.BuildAsync(projectId, occ, issueId, errorType, store, replays, ct);
        return Results.Ok(view);
    })
    .WithName("TraceCorrelation")
    .Produces<CorrelationView>()
    .Produces(StatusCodes.Status403Forbidden);

// Correlation anchored on a session (the replay page's reverse link back to the issue + trace). ViewReplay.
api.MapGet("/projects/{projectId:guid}/sessions/{sessionId}/correlation", async (
        Guid projectId, string sessionId,
        HttpContext http, IAllowedIdentityStore allow,
        IIssueStore issues, IObservabilityStore store, IReplaySessionStore replays, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewReplay, ct);
        if (denied is not null)
            return denied;
        var occ = await store.GetLatestOccurrenceBySessionAsync(projectId, sessionId, ct);
        var (issueId, errorType) = await ResolveIssue(issues, projectId, occ, ct);
        // Even with no error occurrence, the walk still anchors on the session so the replay card renders.
        occ ??= new IssueOccurrence("session", 0, null, null, sessionId, Guid.Empty, null);
        var view = await CorrelationBuilder.BuildAsync(projectId, occ, issueId, errorType, store, replays, ct);
        return Results.Ok(view);
    })
    .WithName("SessionCorrelation")
    .Produces<CorrelationView>()
    .Produces(StatusCodes.Status403Forbidden);

static async Task<(Guid? IssueId, string? ErrorType)> ResolveIssue(
    IIssueStore issues, Guid projectId, IssueOccurrence? occ, CancellationToken ct)
{
    if (occ?.Fingerprint is not { } fp)
        return (null, null);
    var issue = await issues.GetByFingerprintAsync(projectId, fp, ct);
    return issue is null ? (null, null) : (issue.Id, issue.ErrorType ?? issue.Title);
}

// Issue triage (resolve / reopen / ignore). Editor+ (EditCapturePolicy is the Editor-tier verb today).
api.MapPost("/projects/{projectId:guid}/issues/{issueId:guid}/status", async (
        Guid projectId, Guid issueId, IssueStatusRequest body,
        HttpContext http, IAllowedIdentityStore allow, IIssueStore issues, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.EditCapturePolicy, ct);
        if (denied is not null)
            return denied;
        if (!Enum.TryParse<IssueStatus>(body.Status, ignoreCase: true, out var st))
            return Results.BadRequest("status must be one of: unresolved, resolved, ignored, regressed");
        var ok = await issues.SetStatusAsync(projectId, issueId, st, body.ResolvedInVersionSequence, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    })
    .WithName("SetIssueStatus")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status403Forbidden);

// Logs explorer (read interface, ADR 0006). ViewLogs.
api.MapGet("/projects/{projectId:guid}/logs", async (
        Guid projectId, long start, long end, Guid? service, int? minSeverity, int? limit,
        HttpContext http, IAllowedIdentityStore allow, IObservabilityStore store, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewLogs, ct);
        if (denied is not null)
            return denied;
        var logs = await store.GetLogsAsync(
            new LogQuery(projectId, new TimeWindow(start, end), service, minSeverity, limit ?? 200), ct);
        return Results.Ok(logs);
    })
    .WithName("ProjectLogs")
    .Produces<IReadOnlyList<IngestedLog>>()
    .Produces(StatusCodes.Status403Forbidden);

// Alerts (ADR 0016). ViewErrors. Built-in rules fire on new/regressed issues today; spike/threshold/heartbeat
// rules and persisted history are future work, so history is empty for now.
api.MapGet("/projects/{projectId:guid}/alerts", async (
        Guid projectId, HttpContext http, IAllowedIdentityStore allow, CancellationToken ct) =>
    {
        var denied = await HttpAuthorization.RequireAsync(http, allow, Capability.ViewErrors, ct);
        if (denied is not null)
            return denied;
        var rules = new[]
        {
            new AlertRuleView("new-issue", "New issue", true),
            new AlertRuleView("regressed-issue", "Regressed issue", true),
        };
        return Results.Ok(new AlertsView(rules, Array.Empty<AlertEventView>()));
    })
    .WithName("ProjectAlerts")
    .Produces<AlertsView>()
    .Produces(StatusCodes.Status403Forbidden);

// Client-side routes (deep links into the SPA) fall back to index.html. API/health/openapi routes are
// already matched above, so this only catches unmatched GETs; it is a no-op when wwwroot is absent.
app.MapFallbackToFile("index.html");

await app.RunAsync();

/// <summary>Who the caller is and what they can do (ADR 0013): the subject + email the IdP asserted, whether
/// they are admitted, their role, and the capability verbs that drive the UI's gating.</summary>
public sealed record MeResponse(
    string SubjectId, string Email, bool Admitted, string? Role, IReadOnlyList<string> Capabilities);

/// <summary>A project in the switcher.</summary>
public sealed record ProjectSummary(Guid Id, string Key, string Name);

public sealed record ServiceSummary(Guid Id, string ServiceName, string? Namespace);

public sealed record IssueCounts(int Unresolved, int Regressed, int Resolved, int Muted, int Total);

// Correlation walk (ADR 0014): the thread linking an error occurrence to its trace, logs and session. Resolvable
// from any anchor (issue, trace or session), so the same view drives the walk on every surface. Fields are
// nullable because any leg of the thread may be absent (a client error has no server trace; a worker job has no
// session).
public sealed record CorrelationView(
    Guid? IssueId, string? ErrorType, long? AtUnixNano, string? TraceId, string? SpanId, string? SessionId,
    TraceSummary? Trace, LogsOnTrace? Logs, ReplayLink? Replay);
public sealed record TraceSummary(
    string RootOperation, long DurationNano, int SpanCount, int ServiceCount, int ErrorSpanCount);
public sealed record LogsOnTrace(int Total, int ErrorCount, int WarnCount, string? TopMessage);
public sealed record ReplayLink(string SessionId, bool Available, int EventCount);

/// <summary>Assembles a <see cref="CorrelationView"/> from a resolved occurrence, enriching the trace summary,
/// logs-on-trace and session replay. Shared by the issue/trace/session correlation endpoints (ADR 0014).</summary>
public static class CorrelationBuilder
{
    public static async Task<CorrelationView> BuildAsync(
        Guid projectId, IssueOccurrence? occ, Guid? issueId, string? errorType,
        IObservabilityStore store, IReplaySessionStore replays, CancellationToken ct)
    {
        if (occ is null)
            return new CorrelationView(issueId, errorType, null, null, null, null, null, null, null);

        TraceSummary? trace = null;
        LogsOnTrace? logs = null;
        if (!string.IsNullOrEmpty(occ.TraceId))
        {
            var view = await store.GetTraceAsync(projectId, occ.TraceId, ct);
            if (view.Spans.Count > 0)
            {
                var minStart = view.Spans.Min(s => s.StartUnixNano);
                var maxEnd = view.Spans.Max(s => s.EndUnixNano);
                // The earliest-starting span is the most robust root for display (a partial trace may be missing
                // the true root's parent link).
                var root = view.Spans.OrderBy(s => s.StartUnixNano).First();
                trace = new TraceSummary(
                    root.Name, maxEnd - minStart, view.Spans.Count,
                    view.Spans.Select(s => s.ServiceId).Distinct().Count(),
                    view.Spans.Count(s => s.StatusCode == 2));
            }
            if (view.Logs.Count > 0)
            {
                var top = view.Logs.Where(l => l.SeverityNumber >= 17).Select(l => l.Body).FirstOrDefault(b => !string.IsNullOrEmpty(b))
                          ?? view.Logs.Select(l => l.Body).FirstOrDefault(b => !string.IsNullOrEmpty(b));
                logs = new LogsOnTrace(
                    view.Logs.Count,
                    view.Logs.Count(l => l.SeverityNumber >= 17),
                    view.Logs.Count(l => l.SeverityNumber is >= 13 and < 17),
                    top);
            }
        }

        ReplayLink? replay = null;
        if (!string.IsNullOrEmpty(occ.SessionId))
        {
            var session = await replays.FindAsync(projectId, occ.SessionId, ct);
            replay = new ReplayLink(occ.SessionId, session is not null, session?.EventCount ?? 0);
        }

        return new CorrelationView(
            issueId, errorType, occ.AtUnixNano, occ.TraceId, occ.SpanId, occ.SessionId, trace, logs, replay);
    }
}

/// <summary>The instance enforcement posture (ADR 0002).</summary>
public sealed record EnforcementView(string Mode, bool Locked);

/// <summary>A notification channel's nature and whether the current mode permits it (ADR 0016).</summary>
public sealed record ChannelView(string Type, bool Reachback, bool AllowedUnderMode);

/// <summary>A pre-registered identity (ADR 0013).</summary>
public sealed record UserView(string Email, string Role, DateTimeOffset CreatedAtUtc);

/// <summary>Pre-register an email with a role.</summary>
public sealed record AllowUserRequest(string Email, string? Role);

/// <summary>Which storage tiers this instance runs (ADR 0005).</summary>
public sealed record StorageHealth(string WriteStore, string ReadStore, bool ClickHouseConfigured);

/// <summary>Set an issue's status (ADR 0015).</summary>
public sealed record IssueStatusRequest(string Status, long? ResolvedInVersionSequence);

/// <summary>An alert rule (ADR 0016).</summary>
public sealed record AlertRuleView(string Id, string Name, bool Enabled);

/// <summary>A fired alert (history; not persisted yet).</summary>
public sealed record AlertEventView(string Kind, Guid IssueId, string Title, DateTimeOffset AtUtc);

/// <summary>Alert rules + recent history for a project.</summary>
public sealed record AlertsView(IReadOnlyList<AlertRuleView> Rules, IReadOnlyList<AlertEventView> History);

/// <summary>Runtime OIDC settings the SPA fetches at startup (ADR 0014), so the image is deployment-portable.</summary>
public sealed record SpaConfig(
    string OidcAuthority, string OidcAudience, string OidcClientId, string ClientProjectKey, string Environment);

/// <summary>One browser-captured event the SPA ships to <c>/ingest/client</c>.</summary>
public sealed record ClientEvent(
    string? Kind, string? Level, string Message, string? Url, int? Status, string? Stack, long? AtUnixMs);

/// <summary>A batch of browser events plus the session context used to correlate them (ADR 0010/0018).</summary>
public sealed record ClientTelemetryRequest(string? SessionId, string? UserAgent, ClientEvent[] Events);

/// <summary>Builds OTLP/JSON logs from a browser telemetry batch so the normal collector->evaluator->store path
/// ingests them like any other logs. String-valued attributes keep the JSON robust across the int64 wire rules.</summary>
public static class ClientTelemetry
{
    public static string BuildOtlpLogs(string projectKey, string service, string env, ClientTelemetryRequest body)
    {
        static object Attr(string k, string v) => new { key = k, value = new { stringValue = v } };
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var records = (body.Events ?? []).Select(e =>
        {
            var attrs = new List<object>();
            if (!string.IsNullOrEmpty(body.SessionId)) attrs.Add(Attr("tamp.session.id", body.SessionId!));
            if (!string.IsNullOrEmpty(e.Kind)) attrs.Add(Attr("client.kind", e.Kind!));
            if (!string.IsNullOrEmpty(e.Url)) attrs.Add(Attr("url.full", e.Url!));
            if (e.Status is int s) attrs.Add(Attr("http.response.status_code", s.ToString()));
            if (!string.IsNullOrEmpty(e.Stack)) attrs.Add(Attr("exception.stacktrace", e.Stack!));
            if (!string.IsNullOrEmpty(body.UserAgent)) attrs.Add(Attr("user_agent.original", body.UserAgent!));

            var warn = string.Equals(e.Level, "warn", StringComparison.OrdinalIgnoreCase);
            return new
            {
                timeUnixNano = ((e.AtUnixMs ?? nowMs) * 1_000_000L).ToString(),
                severityNumber = warn ? 13 : 17,
                severityText = warn ? "WARN" : "ERROR",
                body = new { stringValue = e.Message ?? string.Empty },
                attributes = attrs,
            };
        }).ToList();

        var doc = new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = new[]
                        {
                            Attr("tamp.project.key", projectKey),
                            Attr("service.name", service),
                            Attr("deployment.environment", env),
                        },
                    },
                    scopeLogs = new[]
                    {
                        new { scope = new { name = "tamp-observer-web" }, logRecords = records },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(doc);
    }
}

/// <summary>One delivered replay chunk from the browser SDK (ADR 0010): a batch of rrweb events plus the
/// session identity and first-chunk context. <see cref="Events"/> is the raw rrweb events JSON array.</summary>
public sealed record ReplayChunkRequest(string SessionId, string? StartUrl, string? UserAgent, JsonElement Events);

/// <summary>Exposed so the test host (WebApplicationFactory) can boot the real pipeline.</summary>
public partial class Program;
