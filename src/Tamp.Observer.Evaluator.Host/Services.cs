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

namespace Tamp.Observer.Evaluator.Host;

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
