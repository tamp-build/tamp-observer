using Npgsql;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// DDL and upsert SQL for the materialized time-bucketed rollup (TOBS-25). These are raw SQL tables (not Marten
/// documents) because the on-admit maintenance needs an atomic server-side increment
/// (<c>ON CONFLICT DO UPDATE SET x = x + excluded.x</c>) and element-wise histogram addition, which Marten's
/// document API cannot express. The evaluator (the writer) ensures them at startup, mirroring
/// <c>ClickHouseSchema.EnsureAsync</c>. Lives in the same <c>observer</c> schema as the Marten documents so the
/// DuckDB accelerator can read it over the existing ATTACH.
/// </summary>
public static class RollupSchema
{
    private const string Schema = ObserverStoreConfiguration.SchemaName;

    private static readonly string CreateSignal = $@"
CREATE SCHEMA IF NOT EXISTS {Schema};
CREATE TABLE IF NOT EXISTS {Schema}.observer_rollup_signal (
    project_id   uuid   NOT NULL,
    service_id   uuid   NOT NULL,
    signal       text   NOT NULL,
    bucket_start bigint NOT NULL,
    event_count  bigint NOT NULL DEFAULT 0,
    error_count  bigint NOT NULL DEFAULT 0,
    bytes        bigint NOT NULL DEFAULT 0,
    lat_hist     bigint[] NOT NULL,
    fresh_hist   bigint[] NOT NULL,
    PRIMARY KEY (project_id, service_id, signal, bucket_start)
);";

    // Element-wise add of two fixed-length (32) bigint[] histograms, order-preserving.
    private static readonly string HistAddFn = $@"
CREATE OR REPLACE FUNCTION {Schema}.observer_hist_add(a bigint[], b bigint[]) RETURNS bigint[] AS $$
    SELECT array_agg(COALESCE(a[i], 0) + COALESCE(b[i], 0) ORDER BY i)
    FROM generate_series(1, 32) AS i;
$$ LANGUAGE sql IMMUTABLE;";

    /// <summary>Upsert one signal bucket. Placeholders are '?' for Marten's <c>QueueSqlCommand</c>; parameter
    /// order: project_id, service_id, signal, bucket_start, event_count, error_count, bytes, lat_hist, fresh_hist.</summary>
    public const string UpsertSignalSql = @"
INSERT INTO observer.observer_rollup_signal
    (project_id, service_id, signal, bucket_start, event_count, error_count, bytes, lat_hist, fresh_hist)
VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
ON CONFLICT (project_id, service_id, signal, bucket_start) DO UPDATE SET
    event_count = observer_rollup_signal.event_count + excluded.event_count,
    error_count = observer_rollup_signal.error_count + excluded.error_count,
    bytes       = observer_rollup_signal.bytes       + excluded.bytes,
    lat_hist    = observer.observer_hist_add(observer_rollup_signal.lat_hist, excluded.lat_hist),
    fresh_hist  = observer.observer_hist_add(observer_rollup_signal.fresh_hist, excluded.fresh_hist);";

    /// <summary>Create the rollup schema objects if they do not exist (idempotent).</summary>
    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        foreach (var ddl in new[] { CreateSignal, HistAddFn })
        {
            await using var cmd = new NpgsqlCommand(ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
