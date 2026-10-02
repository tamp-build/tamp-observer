using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Tamp.Observer.Connector.AspNetCore;

/// <summary>
/// The .NET connector (ADR 0018): thin OpenTelemetry wiring that exports OTLP to the tamp-observer collector,
/// maps the trust-root identity onto the resource, and subscribes to the tamp build sources (tamp ADR 0018) so
/// build telemetry flows in for free. Opt-in: the host calls this only when it wants to emit.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTampObserver(this IServiceCollection services, Action<TampObserverConnectorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TampObserverConnectorOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ProjectKey))
            throw new InvalidOperationException(
                "TampObserver connector requires ProjectKey (the tamp.project.key ingestion identity, ADR 0007).");

        var endpoint = new Uri(options.CollectorEndpoint);

        services.AddOpenTelemetry()
            .ConfigureResource(r => ConfigureResource(r, options))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // tamp ADR 0018: subscribe to the build ActivitySources so a tamp build in-process is observed.
                .AddSource("Tamp.Build*")
                .AddOtlpExporter(e => e.Endpoint = endpoint))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // tamp ADR 0018: the single build Meter.
                .AddMeter("Tamp.Build")
                .AddOtlpExporter(e => e.Endpoint = endpoint))
            .WithLogging(l => l.AddOtlpExporter(e => e.Endpoint = endpoint));

        return services;
    }

    private static void ConfigureResource(ResourceBuilder r, TampObserverConnectorOptions options)
    {
        r.AddService(
            serviceName: options.ServiceName,
            serviceNamespace: options.ServiceNamespace,
            serviceVersion: options.ServiceVersion);

        var attributes = new List<KeyValuePair<string, object>>
        {
            // The trust-root ingestion identity (ADR 0004/0007); absent it, the evaluator quarantines the event.
            new("tamp.project.key", options.ProjectKey),
        };
        if (!string.IsNullOrWhiteSpace(options.DeploymentEnvironment))
            attributes.Add(new("deployment.environment", options.DeploymentEnvironment!));

        r.AddAttributes(attributes);
    }
}
