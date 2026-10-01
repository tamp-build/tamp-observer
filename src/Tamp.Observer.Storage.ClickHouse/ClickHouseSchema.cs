using ClickHouse.Client.ADO;

namespace Tamp.Observer.Storage.ClickHouse;

/// <summary>
/// DDL for the ClickHouse telemetry tier (ADR 0005 opt-in top tier). ClickHouse holds the high-volume
/// span/log events for fast columnar aggregation; the entity catalog and raw bucket stay in the Postgres
/// system of record (ADR 0005 section 7, Postgres-canonical). Tables are flat, matching the superset
/// write model (ADR 0006).
/// </summary>
public static class ClickHouseSchema
{
    public const string SpansTable = "observer_spans";
    public const string LogsTable = "observer_logs";

    private const string CreateSpans = $@"
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
) ENGINE = MergeTree ORDER BY (project_id, service_id, start_unix_nano)";

    private const string CreateLogs = $@"
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
) ENGINE = MergeTree ORDER BY (project_id, service_id, time_unix_nano)";

    /// <summary>Create the telemetry tables if they do not exist.</summary>
    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        foreach (var ddl in new[] { CreateSpans, CreateLogs })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
