using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Tamp.Observer.Api.Tests;

/// <summary>
/// Fast-lane endpoint tests over the real API pipeline (ADR 0014) with the external IdP and the store swapped
/// for test doubles. They pin the two contracts that matter at the HTTP boundary: external authN gates every
/// /api route, and the RBAC chokepoint (ADR 0013) decides allow/deny, mapped to 401/403/200.
/// </summary>
public sealed class ApiTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static string LatencyUrl => $"/api/projects/{Project}/latency?start=0&end=1000";

    [Fact]
    public async Task Health_is_anonymous()
    {
        using var app = new ApiFactory();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Openapi_document_is_served()
    {
        // The frontend's typed TS client is generated from this spec (ADR 0014), so it must exist.
        using var app = new ApiFactory();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Me_requires_authentication()
    {
        using var app = new ApiFactory();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_asserted_subject()
    {
        using var app = new ApiFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, "user-123");

        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");

        Assert.NotNull(me);
        Assert.Equal("user-123", me!.SubjectId);
        Assert.True(me.Admitted);
    }

    [Fact]
    public async Task Protected_read_requires_authentication()
    {
        using var app = new ApiFactory();
        using var client = app.CreateClient();

        var response = await client.GetAsync(LatencyUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_but_not_allowlisted_is_forbidden()
    {
        // A valid token from an identity that was never pre-registered: admitted authN, refused admission.
        using var app = new ApiFactory(allowlisted: false);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, "stranger");

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Capability_gated_route_is_forbidden_without_the_capability()
    {
        // A Viewer is admitted but lacks ManageUsers, so the admin-only user list is refused with 403.
        using var app = new ApiFactory(role: Domain.Role.Viewer);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, "user-123");

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Protected_read_returns_data_when_the_capability_is_held()
    {
        // The default caller is an admitted Admin, so ViewTraces is held and latency returns.
        using var app = new ApiFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, "user-123");

        var latency = await client.GetFromJsonAsync<Storage.Abstractions.LatencyPercentiles>(LatencyUrl);

        Assert.NotNull(latency);
        Assert.Equal(FakeObservabilityStore.Latency.Count, latency!.Count);
        Assert.Equal(FakeObservabilityStore.Latency.P99, latency.P99);
    }
}
