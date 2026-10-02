using Microsoft.AspNetCore.Authentication.JwtBearer;
using Tamp.Observer.Api;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;
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
builder.Services.AddAuthorization();

// OpenAPI document (ADR 0014): this is the contract the typed TS client is generated from.
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Liveness: anonymous on purpose so an orchestrator can probe without a token.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .WithName("Health");

// The OpenAPI spec the frontend's typed client generates from.
app.MapOpenApi();

var api = app.MapGroup("/api").RequireAuthorization();

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

await app.RunAsync();

/// <summary>The authenticated subject, as asserted by the external IdP (ADR 0013).</summary>
public sealed record MeResponse(string SubjectId, bool Authenticated);

/// <summary>Exposed so the test host (WebApplicationFactory) can boot the real pipeline.</summary>
public partial class Program;
