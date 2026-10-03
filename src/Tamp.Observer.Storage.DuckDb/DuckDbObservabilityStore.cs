using System.Data.Common;
using DuckDB.NET.Data;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.DuckDb;

/// <summary>
/// The DuckDB on-demand analytical accelerator (ADR 0005 middle tier). Postgres stays the quiet system of
/// record; this provider spins an embedded DuckDB per query, attaches Postgres via DuckDB's postgres
/// extension, runs the heavy columnar aggregation (quantile_cont / GROUP BY) in DuckDB's engine, and goes
/// idle again. Zero standing infra, zero idle cost. Third translator of the capability interface (ADR 0006).
///
/// Note: the postgres extension and (first run) its download require network; an air-gapped deployment
/// pre-bundles the extension. The span/log fields are read out of Marten's jsonb `data` column.
/// </summary>
public sealed class DuckDbObservabilityStore(string postgresConnectionString, string schema = "observer") : IObservabilityStore
{
    private readonly string _attach = ToLibpq(postgresConnectionString);
    private readonly string _spans = $"pg.{schema}.mt_doc_ingestedspan";
    private readonly string _logs = $"pg.{schema}.mt_doc_ingestedlog";

    public async Task<LatencyPercentiles> GetLatencyPercentilesAsync(SpanQuery query, CancellationToken ct = default)
    {
        var (where, bind) = SpanWhere(query);
        var sql = $@"
SELECT count(*) AS c,
       quantile_cont(CAST(json_extract_string(data,'$.DurationNano') AS BIGINT), 0.5) AS p50,
       quantile_cont(CAST(json_extract_string(data,'$.DurationNano') AS BIGINT), 0.95) AS p95,
       quantile_cont(CAST(json_extract_string(data,'$.DurationNano') AS BIGINT), 0.99) AS p99
FROM {_spans} WHERE {where}";

        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, sql, bind);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new LatencyPercentiles(0, 0, 0, 0);
        var count = Convert.ToInt64(reader["c"]);
        if (count == 0)
            return new LatencyPercentiles(0, 0, 0, 0);
        return new LatencyPercentiles(count, Dbl(reader["p50"]), Dbl(reader["p95"]), Dbl(reader["p99"]));
    }

    public async Task<IReadOnlyList<OperationStat>> GetTopOperationsAsync(SpanQuery query, int limit = 10, CancellationToken ct = default)
    {
        var (where, bind) = SpanWhere(query);
        var top = Math.Clamp(limit, 1, 10_000);
        var sql = $@"
SELECT json_extract_string(data,'$.Name') AS name,
       count(*) AS c,
       count(*) FILTER (WHERE CAST(json_extract_string(data,'$.StatusCode') AS INTEGER) = 2) AS e
FROM {_spans} WHERE {where}
GROUP BY name ORDER BY c DESC, name ASC LIMIT {top}";

        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, sql, bind);
        var result = new List<OperationStat>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new OperationStat((string)reader["name"], Convert.ToInt64(reader["c"]), Convert.ToInt64(reader["e"])));
        return result;
    }

    public async Task<TraceView> GetTraceAsync(Guid projectId, string traceId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        var spans = new List<IngestedSpan>();
        var spanSql = $@"
SELECT json_extract_string(data,'$.ProjectId') AS project_id,
       json_extract_string(data,'$.ServiceId') AS service_id,
       json_extract_string(data,'$.VersionId') AS version_id,
       json_extract_string(data,'$.TraceId') AS trace_id,
       json_extract_string(data,'$.SpanId') AS span_id,
       json_extract_string(data,'$.Name') AS name,
       CAST(json_extract_string(data,'$.Kind') AS INTEGER) AS kind,
       CAST(json_extract_string(data,'$.StartUnixNano') AS BIGINT) AS start_nano,
       CAST(json_extract_string(data,'$.EndUnixNano') AS BIGINT) AS end_nano,
       CAST(json_extract_string(data,'$.DurationNano') AS BIGINT) AS duration_nano,
       CAST(json_extract_string(data,'$.StatusCode') AS INTEGER) AS status_code,
       json_extract_string(data,'$.InstanceId') AS instance_id,
       json_extract_string(data,'$.ReceiptId') AS receipt_id
FROM {_spans}
WHERE json_extract_string(data,'$.ProjectId') = $projectId AND json_extract_string(data,'$.TraceId') = $traceId";
        await using (var cmd = Command(conn, spanSql, [("projectId", projectId.ToString()), ("traceId", traceId)]))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                spans.Add(new IngestedSpan
                {
                    ProjectId = Guid.Parse((string)reader["project_id"]),
                    ServiceId = Guid.Parse((string)reader["service_id"]),
                    VersionId = Guid.Parse((string)reader["version_id"]),
                    TraceId = (string)reader["trace_id"],
                    SpanId = (string)reader["span_id"],
                    Name = (string)reader["name"],
                    Kind = Convert.ToInt32(reader["kind"]),
                    StartUnixNano = Convert.ToInt64(reader["start_nano"]),
                    EndUnixNano = Convert.ToInt64(reader["end_nano"]),
                    DurationNano = Convert.ToInt64(reader["duration_nano"]),
                    StatusCode = Convert.ToInt32(reader["status_code"]),
                    InstanceId = (string)reader["instance_id"],
                    ReceiptId = (string)reader["receipt_id"],
                });
        }

        var logs = new List<IngestedLog>();
        var logSql = $@"
SELECT json_extract_string(data,'$.ProjectId') AS project_id,
       json_extract_string(data,'$.ServiceId') AS service_id,
       json_extract_string(data,'$.VersionId') AS version_id,
       CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) AS time_nano,
       CAST(json_extract_string(data,'$.SeverityNumber') AS INTEGER) AS sev_num,
       json_extract_string(data,'$.SeverityText') AS sev_text,
       json_extract_string(data,'$.Body') AS body,
       json_extract_string(data,'$.TraceId') AS trace_id,
       json_extract_string(data,'$.InstanceId') AS instance_id,
       json_extract_string(data,'$.ReceiptId') AS receipt_id
FROM {_logs}
WHERE json_extract_string(data,'$.ProjectId') = $projectId AND json_extract_string(data,'$.TraceId') = $traceId";
        await using (var cmd = Command(conn, logSql, [("projectId", projectId.ToString()), ("traceId", traceId)]))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                logs.Add(new IngestedLog
                {
                    ProjectId = Guid.Parse((string)reader["project_id"]),
                    ServiceId = Guid.Parse((string)reader["service_id"]),
                    VersionId = Guid.Parse((string)reader["version_id"]),
                    TimeUnixNano = Convert.ToInt64(reader["time_nano"]),
                    SeverityNumber = Convert.ToInt32(reader["sev_num"]),
                    SeverityText = reader["sev_text"] as string,
                    Body = reader["body"] as string,
                    TraceId = reader["trace_id"] as string,
                    InstanceId = (string)reader["instance_id"],
                    ReceiptId = (string)reader["receipt_id"],
                });
        }

        return new TraceView(spans, logs);
    }

    public async Task<IReadOnlyList<IngestedLog>> GetLogsAsync(LogQuery query, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var where = "json_extract_string(data,'$.ProjectId') = $projectId"
                  + " AND CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) >= $start"
                  + " AND CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) < $end";
        var bind = new List<(string, object)>
        {
            ("projectId", query.ProjectId.ToString()),
            ("start", query.Window.StartUnixNano),
            ("end", query.Window.EndUnixNano),
        };
        if (query.ServiceId is Guid serviceId)
        {
            where += " AND json_extract_string(data,'$.ServiceId') = $serviceId";
            bind.Add(("serviceId", serviceId.ToString()));
        }
        if (query.MinSeverityNumber is int min)
        {
            where += " AND CAST(json_extract_string(data,'$.SeverityNumber') AS INTEGER) >= $minSev";
            bind.Add(("minSev", min));
        }
        var sql = $@"
SELECT json_extract_string(data,'$.ProjectId') AS project_id,
       json_extract_string(data,'$.ServiceId') AS service_id,
       json_extract_string(data,'$.VersionId') AS version_id,
       CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) AS time_nano,
       CAST(json_extract_string(data,'$.SeverityNumber') AS INTEGER) AS sev_num,
       json_extract_string(data,'$.SeverityText') AS sev_text,
       json_extract_string(data,'$.Body') AS body,
       json_extract_string(data,'$.TraceId') AS trace_id,
       json_extract_string(data,'$.InstanceId') AS instance_id,
       json_extract_string(data,'$.ReceiptId') AS receipt_id
FROM {_logs} WHERE {where}
ORDER BY time_nano DESC LIMIT {(query.Limit <= 0 ? 200 : query.Limit)}";
        var logs = new List<IngestedLog>();
        await using var cmd = Command(conn, sql, bind.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            logs.Add(new IngestedLog
            {
                ProjectId = Guid.Parse((string)reader["project_id"]),
                ServiceId = Guid.Parse((string)reader["service_id"]),
                VersionId = Guid.Parse((string)reader["version_id"]),
                TimeUnixNano = Convert.ToInt64(reader["time_nano"]),
                SeverityNumber = Convert.ToInt32(reader["sev_num"]),
                SeverityText = reader["sev_text"] as string,
                Body = reader["body"] as string,
                TraceId = reader["trace_id"] as string,
                InstanceId = (string)reader["instance_id"],
                ReceiptId = (string)reader["receipt_id"],
            });
        return logs;
    }

    public async Task<IssueOccurrence?> GetLatestOccurrenceAsync(Guid projectId, string fingerprint, CancellationToken ct = default)
    {
        const string match = "json_extract_string(data,'$.Fingerprint') = $match";
        await using var conn = await OpenAsync(ct);
        var span = await LatestOccurrence(conn, _spans, "StartUnixNano", "span", projectId, match, fingerprint, ct);
        var log = await LatestOccurrence(conn, _logs, "TimeUnixNano", "log", projectId, match, fingerprint, ct);
        if (span is null) return log;
        if (log is null) return span;
        return span.AtUnixNano >= log.AtUnixNano ? span : log;
    }

    public async Task<IssueOccurrence?> GetLatestOccurrenceBySessionAsync(Guid projectId, string sessionId, CancellationToken ct = default)
    {
        // Latest error occurrence (has a fingerprint) carrying this session id.
        const string match = "json_extract_string(data,'$.Attributes.\"tamp.session.id\"') = $match"
                           + " AND json_extract_string(data,'$.Fingerprint') IS NOT NULL";
        await using var conn = await OpenAsync(ct);
        var span = await LatestOccurrence(conn, _spans, "StartUnixNano", "span", projectId, match, sessionId, ct);
        var log = await LatestOccurrence(conn, _logs, "TimeUnixNano", "log", projectId, match, sessionId, ct);
        if (span is null) return log;
        if (log is null) return span;
        return span.AtUnixNano >= log.AtUnixNano ? span : log;
    }

    public async Task<IssueOccurrence?> GetLatestOccurrenceByTraceAsync(Guid projectId, string traceId, CancellationToken ct = default)
    {
        const string match = "json_extract_string(data,'$.TraceId') = $match"
                           + " AND json_extract_string(data,'$.Fingerprint') IS NOT NULL";
        await using var conn = await OpenAsync(ct);
        var span = await LatestOccurrence(conn, _spans, "StartUnixNano", "span", projectId, match, traceId, ct);
        var log = await LatestOccurrence(conn, _logs, "TimeUnixNano", "log", projectId, match, traceId, ct);
        if (span is null) return log;
        if (log is null) return span;
        return span.AtUnixNano >= log.AtUnixNano ? span : log;
    }

    private async Task<IssueOccurrence?> LatestOccurrence(
        DuckDBConnection conn, string table, string timeField, string source, Guid projectId, string matchSql, string matchValue, CancellationToken ct)
    {
        var sql = $@"
SELECT json_extract_string(data,'$.TraceId') AS trace_id,
       json_extract_string(data,'$.SpanId') AS span_id,
       json_extract_string(data,'$.Attributes.""tamp.session.id""') AS session_id,
       json_extract_string(data,'$.ServiceId') AS service_id,
       json_extract_string(data,'$.Fingerprint') AS fingerprint,
       CAST(json_extract_string(data,'$.{timeField}') AS BIGINT) AS at_nano
FROM {table}
WHERE json_extract_string(data,'$.ProjectId') = $projectId
  AND {matchSql}
ORDER BY at_nano DESC LIMIT 1";
        await using var cmd = Command(conn, sql, [("projectId", projectId.ToString()), ("match", matchValue)]);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new IssueOccurrence(
            source,
            Convert.ToInt64(reader["at_nano"]),
            reader["trace_id"] as string,
            reader["span_id"] as string,
            reader["session_id"] as string,
            Guid.Parse((string)reader["service_id"]),
            reader["fingerprint"] as string);
    }

    private (string Where, (string Name, object Value)[] Bind) SpanWhere(SpanQuery query)
    {
        var where = "json_extract_string(data,'$.ProjectId') = $projectId"
                  + " AND CAST(json_extract_string(data,'$.StartUnixNano') AS BIGINT) >= $start"
                  + " AND CAST(json_extract_string(data,'$.StartUnixNano') AS BIGINT) < $end";
        var bind = new List<(string, object)>
        {
            ("projectId", query.ProjectId.ToString()),
            ("start", query.Window.StartUnixNano),
            ("end", query.Window.EndUnixNano),
        };
        if (query.ServiceId is Guid serviceId)
        {
            where += " AND json_extract_string(data,'$.ServiceId') = $serviceId";
            bind.Add(("serviceId", serviceId.ToString()));
        }
        return (where, bind.ToArray());
    }

    private async Task<DuckDBConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new DuckDBConnection("DataSource=:memory:");
        await conn.OpenAsync(ct);
        foreach (var stmt in new[]
        {
            "INSTALL postgres;",
            "LOAD postgres;",
            $"ATTACH '{_attach}' AS pg (TYPE postgres, READ_ONLY);",
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = stmt;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return conn;
    }

    private static DuckDBCommand Command(DuckDBConnection conn, string sql, (string Name, object Value)[] bind)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in bind)
            cmd.Parameters.Add(new DuckDBParameter(name, value));
        return cmd;
    }

    private static double Dbl(object value) => value is DBNull ? 0 : Convert.ToDouble(value);

    /// <summary>Convert an Npgsql-style connection string to a libpq string for DuckDB's postgres ATTACH.</summary>
    private static string ToLibpq(string npgsql)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in npgsql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf('=');
            if (i > 0)
                map[part[..i].Trim()] = part[(i + 1)..].Trim();
        }
        string Get(params string[] keys) => keys.Select(k => map.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => v is not null) ?? "";
        return $"host={Get("Host", "Server")} port={Get("Port")} dbname={Get("Database")} user={Get("Username", "User ID", "UserId")} password={Get("Password")}";
    }
}
