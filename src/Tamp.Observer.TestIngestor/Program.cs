using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Synthetic data generator (TOBS-23): pumps fake telemetry into a running tamp-observer so the views light up
// without a real app. Emits error spans (StatusCode=Error + exception.type) that group into Issues (ADR 0015),
// some normal spans for latency, and synthetic rrweb replay sessions over the session front door (ADR 0010).
// All carry tamp.project.key (ADR 0007). Config via env.

var otlp = Env("TI_OTLP", "http://localhost:4317");
var replayUrl = Env("TI_REPLAY", "http://localhost:8080/ingest/replay");
var project = Env("TI_PROJECT", "demo");
var environment = Env("TI_ENV", "prod");
var errorsPer = int.Parse(Env("TI_ERRORS", "25"));
var normalPer = int.Parse(Env("TI_NORMAL", "60"));
var sessionCount = int.Parse(Env("TI_SESSIONS", "5"));

const string SourceName = "Tamp.Observer.TestIngestor";
string[] services = ["checkout", "catalog", "payments"];
string[] versions = ["1.2.0", "1.3.0"];
string[] errorTypes =
[
    "System.NullReferenceException",
    "System.TimeoutException",
    "Npgsql.PostgresException",
    "System.InvalidOperationException",
];
string[] ops = ["GET /orders", "POST /orders", "GET /catalog/items", "POST /checkout", "GET /payments/status"];

var rng = new Random(1337);
var source = new ActivitySource(SourceName);
var baseTime = DateTime.UtcNow.AddMinutes(-30);
long spanTotal = 0, errorTotal = 0;

Console.WriteLine($"test ingestor -> OTLP {otlp}, project '{project}', env '{environment}'");

foreach (var svc in services)
{
    foreach (var ver in versions)
    {
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(SourceName)
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: svc, serviceVersion: ver)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>("tamp.project.key", project),
                    new KeyValuePair<string, object>("deployment.environment", environment),
                ]))
            .AddOtlpExporter(o => o.Endpoint = new Uri(otlp))
            .Build();

        for (var i = 0; i < normalPer; i++)
        {
            var start = baseTime.AddSeconds(rng.Next(0, 1800));
            using var a = source.StartActivity(ops[rng.Next(ops.Length)], ActivityKind.Server, default(ActivityContext), startTime: start);
            if (a is null) continue;
            a.SetEndTime(start.AddMilliseconds(rng.Next(5, 350)));
            a.SetStatus(ActivityStatusCode.Ok);
            spanTotal++;
        }

        for (var i = 0; i < errorsPer; i++)
        {
            var type = errorTypes[rng.Next(errorTypes.Length)];
            var start = baseTime.AddSeconds(rng.Next(0, 1800));
            using var a = source.StartActivity(ops[rng.Next(ops.Length)], ActivityKind.Server, default(ActivityContext), startTime: start);
            if (a is null) continue;
            a.SetTag("exception.type", type);
            a.SetTag("exception.message", $"{type} at {svc} v{ver}");
            a.SetStatus(ActivityStatusCode.Error, "synthetic error");
            a.SetEndTime(start.AddMilliseconds(rng.Next(50, 900)));
            spanTotal++;
            errorTotal++;
        }

        provider.ForceFlush(10_000);
    }
}

Console.WriteLine($"emitted {spanTotal} spans ({errorTotal} errors across {errorTypes.Length} types x {services.Length} services x {versions.Length} versions)");

// Synthetic replay sessions over the session front door.
using var http = new HttpClient();
var sessionsSent = 0;
for (var i = 0; i < sessionCount; i++)
{
    var sessionId = Guid.NewGuid().ToString();
    var t0 = DateTimeOffset.UtcNow.AddMinutes(-rng.Next(1, 25)).ToUnixTimeMilliseconds();
    var events = SyntheticRrwebEvents(t0, svc: services[i % services.Length]);
    var body = JsonSerializer.Serialize(new
    {
        sessionId,
        startUrl = "https://demo.local/app",
        userAgent = "TestIngestor/1.0 (synthetic)",
        events,
    });
    using var req = new HttpRequestMessage(HttpMethod.Post, replayUrl)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    req.Headers.Add("X-Tamp-Project-Key", project);
    var resp = await http.SendAsync(req);
    if (resp.IsSuccessStatusCode) sessionsSent++;
    else Console.WriteLine($"replay session {sessionId[..8]} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
}

Console.WriteLine($"posted {sessionsSent}/{sessionCount} replay sessions");
Console.WriteLine("done; allow a few seconds for the evaluator to drain and project Issues.");
return;

static string Env(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

// A minimal but structurally valid rrweb event sequence: Meta, FullSnapshot, then incremental interactions.
static object[] SyntheticRrwebEvents(long t0, string svc) =>
[
    new { type = 4, data = new { href = $"https://demo.local/{svc}", width = 1280, height = 800 }, timestamp = t0 },
    new { type = 2, data = new { node = new { type = 0, id = 1, childNodes = Array.Empty<object>() }, initialOffset = new { top = 0, left = 0 } }, timestamp = t0 + 20 },
    new { type = 3, data = new { source = 1, positions = new[] { new { x = 120, y = 240, id = 1, timeOffset = 0 } } }, timestamp = t0 + 500 },
    new { type = 3, data = new { source = 2, type = 2, id = 1, x = 130, y = 250 }, timestamp = t0 + 1200 },
    new { type = 3, data = new { source = 0, adds = Array.Empty<object>(), removes = Array.Empty<object>() }, timestamp = t0 + 2000 },
];
