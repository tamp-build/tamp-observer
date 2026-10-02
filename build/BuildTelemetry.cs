using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

/// <summary>
/// Exports the build's own ADR 0018 diagnostics (the Tamp.Build* ActivitySources and the Tamp.Build Meter) to
/// a tamp-observer collector over OTLP, so CI dogfoods the platform with real build telemetry (TOBS-21). Off
/// unless OTEL_EXPORTER_OTLP_ENDPOINT is set, so local builds pay nothing. The OTLP endpoint, protocol, and
/// headers (the Cloudflare Access service-token headers in CI) come from the standard OTEL_EXPORTER_OTLP_*
/// environment variables; this only adds the resource identity the evaluator keys on.
/// </summary>
internal static class BuildTelemetry
{
    public static IDisposable Start()
    {
        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return new NoOp();

        var projectKey = Environment.GetEnvironmentVariable("OBSERVER_BUILD_PROJECT") ?? "tamp";
        var environment = Environment.GetEnvironmentVariable("OBSERVER_BUILD_ENV") ?? "ci";

        ResourceBuilder Resource() => ResourceBuilder.CreateDefault()
            .AddService(serviceName: "tamp-observer-build", serviceNamespace: "build")
            .AddAttributes(
            [
                // Trust-root ingestion identity (ADR 0004/0007); without it the evaluator quarantines the event.
                new KeyValuePair<string, object>("tamp.project.key", projectKey),
                new KeyValuePair<string, object>("deployment.environment", environment),
            ]);

        // AddOtlpExporter() with no arguments reads OTEL_EXPORTER_OTLP_ENDPOINT / _PROTOCOL / _HEADERS.
        var tracer = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(Resource())
            .AddSource("Tamp.Build*")
            .AddOtlpExporter()
            .Build();

        var meter = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(Resource())
            .AddMeter("Tamp.Build")
            .AddOtlpExporter()
            .Build();

        return new Providers(tracer, meter);
    }

    private sealed class NoOp : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Providers(TracerProvider tracer, MeterProvider meter) : IDisposable
    {
        public void Dispose()
        {
            // Flush before the process exits, or the last batch of spans/metrics never ships.
            tracer.ForceFlush(10_000);
            meter.ForceFlush(10_000);
            tracer.Dispose();
            meter.Dispose();
        }
    }
}
