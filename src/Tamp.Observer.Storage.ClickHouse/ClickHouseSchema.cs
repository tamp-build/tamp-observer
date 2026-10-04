using ClickHouse.Client.ADO;

namespace Tamp.Observer.Storage.ClickHouse;

/// <summary>
/// DDL for the ClickHouse telemetry tier (ADR 0005 opt-in top tier). ClickHouse holds the high-volume
/// span/log events for fast columnar aggregation; the entity catalog and raw bucket stay in the Postgres
/// system of record (ADR 0005 section 7, Postgres-canonical). Tables are flat, matching the superset
/// write model (ADR 0006). Retention (TOBS-24) is native MergeTree TTL on <c>received_at</c>, applied on both
/// create and (for already-created tables) an ALTER MODIFY TTL so a changed window takes effect.
/// </summary>
public static class ClickHouseSchema
{
    public const string SpansTable = "observer_spans";
    public const string LogsTable = "observer_logs";

    private static string CreateSpans(int retentionDays) => $@"
CREATE TABLE IF NOT EXISTS {SpansTable} (
    project_id UUID,
    service_id UUID,
    environment_id Nullable(UUID),
    version_id UUID,
    trace_id String,
    span_id String,
    parent_span_id Nullable(String),
    name String,
    kind Int32,
    start_unix_nano Int64,
    end_unix_nano Int64,
    duration_nano Int64,
    status_code Int32,
    status_message Nullable(String),
    instance_id String,
    attributes String,
    receipt_id String,
    received_at DateTime64(9, 'UTC')
) ENGINE = MergeTree ORDER BY (project_id, service_id, start_unix_nano)
  TTL toDateTime(received_at) + INTERVAL {retentionDays} DAY";

    private static string CreateLogs(int retentionDays) => $@"
CREATE TABLE IF NOT EXISTS {LogsTable} (
    project_id UUID,
    service_id UUID,
    environment_id Nullable(UUID),
    version_id UUID,
    time_unix_nano Int64,
    severity_number Int32,
    severity_text Nullable(String),
    body Nullable(String),
    trace_id Nullable(String),
    span_id Nullable(String),
    instance_id String,
    attributes String,
    receipt_id String,
    received_at DateTime64(9, 'UTC')
) ENGINE = MergeTree ORDER BY (project_id, service_id, time_unix_nano)
  TTL toDateTime(received_at) + INTERVAL {retentionDays} DAY";

    /// <summary>Create the telemetry tables if they do not exist, and apply the retention TTL (TOBS-24). The
    /// ALTER MODIFY TTL ensures an existing table (created before TTL, or with a different window) adopts the
    /// configured retention.</summary>
    public static async Task EnsureAsync(string connectionString, int retentionDays = 30, CancellationToken ct = default)
    {
        var days = retentionDays > 0 ? retentionDays : 30;
        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        var statements = new[]
        {
            CreateSpans(days),
            CreateLogs(days),
            $"ALTER TABLE {SpansTable} MODIFY TTL toDateTime(received_at) + INTERVAL {days} DAY",
            $"ALTER TABLE {LogsTable} MODIFY TTL toDateTime(received_at) + INTERVAL {days} DAY",
        };
        foreach (var ddl in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
