using System.Data;
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
public sealed class DuckDbObservabilityStore(string postgresConnectionString, string schema = "observer")
    : IObservabilityStore, IDisposable
{
    private readonly string _attach = ToLibpq(postgresConnectionString);
    private readonly string _spans = $"pg.{schema}.mt_doc_ingestedspan";
    private readonly string _logs = $"pg.{schema}.mt_doc_ingestedlog";
    private readonly string _metrics = $"pg.{schema}.mt_doc_ingestedmetric";

    // One long-lived in-process DuckDB connection with the Postgres attach held open, reused across reads
    // (TOBS-34). The connection is single-threaded, so access is serialized through the gate; with the attach
    // already established each query is fast, so serializing a handful of parallel reads is cheap. A broken
    // connection is rebuilt on the next acquire.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DuckDBConnection? _shared;

    private sealed class Lease(SemaphoreSlim gate, DuckDBConnection conn) : IAsyncDisposable
    {
        public DuckDBConnection Conn => conn;
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    public async Task<LatencyPercentiles> GetLatencyPercentilesAsync(SpanQuery query, CancellationToken ct = default)
    {
        var (where, bind) = SpanWhere(query);
        var sql = $@"
SELECT count(*) AS c,
       quantile_cont(CAST(json_extract_string(data,'$.DurationNano') AS BIGINT), 0.5) AS p50,
       quantile_cont(CAST(json_extract_string(data,'$.DurationNano') AS BIGINT), 0.95) AS p95,
       quantile_cont(CAST(json_extract_string(data,'$.DurationNano') AS BIGINT), 0.99) AS p99
FROM {_spans} WHERE {where}";

        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;
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

        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;
        await using var cmd = Command(conn, sql, bind);
        var result = new List<OperationStat>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new OperationStat((string)reader["name"], Convert.ToInt64(reader["c"]), Convert.ToInt64(reader["e"])));
        return result;
    }

    public async Task<TraceView> GetTraceAsync(Guid projectId, string traceId, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;

        var spans = new List<IngestedSpan>();
        var spanSql = $@"
SELECT json_extract_string(data,'$.ProjectId') AS project_id,
       json_extract_string(data,'$.ServiceId') AS service_id,
       json_extract_string(data,'$.VersionId') AS version_id,
       json_extract_string(data,'$.TraceId') AS trace_id,
       json_extract_string(data,'$.SpanId') AS span_id,
       json_extract_string(data,'$.ParentSpanId') AS parent_span_id,
       json_extract_string(data,'$.Name') AS name,
       CAST(json_extract_string(data,'$.Kind') AS INTEGER) AS kind,
       CAST(json_extract_string(data,'$.StartUnixNano') AS BIGINT) AS start_nano,
       CAST(json_extract_string(data,'$.EndUnixNano') AS BIGINT) AS end_nano,
       CAST(json_extract_string(data,'$.DurationNano') AS BIGINT) AS duration_nano,
       CAST(json_extract_string(data,'$.StatusCode') AS INTEGER) AS status_code,
       json_extract_string(data,'$.StatusMessage') AS status_message,
       json_extract(data,'$.Attributes')::VARCHAR AS attributes,
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
                    ParentSpanId = reader["parent_span_id"] as string,
                    Name = (string)reader["name"],
                    Kind = Convert.ToInt32(reader["kind"]),
                    StartUnixNano = Convert.ToInt64(reader["start_nano"]),
                    EndUnixNano = Convert.ToInt64(reader["end_nano"]),
                    DurationNano = Convert.ToInt64(reader["duration_nano"]),
                    StatusCode = Convert.ToInt32(reader["status_code"]),
                    StatusMessage = reader["status_message"] as string,
                    Attributes = ParseAttrs(reader["attributes"] as string),
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
        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;
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
        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;
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
        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;
        var span = await LatestOccurrence(conn, _spans, "StartUnixNano", "span", projectId, match, sessionId, ct);
        var log = await LatestOccurrence(conn, _logs, "TimeUnixNano", "log", projectId, match, sessionId, ct);
        if (span is null) return log;
        if (log is null) return span;
        return span.AtUnixNano >= log.AtUnixNano ? span : log;
    }

    public async Task<IReadOnlyList<SeriesBucket>> GetSpanSeriesAsync(SpanQuery query, int buckets, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        var width = Math.Max(1, (query.Window.EndUnixNano - query.Window.StartUnixNano) / buckets);
        var where = "json_extract_string(data,'$.ProjectId') = $projectId"
                  + " AND CAST(json_extract_string(data,'$.StartUnixNano') AS BIGINT) >= $start"
                  + " AND CAST(json_extract_string(data,'$.StartUnixNano') AS BIGINT) < $end";
        var bind = new List<(string, object)>
        {
            ("projectId", query.ProjectId.ToString()), ("start", query.Window.StartUnixNano),
            ("end", query.Window.EndUnixNano), ("width", width),
        };
        if (query.ServiceId is Guid svc)
        {
            where += " AND json_extract_string(data,'$.ServiceId') = $serviceId";
            bind.Add(("serviceId", svc.ToString()));
        }
        // DuckDB is the columnar accelerator: compute volume/errors AND an exact per-bucket p95 natively
        // (quantile_cont over span duration). The materialized rollup (TOBS-25) is the Postgres-floor
        // optimization; here native exact quantiles are cheaper and more precise than folding histograms.
        var sql = $@"
SELECT CAST((CAST(json_extract_string(data,'$.StartUnixNano') AS BIGINT) - $start) / $width AS INTEGER) AS b,
       count(*) AS c,
       count(*) FILTER (WHERE CAST(json_extract_string(data,'$.StatusCode') AS INTEGER) = 2) AS e,
       quantile_cont(CAST(json_extract_string(data,'$.DurationNano') AS BIGINT), 0.95) AS p95
FROM {_spans} WHERE {where} GROUP BY b";
        var counts = new long[buckets];
        var errors = new long[buckets];
        var p95 = new long[buckets];
        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;
        await using var cmd = Command(conn, sql, bind.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var b = Math.Clamp(Convert.ToInt32(reader["b"]), 0, buckets - 1);
            counts[b] = Convert.ToInt64(reader["c"]);
            errors[b] = Convert.ToInt64(reader["e"]);
            p95[b] = (long)Dbl(reader["p95"]);
        }
        var list = new List<SeriesBucket>(buckets);
        for (var i = 0; i < buckets; i++)
            list.Add(new SeriesBucket(query.Window.StartUnixNano + (long)i * width, counts[i], errors[i], p95[i]));
        return list;
    }

    public async Task<IReadOnlyList<OperationSeries>> GetOperationSeriesAsync(SpanQuery query, int buckets, int limit = 10, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        limit = limit <= 0 ? 10 : limit;
        var width = Math.Max(1, (query.Window.EndUnixNano - query.Window.StartUnixNano) / buckets);
        var (where, bind) = SpanWhere(query);
        var bindW = bind.Append(("width", (object)width)).ToArray();
        const string dur = "CAST(json_extract_string(data,'$.DurationNano') AS BIGINT)";
        const string isErr = "CAST(json_extract_string(data,'$.StatusCode') AS INTEGER) = 2";

        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;

        // Top operations with exact overall p95.
        var top = new List<(string Op, long Count, long Err, long P95)>();
        var topSql = $@"
SELECT json_extract_string(data,'$.Name') AS op, count(*) AS c,
       count(*) FILTER (WHERE {isErr}) AS e, quantile_cont({dur}, 0.95) AS p95
FROM {_spans} WHERE {where} GROUP BY op ORDER BY c DESC, op LIMIT {limit}";
        await using (var cmd = Command(conn, topSql, bind))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                top.Add(((string)r["op"], Convert.ToInt64(r["c"]), Convert.ToInt64(r["e"]), (long)Dbl(r["p95"])));

        // Per-(operation, bucket) call volume for the sparklines.
        var series = new Dictionary<string, (long[] C, long[] E)>(StringComparer.Ordinal);
        var seriesSql = $@"
SELECT json_extract_string(data,'$.Name') AS op,
       CAST((CAST(json_extract_string(data,'$.StartUnixNano') AS BIGINT) - $start) / $width AS INTEGER) AS b,
       count(*) AS c, count(*) FILTER (WHERE {isErr}) AS e
FROM {_spans} WHERE {where} GROUP BY op, b";
        await using (var cmd = Command(conn, seriesSql, bindW))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var op = (string)r["op"];
                if (!series.TryGetValue(op, out var agg)) series[op] = agg = (new long[buckets], new long[buckets]);
                var b = Math.Clamp(Convert.ToInt32(r["b"]), 0, buckets - 1);
                agg.C[b] += Convert.ToInt64(r["c"]);
                agg.E[b] += Convert.ToInt64(r["e"]);
            }

        return top.Select(t =>
        {
            var (cc, ee) = series.GetValueOrDefault(t.Op, (new long[buckets], new long[buckets]));
            var sb = new List<SeriesBucket>(buckets);
            for (var i = 0; i < buckets; i++)
                sb.Add(new SeriesBucket(query.Window.StartUnixNano + (long)i * width, cc[i], ee[i]));
            return new OperationSeries(t.Op, t.Count, t.Err, t.P95, sb);
        }).ToList();
    }

    // Metric reads fetch rows and reduce in C# (like the issue series): the gauge count is low-volume, and it
    // avoids DuckDB window/arg_max and DOUBLE-cast quirks over the Postgres json scanner. Value is read as text
    // and parsed invariantly.
    public async Task<IReadOnlyList<MetricLatest>> GetLatestMetricsAsync(Guid projectId, TimeWindow window, CancellationToken ct = default)
    {
        var sql = $@"
SELECT json_extract_string(data,'$.Name') AS name,
       json_extract_string(data,'$.ServiceId') AS svc,
       json_extract_string(data,'$.Value') AS val,
       CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) AS t
FROM {_metrics}
WHERE json_extract_string(data,'$.ProjectId') = $projectId
  AND CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) >= $start
  AND CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) < $end";
        var latest = new Dictionary<(string, string), (double Val, long T)>();
        try
        {
            await using var lease = await OpenAsync(ct);
            await using var cmd = Command(lease.Conn, sql,
                [("projectId", projectId.ToString()), ("start", window.StartUnixNano), ("end", window.EndUnixNano)]);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = ((string)r["name"], (string)r["svc"]);
                var t = Convert.ToInt64(r["t"]);
                if (!latest.TryGetValue(key, out var cur) || t >= cur.T) latest[key] = (ParseDouble(r["val"]), t);
            }
        }
        catch (DuckDBException)
        {
            // Metric table not created yet (Marten makes it on first metric write): no metrics is empty, not an error.
            return [];
        }
        return latest
            .Select(kv => new MetricLatest(kv.Key.Item1, Guid.Parse(kv.Key.Item2), kv.Value.Val, kv.Value.T))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<MetricBucket>> GetMetricSeriesAsync(Guid projectId, string name, TimeWindow window, int buckets, Guid? serviceId = null, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        var width = Math.Max(1, (window.EndUnixNano - window.StartUnixNano) / buckets);
        var where = "json_extract_string(data,'$.ProjectId') = $projectId"
            + " AND json_extract_string(data,'$.Name') = $name"
            + " AND CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) >= $start"
            + " AND CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) < $end";
        var bind = new List<(string, object)>
        {
            ("projectId", projectId.ToString()), ("name", name),
            ("start", window.StartUnixNano), ("end", window.EndUnixNano),
        };
        if (serviceId is Guid svc)
        {
            where += " AND json_extract_string(data,'$.ServiceId') = $serviceId";
            bind.Add(("serviceId", svc.ToString()));
        }
        var sql = $@"
SELECT CAST(json_extract_string(data,'$.TimeUnixNano') AS BIGINT) AS t, json_extract_string(data,'$.Value') AS val
FROM {_metrics} WHERE {where}";
        var last = new double[buckets];
        var lastAt = new long[buckets];
        var seen = new bool[buckets];
        try
        {
            await using var lease = await OpenAsync(ct);
            await using var cmd = Command(lease.Conn, sql, bind.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var t = Convert.ToInt64(r["t"]);
                var b = (int)Math.Clamp((t - window.StartUnixNano) / width, 0, buckets - 1);
                if (!seen[b] || t >= lastAt[b]) { last[b] = ParseDouble(r["val"]); lastAt[b] = t; seen[b] = true; }
            }
        }
        catch (DuckDBException)
        {
            // Metric table not created yet: return a zero-filled series rather than erroring.
        }
        var series = new List<MetricBucket>(buckets);
        for (var i = 0; i < buckets; i++)
            series.Add(new MetricBucket(window.StartUnixNano + (long)i * width, last[i]));
        return series;
    }

    private static double ParseDouble(object value) =>
        value is DBNull or null ? 0 : double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    public async Task<IReadOnlyList<IssueSeries>> GetIssueSeriesAsync(Guid projectId, TimeWindow window, int buckets, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        var width = Math.Max(1, (window.EndUnixNano - window.StartUnixNano) / buckets);
        var byFp = new Dictionary<string, long[]>();
        var sessions = new Dictionary<string, HashSet<string>>();
        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;

        async Task Bucketize(string table, string timeField)
        {
            var sql = $@"
SELECT json_extract_string(data,'$.Fingerprint') AS fp,
       CAST((CAST(json_extract_string(data,'$.{timeField}') AS BIGINT) - $start) / $width AS INTEGER) AS b,
       count(*) AS c
FROM {table}
WHERE json_extract_string(data,'$.ProjectId') = $projectId
  AND json_extract_string(data,'$.Fingerprint') IS NOT NULL
  AND CAST(json_extract_string(data,'$.{timeField}') AS BIGINT) >= $start
  AND CAST(json_extract_string(data,'$.{timeField}') AS BIGINT) < $end
GROUP BY fp, b";
            await using var cmd = Command(conn, sql,
                [("projectId", projectId.ToString()), ("start", window.StartUnixNano), ("end", window.EndUnixNano), ("width", width)]);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var fp = (string)r["fp"];
                if (!byFp.TryGetValue(fp, out var arr)) byFp[fp] = arr = new long[buckets];
                arr[Math.Clamp(Convert.ToInt32(r["b"]), 0, buckets - 1)] += Convert.ToInt64(r["c"]);
            }
        }

        async Task Sessions(string table)
        {
            var sql = $@"
SELECT DISTINCT json_extract_string(data,'$.Fingerprint') AS fp,
       json_extract_string(data,'$.Attributes.""tamp.session.id""') AS sid
FROM {table}
WHERE json_extract_string(data,'$.ProjectId') = $projectId
  AND json_extract_string(data,'$.Fingerprint') IS NOT NULL
  AND json_extract_string(data,'$.Attributes.""tamp.session.id""') IS NOT NULL";
            await using var cmd = Command(conn, sql, [("projectId", projectId.ToString())]);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var fp = (string)r["fp"];
                if (!sessions.TryGetValue(fp, out var set)) sessions[fp] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add((string)r["sid"]);
            }
        }

        await Bucketize(_spans, "StartUnixNano");
        await Bucketize(_logs, "TimeUnixNano");
        await Sessions(_spans);
        await Sessions(_logs);

        return byFp.Select(kv => new IssueSeries(kv.Key, kv.Value, sessions.TryGetValue(kv.Key, out var s) ? s.Count : 0)).ToList();
    }

    public async Task<IssueOccurrence?> GetLatestOccurrenceByTraceAsync(Guid projectId, string traceId, CancellationToken ct = default)
    {
        const string match = "json_extract_string(data,'$.TraceId') = $match"
                           + " AND json_extract_string(data,'$.Fingerprint') IS NOT NULL";
        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;
        var span = await LatestOccurrence(conn, _spans, "StartUnixNano", "span", projectId, match, traceId, ct);
        var log = await LatestOccurrence(conn, _logs, "TimeUnixNano", "log", projectId, match, traceId, ct);
        if (span is null) return log;
        if (log is null) return span;
        return span.AtUnixNano >= log.AtUnixNano ? span : log;
    }

    public async Task<ExceptionDetail?> GetLatestExceptionAsync(Guid projectId, string fingerprint, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var conn = lease.Conn;
        var span = await LatestException(conn, _spans, "StartUnixNano", projectId, fingerprint, ct);
        var log = await LatestException(conn, _logs, "TimeUnixNano", projectId, fingerprint, ct);
        if (span is null) return log?.Detail;
        if (log is null) return span?.Detail;
        return span.Value.At >= log.Value.At ? span.Value.Detail : log.Value.Detail;
    }

    private async Task<(ExceptionDetail Detail, long At)?> LatestException(
        DuckDBConnection conn, string table, string timeField, Guid projectId, string fingerprint, CancellationToken ct)
    {
        var sql = $@"
SELECT json_extract_string(data,'$.Attributes.""exception.type""') AS etype,
       json_extract_string(data,'$.Attributes.""exception.message""') AS emsg,
       json_extract_string(data,'$.Attributes.""exception.stacktrace""') AS estack,
       CAST(json_extract_string(data,'$.{timeField}') AS BIGINT) AS at_nano
FROM {table}
WHERE json_extract_string(data,'$.ProjectId') = $projectId
  AND json_extract_string(data,'$.Fingerprint') = $fingerprint
ORDER BY at_nano DESC LIMIT 1";
        await using var cmd = Command(conn, sql, [("projectId", projectId.ToString()), ("fingerprint", fingerprint)]);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        var detail = new ExceptionDetail(r["etype"] as string, r["emsg"] as string, r["estack"] as string);
        return (detail, Convert.ToInt64(r["at_nano"]));
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

    // Acquire the shared connection under the gate, (re)building it if absent or broken. The returned lease
    // releases the gate on dispose; it never closes the shared connection.
    private async Task<Lease> OpenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_shared is null || _shared.State != ConnectionState.Open)
            {
                _shared?.Dispose();
                _shared = await CreateAsync(ct);
            }
            return new Lease(_gate, _shared);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private async Task<DuckDBConnection> CreateAsync(CancellationToken ct)
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

    public void Dispose()
    {
        _shared?.Dispose();
        _gate.Dispose();
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

    // Parse the span Attributes JSON object (string values) back into a dictionary; tolerant of nulls/non-strings.
    private static Dictionary<string, string> ParseAttrs(string? json)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return dict;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    dict[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? p.Value.GetString() ?? "" : p.Value.ToString();
        }
        catch
        {
            // malformed attributes blob: skip
        }
        return dict;
    }

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
