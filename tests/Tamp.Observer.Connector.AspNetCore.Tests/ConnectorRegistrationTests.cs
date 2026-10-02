using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Tamp.Observer.Connector.AspNetCore;
using Xunit;

namespace Tamp.Observer.Connector.AspNetCore.Tests;

/// <summary>
/// Fast unit tests for the connector's registration contract (ADR 0018): the trust-root project key is
/// mandatory (ADR 0007), and with it the OpenTelemetry pipeline registers and resolves. Fast lane.
/// </summary>
public sealed class ConnectorRegistrationTests
{
    [Fact]
    public void Missing_project_key_is_rejected()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddTampObserver(o => { o.ServiceName = "svc"; }));
        Assert.Contains("tamp.project.key", ex.Message);
    }

    [Fact]
    public void Registers_and_resolves_the_tracer_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTampObserver(o =>
        {
            o.ProjectKey = "demo";
            o.ServiceName = "svc";
            o.ServiceVersion = "1.0.0";
            o.DeploymentEnvironment = "test";
            o.CollectorEndpoint = "http://localhost:4317";
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<TracerProvider>());
    }
}
