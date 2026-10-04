using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tamp.Observer.Alerting;
using Tamp.Observer.Domain;
using Tamp.Observer.Evaluator;
using Tamp.Observer.RawBucket.Abstractions;
using Tamp.Observer.RawBucket.FileSpool;
using Tamp.Observer.RawBucket.Valkey;
using Tamp.Observer.Storage.Abstractions;
using Tamp.Observer.Storage.ClickHouse;
using Tamp.Observer.Storage.Postgres;

// Runnable evaluator (ADR 0004): drains the raw bucket the Go collector lands into and promotes
// entities into the Postgres/Marten store. Floor config via env vars; no cloud anything.

var connectionString = Environment.GetEnvironmentVariable("OBSERVER_DB")
    ?? "Host=localhost;Port=5432;Database=observer;Username=observer;Password=observer";
var spoolDirectory = Environment.GetEnvironmentVariable("OBSERVER_SPOOL")
    ?? "./_spool";
// Raw-bucket tier dial (ADR 0004 section 2): "file" (floor) or "valkey" (high).
var rawBucketTier = Environment.GetEnvironmentVariable("OBSERVER_RAWBUCKET") ?? "file";
var valkeyConnection = Environment.GetEnvironmentVariable("OBSERVER_VALKEY") ?? "localhost:6379";

// Admin one-shot: create the trust-root Project (ADR 0007: a human creates projects; they are never
// auto-created from telemetry). Usage: create-project <key> [name]
if (args.Length >= 2 && args[0] == "create-project")
{
    var key = args[1];
    var name = args.Length >= 3 ? args[2] : key;
    using var store = ObserverStore.For(connectionString);
    await using var admin = store.LightweightSession();
    admin.Store(new Project { Key = key, Name = name, CreatedAtUtc = DateTimeOffset.UtcNow });
    await admin.SaveChangesAsync();
    Console.WriteLine($"created project '{key}' ({name})");
    return;
}

// Admin one-shot: pre-register an identity on the admission list (ADR 0013). No self-service accounts; an admin
// adds the email here. Usage: allow-user <email> [viewer|editor|admin]
if (args.Length >= 2 && args[0] == "allow-user")
{
    var email = args[1];
    var role = args.Length >= 3 && Enum.TryParse<Role>(args[2], ignoreCase: true, out var r) ? r : Role.Viewer;
    using var store = ObserverStore.For(connectionString);
    await new MartenAllowedIdentityStore(store).AddAsync(email, role);
    Console.WriteLine($"allowed '{AllowedIdentity.Normalize(email)}' as {role}");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTampObserverStore(connectionString);

// Storage-tier write dial (ADR 0005/0006, TOBS-19): the floor writes everything to Postgres. Dialing the
// telemetry tier up to ClickHouse keeps entities/issues in Postgres (the system of record) and fans the
// high-volume spans/logs to ClickHouse via a composite sink. Absent OBSERVER_SINK = postgres floor; the
// loosening to a second tier is explicit, never defaulted on.
var sinkTier = Environment.GetEnvironmentVariable("OBSERVER_SINK") ?? "postgres";
if (sinkTier == "clickhouse")
{
    var clickHouse = Environment.GetEnvironmentVariable("OBSERVER_CLICKHOUSE")
        ?? throw new InvalidOperationException("OBSERVER_SINK=clickhouse requires OBSERVER_CLICKHOUSE (connection string).");
    builder.Services.AddSingleton<IEventSink>(sp => new CompositeTieredEventSink(
        new MartenEventSink(sp.GetRequiredService<IDocumentStore>()),
        new ClickHouseEventSink(clickHouse)));

    // The evaluator owns the telemetry-tier schema (it is the writer): create the tables before draining.
    // Retry briefly so a just-started ClickHouse that is healthy but not yet query-ready does not crash boot.
    for (var attempt = 1; ; attempt++)
    {
        try { await ClickHouseSchema.EnsureAsync(clickHouse); break; }
        catch when (attempt < 15) { await Task.Delay(TimeSpan.FromSeconds(2)); }
    }
}

// The evaluator owns the materialized rollup schema (TOBS-25), as it is the writer. Postgres is always the
// system of record, so ensure the rollup tables exist on every boot, regardless of the telemetry-tier dial.
await RollupSchema.EnsureAsync(connectionString);

builder.Services.AddSingleton<IRawBucketReader>(_ => rawBucketTier switch
{
    "valkey" => new ValkeyRawBucketReader(valkeyConnection),
    _ => new FileSpoolRawBucketReader(spoolDirectory),
});
// Alerting channels (ADR 0016, TOBS-29): configuration is the persisted ChannelSettings document, edited in the
// admin UI and read by the dispatcher on each dispatch so changes take effect without restarting the evaluator.
// Env vars remain a fallback for a fresh install with no saved config. Cloud channels are reachback and get gated
// off under locked/enforcing by the dispatcher's enforcement check.
var alertHttp = new HttpClient();
var envChannels = new List<INotificationChannel>();
if (Environment.GetEnvironmentVariable("OBSERVER_ALERT_SLACK_WEBHOOK") is { Length: > 0 } slackUrl)
    envChannels.Add(new SlackChannel(alertHttp, slackUrl));
if (Environment.GetEnvironmentVariable("OBSERVER_ALERT_TELEGRAM_TOKEN") is { Length: > 0 } tgToken
    && Environment.GetEnvironmentVariable("OBSERVER_ALERT_TELEGRAM_CHAT") is { Length: > 0 } tgChat)
    envChannels.Add(new TelegramChannel(alertHttp, tgToken, tgChat));
if (Environment.GetEnvironmentVariable("OBSERVER_ALERT_SMTP_HOST") is { Length: > 0 } smtpHost
    && Environment.GetEnvironmentVariable("OBSERVER_ALERT_SMTP_FROM") is { Length: > 0 } smtpFrom
    && Environment.GetEnvironmentVariable("OBSERVER_ALERT_SMTP_TO") is { Length: > 0 } smtpTo)
    envChannels.Add(new SmtpChannel(new SmtpChannelOptions { Host = smtpHost, From = smtpFrom, To = smtpTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }));

builder.Services.AddSingleton(alertHttp);
builder.Services.AddSingleton<IAlertDispatcher>(sp =>
    new DbChannelAlertDispatcher(sp.GetRequiredService<IDocumentStore>(), alertHttp, envChannels));
builder.Services.AddSingleton<IngestEvaluator>();
builder.Services.AddHostedService<SpoolIngestWorker>();

await builder.Build().RunAsync();

/// <summary>Live-config alert dispatcher (ADR 0016, TOBS-29): on each dispatch it reads the persisted
/// <see cref="ChannelSettings"/> and builds the enabled channels and routing matrix from it, so admin edits in the
/// UI take effect without restarting the evaluator. Falls back to env-configured channels when the saved config
/// enables none (a fresh install).</summary>
sealed class DbChannelAlertDispatcher(
    IDocumentStore store, HttpClient http, IReadOnlyList<INotificationChannel> envFallback) : IAlertDispatcher
{
    public async Task DispatchAsync(IReadOnlyList<AlertEvent> alerts, IEnforcementGate gate, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var settings = await session.LoadAsync<ChannelSettings>(ChannelSettings.SingletonId, ct);

        IReadOnlyList<INotificationChannel> channels;
        IReadOnlyDictionary<AlertKind, IReadOnlySet<string>>? routing = null;
        if (settings is not null && (settings.Smtp.Enabled || settings.Slack.Enabled || settings.Telegram.Enabled))
        {
            channels = ChannelFactory.BuildEnabled(settings, http);
            var map = ChannelFactory.Routing(settings);
            routing = map.Count > 0 ? map : null; // no saved matrix: deliver every kind to every enabled channel.
        }
        else
        {
            channels = envFallback;
        }

        await new AlertDispatcher(channels, routing: routing).DispatchAsync(alerts, gate, ct);
    }
}
