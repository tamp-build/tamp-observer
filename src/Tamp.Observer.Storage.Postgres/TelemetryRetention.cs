using Npgsql;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// Time-based pruning of the high-volume telemetry documents (TOBS-24): spans, logs and metric points older than
/// a retention cutoff are deleted from the Postgres (Marten) tier. Entities, issues and auth are the system of
/// record and are never pruned here; only telemetry. DuckDB reads these same tables, so it inherits whatever
/// Postgres keeps. The deletes key on the per-row time field, which Marten has already indexed
/// (<c>StartUnixNano</c>/<c>TimeUnixNano</c>), so the prune uses the index rather than a full scan.
/// </summary>
public static class TelemetryRetention
{
    private const string Schema = ObserverStoreConfiguration.SchemaName;

    // (table, time field) pairs. The metric table may not exist until the first metric is written.
    private static readonly (string Table, string TimeField)[] Targets =
    [
        ("mt_doc_ingestedspan", "StartUnixNano"),
        ("mt_doc_ingestedlog", "TimeUnixNano"),
        ("mt_doc_ingestedmetric", "TimeUnixNano"),
    ];

    /// <summary>Delete telemetry rows whose event time is before <paramref name="cutoffNano"/> (Unix nanos).
    /// Returns the total rows removed. A missing table (e.g. metrics before the first write) is skipped.</summary>
    public static async Task<int> PruneAsync(string connectionString, long cutoffNano, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var removed = 0;
        foreach (var (table, field) in Targets)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"DELETE FROM {Schema}.{table} WHERE (data->>'{field}')::bigint < @cutoff", conn);
                cmd.Parameters.AddWithValue("cutoff", cutoffNano);
                removed += await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                // Table not created yet; nothing to prune.
            }
        }
        return removed;
    }
}
