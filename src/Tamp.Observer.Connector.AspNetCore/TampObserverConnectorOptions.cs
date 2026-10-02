namespace Tamp.Observer.Connector.AspNetCore;

/// <summary>
/// Configuration for the ASP.NET Core connector (ADR 0018). These values become the OpenTelemetry resource the
/// evaluator keys on: <see cref="ProjectKey"/> is the trust-root ingestion identity (ADR 0007), without which
/// telemetry is quarantined, and the service/version/environment fields are the discovered entity axes.
/// </summary>
public sealed class TampObserverConnectorOptions
{
    /// <summary>The Project key, emitted as the <c>tamp.project.key</c> resource attribute. Required.</summary>
    public string ProjectKey { get; set; } = "";

    /// <summary>The logical service (OTel <c>service.name</c>).</summary>
    public string ServiceName { get; set; } = "unknown-service";

    /// <summary>The area/component (OTel <c>service.namespace</c>), mirroring tamp ADR 0018's Area facet.</summary>
    public string? ServiceNamespace { get; set; }

    /// <summary>The service version (OTel <c>service.version</c>); the correctness/partition axis (ADR 0007).</summary>
    public string? ServiceVersion { get; set; }

    /// <summary>Deployment environment (OTel <c>deployment.environment</c>); the capture-policy scope (ADR 0007).</summary>
    public string? DeploymentEnvironment { get; set; }

    /// <summary>OTLP endpoint of the tamp-observer collector. Defaults to the local floor gRPC port.</summary>
    public string CollectorEndpoint { get; set; } = "http://localhost:4317";
}
