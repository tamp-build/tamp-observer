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
// Retention windows (TOBS-24 raw telemetry + TOBS-25 rollup). Raw spans/logs/metrics default to 30 days; the
// rollup is kept longer (default 90) so aggregates outlive the raw events. Used by both the ClickHouse TTL and
// the Postgres prune service below.
var telemetryRetentionDays = int.TryParse(Environment.GetEnvironmentVariable("OBSERVER_TELEMETRY_RETENTION_DAYS"), out var td) && td > 0 ? td : 30;
var rollupRetentionDays = int.TryParse(Environment.GetEnvironmentVariable("OBSERVER_ROLLUP_RETENTION_DAYS"), out var rd) && rd > 0 ? rd : 90;

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

// Admin one-shot: backfill the TOBS-25 rollup from existing spans/logs so historical windows are not blank
// before the rollup started being maintained on admit. Idempotency note: this increments the rollup, so run it
// once against a fresh rollup. Usage: rollup-backfill
if (args.Length >= 1 && args[0] == "rollup-backfill")
{
    using var store = ObserverStore.For(connectionString);
    await RollupSchema.EnsureAsync(connectionString);
    var accum = new RollupAccumulator();
    long spanCount = 0, logCount = 0;
    await using (var q = store.QuerySession())
    {
        await foreach (var s in q.Query<IngestedSpan>().ToAsyncEnumerable())
        {
            accum.AddSpan(s.ProjectId, s.ServiceId, s.Name, s.StartUnixNano, s.DurationNano, s.StatusCode == 2,
                s.Fingerprint, s.Attributes.GetValueOrDefault(ResourceKeys.SessionId),
                s.ReceivedAt.ToUnixTimeMilliseconds() * 1_000_000L);
            spanCount++;
        }
        await foreach (var l in q.Query<IngestedLog>().ToAsyncEnumerable())
        {
            accum.AddLog(l.ProjectId, l.ServiceId, l.TimeUnixNano, l.SeverityNumber >= 17,
                l.Fingerprint, l.Attributes.GetValueOrDefault(ResourceKeys.SessionId),
                l.ReceivedAt.ToUnixTimeMilliseconds() * 1_000_000L);
            logCount++;
        }
    }
    await new MartenEventSink(store).WriteAsync(new AdmittedBatch([], [], [], [], [], [], accum.Build()));
    Console.WriteLine($"rollup-backfill: folded {spanCount} spans + {logCount} logs into the rollup");
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
        try { await ClickHouseSchema.EnsureAsync(clickHouse, telemetryRetentionDays); break; }
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

// Retention (TOBS-24 raw telemetry + TOBS-25 rollup), pruned hourly. Windows are configured at the top; raw
// spans/logs/metrics default to 30 days, the rollup to 90. Issues/entities/auth are never pruned.
builder.Services.AddHostedService(_ => new RetentionService(
    connectionString, TimeSpan.FromDays(telemetryRetentionDays), TimeSpan.FromDays(rollupRetentionDays), TimeSpan.FromHours(1)));

await builder.Build().RunAsync();

/// <summary>Prunes raw telemetry (TOBS-24) and the rollup (TOBS-25) past their retention windows on a fixed
/// interval. Best-effort: a failed prune is swallowed and retried next tick, so it never takes the evaluator
/// down. The rollup window is kept longer so aggregates survive after the raw events are gone.</summary>
sealed class RetentionService(string connectionString, TimeSpan telemetryRetention, TimeSpan rollupRetention, TimeSpan interval) : BackgroundService
{
    private static long CutoffNano(TimeSpan window) => (DateTimeOffset.UtcNow - window).ToUnixTimeMilliseconds() * 1_000_000L;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TelemetryRetention.PruneAsync(connectionString, CutoffNano(telemetryRetention), stoppingToken); }
            catch { /* best-effort; retry next interval */ }
            try { await RollupSchema.PruneAsync(connectionString, CutoffNano(rollupRetention), stoppingToken); }
            catch { /* best-effort; retry next interval */ }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

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
