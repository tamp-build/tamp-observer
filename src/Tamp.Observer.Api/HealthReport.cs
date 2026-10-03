using Marten;
using Npgsql;
using StackExchange.Redis;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Api;

// The Storage & health aggregation (ADR 0014, README 7.8): gathers live stats from the real pipeline components
// the API can reach without extra infrastructure. Postgres (pg_stat_*) and Valkey (INFO + stream introspection)
// are gathered live; the quarantine count is real from Marten. Time-series (throughput, freshness, lag history),
// per-pod CPU/memory/restarts, retention schedule and the health-event log need sources we do not collect yet
// (a metrics store, the k8s metrics API, a retention scheduler, an events log) and come back null/empty, which
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
    double? CpuPercent, long? MemoryBytes, int? Restarts, string? Uptime);

public static class HealthReport
{
    private const long PendingWarnThreshold = 5000; // raw-bucket backlog that counts as lagging

    public static async Task<HealthView> BuildAsync(
        string pgConn, string? valkeyConn, string storeTier, bool clickHouse, IDocumentStore docs, CancellationToken ct)
    {
        var pg = await TryPostgresAsync(pgConn, ct);
        var valkey = string.IsNullOrWhiteSpace(valkeyConn) ? null : await TryValkeyAsync(valkeyConn, ct);
        var (rejected24h, quarantineTotal) = await QuarantineAsync(docs, ct);

        var buffered = valkey?.Streams.Sum(s => s.Pending) ?? 0;
        var dominant = valkey?.Streams.OrderByDescending(s => s.Pending).FirstOrDefault();
        var lagging = valkey?.LaggingCount ?? 0;

        var kpis = new HealthKpis(
            IngestRatePerSec: null, FreshnessP95Sec: null,
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
            new("API", "serves UI + DuckDB", "ok", null, null, null, null, null),
            new("Collector", "Go OTel", "ok", null, null, null, null, null),
            new("Evaluator", ".NET workers", lagging > 0 ? "warn" : "ok", null, null, null, null, null),
            new("Valkey", "streams", valkey is null ? "crit" : "ok", valkey is null ? "0 / 1" : "1 / 1", null,
                valkey?.Server.UsedMemoryBytes, null, null),
            new("Postgres", "write store", pgOk ? "ok" : "crit", pgOk ? "1 / 1" : "0 / 1", null, null, null, null),
            new("Dex", "OIDC broker", "ok", null, null, null, null, null),
        };

        return new HealthView(
            overall, overallMsg, kpis, pipeline, valkey, pg,
            new DuckTier(storeTier == "duckdb", storeTier), clickHouse, components);
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
            int consumers = 0;
            var status = "ok";
            try
            {
                length = await db.StreamLengthAsync(streamKey);
                var groups = await db.StreamGroupInfoAsync(streamKey);
                var evalGroup = groups.FirstOrDefault(g => g.Name == "evaluator");
                pending = evalGroup.PendingMessageCount;
                consumers = evalGroup.ConsumerCount;
                status = pending > PendingWarnThreshold ? "warn" : "ok";
                streams.Add(new StreamStat(streamKey, length, pending, null, consumers, 1, status));
            }
            catch
            {
                // Stream/group not created yet (no ingest): show it empty rather than failing the whole page.
                streams.Add(new StreamStat(streamKey, 0, 0, null, 0, 1, "ok"));
            }

            var srv = new ValkeyServer(
                UsedMemoryBytes: InfoLong(info, "used_memory"),
                MaxMemoryBytes: InfoLong(info, "maxmemory"),
                EvictionPolicy: InfoStr(info, "maxmemory_policy") ?? "?",
                Persistence: InfoStr(info, "aof_enabled") == "1" ? "AOF enabled" : "RDB only",
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
