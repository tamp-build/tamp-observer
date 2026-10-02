using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;
using IAuthorizationService = Tamp.Observer.Domain.IAuthorizationService;

namespace Tamp.Observer.Api.Tests;

/// <summary>
/// A test authentication handler that stands in for the external OIDC IdP (ADR 0013). A request carrying the
/// <c>X-Test-Sub</c> header is treated as authenticated with that subject; without it the request is
/// unauthenticated, so the pipeline answers 401 exactly as it would for a missing bearer token. This keeps
/// the auth contract under test without reaching a real IdP over the network.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string SubjectHeader = "X-Test-Sub";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubjectHeader, out var sub) || string.IsNullOrEmpty(sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, sub!)], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>A canned read store so endpoint/authorization behaviour is tested without a database.</summary>
public sealed class FakeObservabilityStore : IObservabilityStore
{
    public static readonly LatencyPercentiles Latency = new(42, 10, 95, 99);

    public Task<LatencyPercentiles> GetLatencyPercentilesAsync(SpanQuery query, CancellationToken ct = default) =>
        Task.FromResult(Latency);

    public Task<IReadOnlyList<OperationStat>> GetTopOperationsAsync(SpanQuery query, int limit = 10, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OperationStat>>([new OperationStat("GET /", 5, 1)]);

    public Task<TraceView> GetTraceAsync(Guid projectId, string traceId, CancellationToken ct = default) =>
        Task.FromResult(new TraceView([], []));
}

/// <summary>A controllable chokepoint so allow/deny maps to HTTP status can be asserted directly.</summary>
public sealed class FakeAuthorizationService(bool allow) : IAuthorizationService
{
    public Task<bool> CheckAsync(string subjectId, Capability capability, ResourceScope target, CancellationToken ct = default) =>
        Task.FromResult(allow);
}

/// <summary>Boots the real API pipeline with the external IdP and the store swapped for test doubles.</summary>
public sealed class ApiFactory(bool authorize = true) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Swap the external IdP for a header-driven test scheme, and make it the default so the
            // RequireAuthorization() policy authenticates against it.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.AddSingleton<IObservabilityStore>(new FakeObservabilityStore());
            services.AddSingleton<IAuthorizationService>(new FakeAuthorizationService(authorize));
        });
    }
}
