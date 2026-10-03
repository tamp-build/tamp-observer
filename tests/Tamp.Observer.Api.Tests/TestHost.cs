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
    public const string EmailHeader = "X-Test-Email";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubjectHeader, out var sub) || string.IsNullOrEmpty(sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var email = Request.Headers.TryGetValue(EmailHeader, out var e) && !string.IsNullOrEmpty(e)
            ? e.ToString()
            : $"{sub}@test.local";
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, sub!), new Claim("email", email)], SchemeName);
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

    public Task<IReadOnlyList<IngestedLog>> GetLogsAsync(LogQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IngestedLog>>([]);

    public Task<IssueOccurrence?> GetLatestOccurrenceAsync(Guid projectId, string fingerprint, CancellationToken ct = default) =>
        Task.FromResult<IssueOccurrence?>(null);
}

/// <summary>A controllable admission list: the role drives the caller's capabilities, so allow/deny and
/// capability gating map to HTTP status can be asserted directly.</summary>
public sealed class FakeAllowedIdentityStore(bool allow, Role role = Role.Admin) : IAllowedIdentityStore
{
    public Task<AllowedIdentity?> FindAsync(string email, CancellationToken ct = default) =>
        Task.FromResult(allow ? new AllowedIdentity { Email = email, Role = role } : null);

    public Task AddAsync(string email, Role r, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AllowedIdentity>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AllowedIdentity>>([]);
}

/// <summary>An empty issue store so issue endpoints resolve without a database.</summary>
public sealed class FakeIssueStore : IIssueStore
{
    public Task<IReadOnlyList<Issue>> ListAsync(Guid projectId, IssueStatus? status = null, Guid? serviceId = null, int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Issue>>([]);

    public Task<Issue?> GetAsync(Guid projectId, Guid issueId, CancellationToken ct = default) =>
        Task.FromResult<Issue?>(null);

    public Task<bool> SetStatusAsync(Guid projectId, Guid issueId, IssueStatus status, long? resolvedInVersionSequence = null, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <summary>Boots the real API pipeline with the external IdP and the stores swapped for test doubles. The
/// caller's <paramref name="role"/> sets their capabilities (when admitted).</summary>
public sealed class ApiFactory(bool allowlisted = true, Role role = Role.Admin) : WebApplicationFactory<Program>
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
            services.AddSingleton<IAllowedIdentityStore>(new FakeAllowedIdentityStore(allowlisted, role));
            services.AddSingleton<IIssueStore>(new FakeIssueStore());
        });
    }
}
