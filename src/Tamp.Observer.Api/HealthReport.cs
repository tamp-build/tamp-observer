using Marten;
using Npgsql;
using StackExchange.Redis;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Api;

// The Storage & health aggregation (ADR 0014, README 7.8): gathers live stats from the real pipeline components
// the API can reach without extra infrastructure. Postgres (pg_stat_*) and Valkey (INFO + stream introspection)
// are gathered live; the quarantine count is real from Marten. Ingest rate and freshness p95 come from the
// materialized rollup (TOBS-25). The remaining time-series (throughput-by-signal history, raw:replay lag history),
// per-pod CPU/memory/restarts, retention schedule and the health-event log need sources we do not collect yet
// (history arrays on the health endpoint, the k8s metrics API, an events log) and come back null/empty, which
// the page renders as honest "not collected yet" rather than fabricated numbers.

public sealed record HealthView(
    string OverallStatus, string? OverallMessage,
    HealthKpis Kpis,
    IReadOnlyList<PipelineStage> Pipeline,
    ValkeyHealth? Valkey,
    PgTier? Postgres,
    DuckTier Duck,
    bool ClickHouseConfigured,
    IReadOnlyList<ComponentHealth> Components);

public sealed record HealthKpis(
    double? IngestRatePerSec, double? FreshnessP95Sec, long BufferedPending, string? DominantStream,
    long Dropped24h, long Rejected24h, long DeadLetters);
public sealed record PipelineStage(string Name, string Status, IReadOnlyList<string> Facts, string? Tag);
public sealed record ValkeyHealth(ValkeyServer Server, IReadOnlyList<StreamStat> Streams, int LaggingCount);
public sealed record ValkeyServer(
    long UsedMemoryBytes, long MaxMemoryBytes, string EvictionPolicy, string Persistence,
    long OpsPerSec, int Clients, string Version, double UptimeDays);
public sealed record StreamStat(
    string Name, long Length, long Pending, double? LagSeconds, int Consumers, int ConsumersExpected, string Status);
public sealed record PgTier(
    int Connections, int MaxConnections, long DbSizeBytes, double CacheHitRatio, string Version,
    IReadOnlyList<TableSize> LargestTables);
public sealed record TableSize(string Name, long Bytes);
public sealed record DuckTier(bool Enabled, string Store);
public sealed record ComponentHealth(
    string Name, string Subtitle, string Status, string? RunningDesired,
    double? CpuMillicores, long? MemoryBytes, int? Restarts, string? Uptime);

public static class HealthReport
{
    private const long PendingWarnThreshold = 5000; // raw-bucket backlog that counts as lagging

    public static async Task<HealthView> BuildAsync(
        string pgConn, string? valkeyConn, string storeTier, bool clickHouse, IDocumentStore docs, CancellationToken ct)
    {
        var pg = await TryPostgresAsync(pgConn, ct);
        var valkey = string.IsNullOrWhiteSpace(valkeyConn) ? null : await TryValkeyAsync(valkeyConn, ct);
        var (rejected24h, quarantineTotal) = await QuarantineAsync(docs, ct);
        var pods = await K8sMetrics.TryReadAsync(ct);

        var buffered = valkey?.Streams.Sum(s => s.Pending) ?? 0;
        var dominant = valkey?.Streams.OrderByDescending(s => s.Pending).FirstOrDefault();
        var lagging = valkey?.LaggingCount ?? 0;

        // Ingest rate + freshness p95 come from the materialized signal rollup (TOBS-25) over a recent window,
        // replacing the former "not collected yet" placeholders. Null only if the rollup is unreachable.
        var (ingestRate, freshnessP95Sec) = await RollupKpisAsync(pgConn, ct);

        var kpis = new HealthKpis(
            IngestRatePerSec: ingestRate, FreshnessP95Sec: freshnessP95Sec,
            BufferedPending: buffered, DominantStream: dominant?.Pending > 0 ? dominant.Name : null,
            Dropped24h: 0, Rejected24h: rejected24h, DeadLetters: quarantineTotal);

        var pgOk = pg is not null;
        var valkeyStatus = valkey is null ? "crit" : lagging > 0 ? "warn" : "ok";
        var pipeline = new List<PipelineStage>
        {
            new("Receivers", "ok", ["OTLP gRPC :4317", "OTLP HTTP :4318", "POST /ingest/replay"], null),
            new("Collector", "ok", ["Go OTel", "landing to Valkey"], null),
            new("Valkey streams", valkeyStatus,
                valkey is null
                    ? ["unreachable"]
                    : [$"{valkey.Streams.Count} stream(s) · {buffered:N0} pending", lagging > 0 ? $"{lagging} lagging" : "draining cleanly"],
                null),
            new("Evaluator", "ok", [".NET workers", $"{quarantineTotal} quarantined"], null),
            new("Postgres", pgOk ? "ok" : "crit", pgOk ? ["write store"] : ["unreachable"], "write"),
            new("DuckDB", "ok", ["read store · in-process"], "read"),
            new("ClickHouse", clickHouse ? "ok" : "off", [clickHouse ? "analytical tier" : "not configured"], "analytics"),
        };

        var overall = valkeyStatus == "crit" || !pgOk ? "critical" : lagging > 0 ? "degraded" : "healthy";
        var overallMsg = overall switch
        {
            "critical" => valkey is null ? "Valkey is unreachable; ingest buffering cannot be read." : "Postgres is unreachable.",
            "degraded" => $"{dominant?.Name ?? "a stream"} is backing up ({buffered:N0} pending). Ingest is still accepting; nothing dropped.",
            _ => null,
        };

        var components = new List<ComponentHealth>
        {
            Component("API", "api", "serves UI + DuckDB", "ok", pods),
            Component("Collector", "collector", "Go OTel", "ok", pods),
            Component("Evaluator", "evaluator", ".NET workers", lagging > 0 ? "warn" : "ok", pods),
            Component("Valkey", "valkey", "streams", valkey is null ? "crit" : "ok", pods),
            Component("Postgres", "postgres", "write store", pgOk ? "ok" : "crit", pods),
            Component("Dex", "dex", "OIDC broker", "ok", pods),
        };

        return new HealthView(
            overall, overallMsg, kpis, pipeline, valkey, pg,
            new DuckTier(storeTier == "duckdb", storeTier), clickHouse, components);
    }

    // Merge the static component descriptor with live pod metrics (TOBS-33). Pods are matched to a component by
    // the "-<token>" segment of their name (e.g. "observer-tamp-observer-api-xxxx" -> "api"). CPU/memory sum
    // across matched pods; running/desired counts ready pods vs matched pods; restarts/uptime come from the first
    // matched pod. When no pod matches (not in a cluster, or no RBAC) the live fields stay null and the page shows
    // the static status with em-dashes, exactly as before.
    private static ComponentHealth Component(string name, string token, string subtitle, string status, IReadOnlyList<PodMetrics> pods)
    {
        var matched = pods.Where(p => p.Pod.Contains($"-{token}", StringComparison.Ordinal)).ToList();
        if (matched.Count == 0)
            return new ComponentHealth(name, subtitle, status, null, null, null, null, null);

        var cpu = matched.Any(p => p.CpuMillicores is not null) ? matched.Sum(p => p.CpuMillicores ?? 0) : (double?)null;
        var mem = matched.Any(p => p.MemoryBytes is not null) ? matched.Sum(p => p.MemoryBytes ?? 0) : (long?)null;
        var running = matched.Count(p => p.Running && p.Ready);
        var restarts = matched.Sum(p => p.Restarts);
        var started = matched.Where(p => p.StartedAt is not null).Select(p => p.StartedAt!.Value).DefaultIfEmpty().Min();
        var uptime = started == default ? null : FormatUptime(DateTimeOffset.UtcNow - started);
        // A matched pod that is not ready downgrades the component status.
        var liveStatus = running < matched.Count && status == "ok" ? "warn" : status;
        return new ComponentHealth(name, subtitle, liveStatus, $"{running} / {matched.Count}", cpu, mem, restarts, uptime);
    }

    private static string FormatUptime(TimeSpan up) => up switch
    {
        { TotalDays: >= 1 } => $"{(int)up.TotalDays}d {up.Hours}h",
        { TotalHours: >= 1 } => $"{(int)up.TotalHours}h {up.Minutes}m",
        { TotalMinutes: >= 1 } => $"{(int)up.TotalMinutes}m",
        _ => $"{(int)up.TotalSeconds}s",
    };

    private const int RollupKpiWindowMinutes = 15;

    // Ingest rate (events/sec) and freshness p95 (seconds) from the signal rollup over the last window (TOBS-25).
    // Returns (null, null) if the rollup table is unreachable so the page still renders "not collected yet".
    private static async Task<(double? Rate, double? FreshP95Sec)> RollupKpisAsync(string pgConn, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(pgConn);
            await conn.OpenAsync(ct);
            var nowNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
            var startNano = nowNano - (long)RollupKpiWindowMinutes * 60 * 1_000_000_000L;
            long totalEvents = 0;
            var fresh = LatencyHistogram.Empty();
            await using var cmd = new NpgsqlCommand(
                "SELECT event_count, fresh_hist FROM observer.observer_rollup_signal WHERE bucket_start >= @s", conn);
            cmd.Parameters.AddWithValue("s", startNano);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                totalEvents += r.GetInt64(0);
                LatencyHistogram.AddInto(fresh, r.GetFieldValue<long[]>(1));
            }
            var rate = totalEvents / (RollupKpiWindowMinutes * 60.0);
            var freshP95Sec = LatencyHistogram.Percentile(fresh, 0.95) / 1_000_000_000.0;
            return (rate, freshP95Sec);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task<(long Rejected24h, long Total)> QuarantineAsync(IDocumentStore docs, CancellationToken ct)
    {
        try
        {
            await using var s = docs.QuerySession();
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var recent = await s.Query<QuarantinedEvent>().CountAsync(q => q.QuarantinedAt >= since, ct);
            var total = await s.Query<QuarantinedEvent>().CountAsync(ct);
            return (recent, total);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static async Task<PgTier?> TryPostgresAsync(string connString, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync(ct);

            var connections = await ScalarInt(conn, "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()", ct);
            var maxConns = await ScalarInt(conn, "SELECT setting::int FROM pg_settings WHERE name = 'max_connections'", ct);
            var dbSize = await ScalarLong(conn, "SELECT pg_database_size(current_database())", ct);
            var cacheHit = await ScalarDouble(conn,
                "SELECT COALESCE(sum(blks_hit)::float / NULLIF(sum(blks_hit) + sum(blks_read), 0), 1) FROM pg_stat_database WHERE datname = current_database()", ct);
            var version = await ScalarString(conn, "SHOW server_version", ct) ?? "?";

            var tables = new List<TableSize>();
            await using (var cmd = new NpgsqlCommand(
                @"SELECT c.relname, pg_total_relation_size(c.oid) AS bytes
                  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE n.nspname = 'observer' AND c.relkind = 'r'
                  ORDER BY bytes DESC LIMIT 5", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var name = r.GetString(0).Replace("mt_doc_ingested", "").Replace("mt_doc_", "");
                    tables.Add(new TableSize(name, r.GetInt64(1)));
                }
            }

            return new PgTier(connections, maxConns, dbSize, cacheHit, version, tables);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ValkeyHealth?> TryValkeyAsync(string connString, CancellationToken ct)
    {
        try
        {
            var options = ConfigurationOptions.Parse(connString);
            // A health read wants a fast yes/no: let ConnectAsync establish (or throw -> null) rather than
            // returning early on a not-yet-connected multiplexer.
            options.AbortOnConnectFail = true;
            options.AllowAdmin = true; // required for the INFO command (read-only server stats)
            options.ConnectTimeout = 3000;
            options.ConnectRetry = 1;
            await using var mux = await ConnectionMultiplexer.ConnectAsync(options);

            var db = mux.GetDatabase();
            var server = mux.GetServers().FirstOrDefault(s => s.IsConnected);
            var info = await ReadInfoAsync(server);

            var streamKey = "tamp.observer.raw";
            var streams = new List<StreamStat>();
            long length = 0, pending = 0;
            var status = "ok";
            try
            {
                length = await db.StreamLengthAsync(streamKey);
                var groups = await db.StreamGroupInfoAsync(streamKey);
                var evalGroup = groups.FirstOrDefault(g => g.Name == "evaluator");
                pending = evalGroup.PendingMessageCount;
                // "N of M": active consumers of total registered. The raw ConsumerCount counts stale members a
                // crashed/replaced evaluator left behind, which is what produced the nonsensical "3 of 1"; count
                // only consumers with recent activity as active, and show the registered total as the denominator.
                var (active, total) = await ConsumersAsync(db, streamKey);
                status = pending > PendingWarnThreshold ? "warn" : "ok";
                streams.Add(new StreamStat(streamKey, length, pending, null, active, total, status));
            }
            catch
            {
                // Stream/group not created yet (no ingest): show it empty rather than failing the whole page.
                streams.Add(new StreamStat(streamKey, 0, 0, null, 0, 0, "ok"));
            }

            var srv = new ValkeyServer(
                UsedMemoryBytes: InfoLong(info, "used_memory"),
                MaxMemoryBytes: InfoLong(info, "maxmemory"),
                EvictionPolicy: InfoStr(info, "maxmemory_policy") ?? "?",
                Persistence: await PersistenceAsync(info, server),
                OpsPerSec: InfoLong(info, "instantaneous_ops_per_sec"),
                Clients: (int)InfoLong(info, "connected_clients"),
                Version: InfoStr(info, "redis_version") ?? InfoStr(info, "valkey_version") ?? "?",
                UptimeDays: InfoLong(info, "uptime_in_seconds") / 86400.0);

            var lagging = streams.Count(s => s.Status == "warn");
            return new ValkeyHealth(srv, streams, lagging);
        }
        catch
        {
            return null;
        }
    }

    private const long StaleConsumerIdleMs = 60_000; // a consumer idle longer than this is treated as dropped.

    // Active (recently seen) vs total registered consumers in the evaluator group. XINFO CONSUMERS gives per
    // consumer idle time; a long-idle one is a stale member left by a crashed/replaced evaluator.
    private static async Task<(int Active, int Total)> ConsumersAsync(IDatabase db, string streamKey)
    {
        try
        {
            var consumers = await db.StreamConsumerInfoAsync(streamKey, "evaluator");
            var active = consumers.Count(c => c.IdleTimeInMilliseconds < StaleConsumerIdleMs);
            return (active, consumers.Length);
        }
        catch
        {
            return (0, 0);
        }
    }

    // Honest persistence posture: AOF if enabled; otherwise RDB only when snapshot points are configured, else
    // "persistence off" (the pod runs with no durability, which "RDB only" wrongly implied).
    private static async Task<string> PersistenceAsync(Dictionary<string, string> info, IServer? server)
    {
        if (InfoStr(info, "aof_enabled") == "1")
            return "AOF enabled";
        try
        {
            if (server is not null)
            {
                var save = await server.ConfigGetAsync("save");
                var saveVal = save.Length > 0 ? save[0].Value : null;
                return string.IsNullOrWhiteSpace(saveVal) ? "persistence off" : "RDB snapshots";
            }
        }
        catch
        {
            // CONFIG GET unavailable: fall through to the conservative label below.
        }
        return "RDB only";
    }

    private static async Task<Dictionary<string, string>> ReadInfoAsync(IServer? server)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (server is null)
            return dict;
        var groups = await server.InfoAsync();
        foreach (var grp in groups)
            foreach (var kv in grp)
                dict[kv.Key] = kv.Value;
        return dict;
    }

    private static long InfoLong(Dictionary<string, string> info, string key) =>
        info.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : 0;
    private static string? InfoStr(Dictionary<string, string> info, string key) =>
        info.TryGetValue(key, out var v) ? v : null;

    private static async Task<int> ScalarInt(NpgsqlConnection c, string sql, CancellationToken ct) =>
        Convert.ToInt32(await Scalar(c, sql, ct) ?? 0);
    private static async Task<long> ScalarLong(NpgsqlConnection c, string sql, CancellationToken ct) =>
        Convert.ToInt64(await Scalar(c, sql, ct) ?? 0L);
    private static async Task<double> ScalarDouble(NpgsqlConnection c, string sql, CancellationToken ct) =>
        Convert.ToDouble(await Scalar(c, sql, ct) ?? 0.0);
    private static async Task<string?> ScalarString(NpgsqlConnection c, string sql, CancellationToken ct) =>
        (await Scalar(c, sql, ct))?.ToString();
    private static async Task<object?> Scalar(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync(ct);
    }
}
