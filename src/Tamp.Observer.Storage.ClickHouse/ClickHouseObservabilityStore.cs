using System.Data.Common;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.ClickHouse;

/// <summary>
/// The ClickHouse translator for <see cref="IObservabilityStore"/> (ADR 0006). The same capability
/// intents as the Postgres provider, but the analytical reductions are pushed DOWN into ClickHouse as
/// native <c>quantile</c> and <c>GROUP BY</c> rather than reduced in-process. This is the concrete payoff
/// of a capability-based interface over shared SQL: the columnar engine does what it is good at.
/// </summary>
public sealed class ClickHouseObservabilityStore(string connectionString) : IObservabilityStore
{
    public async Task<LatencyPercentiles> GetLatencyPercentilesAsync(SpanQuery query, CancellationToken ct = default)
    {
        var where = SpanWhere(query, out var hasService);
        var sql = $@"
SELECT count() AS c,
       quantileExact(0.5)(duration_nano) AS p50,
       quantileExact(0.95)(duration_nano) AS p95,
       quantileExact(0.99)(duration_nano) AS p99
FROM {ClickHouseSchema.SpansTable} WHERE {where}";

        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = (ClickHouseCommand)conn.CreateCommand();
        cmd.CommandText = sql;
        BindSpanParams(cmd, query, hasService);

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
        var where = SpanWhere(query, out var hasService);
        var top = Math.Clamp(limit, 1, 10_000);
        var sql = $@"
SELECT name, count() AS c, countIf(status_code = 2) AS e
FROM {ClickHouseSchema.SpansTable} WHERE {where}
GROUP BY name ORDER BY c DESC, name ASC LIMIT {top}";

        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = (ClickHouseCommand)conn.CreateCommand();
        cmd.CommandText = sql;
        BindSpanParams(cmd, query, hasService);

        var result = new List<OperationStat>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new OperationStat((string)reader["name"], Convert.ToInt64(reader["c"]), Convert.ToInt64(reader["e"])));
        return result;
    }

    public async Task<TraceView> GetTraceAsync(Guid projectId, string traceId, CancellationToken ct = default)
    {
        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);

        var spans = new List<IngestedSpan>();
        await using (var cmd = (ClickHouseCommand)conn.CreateCommand())
        {
            cmd.CommandText = $@"
SELECT project_id, service_id, environment_id, version_id, trace_id, span_id, parent_span_id, name, kind,
       start_unix_nano, end_unix_nano, duration_nano, status_code, status_message, instance_id, receipt_id, received_at
FROM {ClickHouseSchema.SpansTable} WHERE project_id = {{projectId:UUID}} AND trace_id = {{traceId:String}}";
            cmd.AddParameter("projectId", "UUID", projectId);
            cmd.AddParameter("traceId", "String", traceId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                spans.Add(MapSpan(reader));
        }

        var logs = new List<IngestedLog>();
        await using (var cmd = (ClickHouseCommand)conn.CreateCommand())
        {
            cmd.CommandText = $@"
SELECT project_id, service_id, environment_id, version_id, time_unix_nano, severity_number, severity_text, body,
       trace_id, span_id, instance_id, receipt_id, received_at
FROM {ClickHouseSchema.LogsTable} WHERE project_id = {{projectId:UUID}} AND trace_id = {{traceId:String}}";
            cmd.AddParameter("projectId", "UUID", projectId);
            cmd.AddParameter("traceId", "String", traceId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                logs.Add(MapLog(reader));
        }

        return new TraceView(spans, logs);
    }

    public async Task<IReadOnlyList<IngestedLog>> GetLogsAsync(LogQuery query, CancellationToken ct = default)
    {
        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        var where = "project_id = {projectId:UUID} AND time_unix_nano >= {start:Int64} AND time_unix_nano < {end:Int64}";
        if (query.ServiceId is not null)
            where += " AND service_id = {serviceId:UUID}";
        if (query.MinSeverityNumber is not null)
            where += " AND severity_number >= {minSev:Int32}";

        var logs = new List<IngestedLog>();
        await using var cmd = (ClickHouseCommand)conn.CreateCommand();
        cmd.CommandText = $@"
SELECT project_id, service_id, environment_id, version_id, time_unix_nano, severity_number, severity_text, body,
       trace_id, span_id, instance_id, receipt_id, received_at
FROM {ClickHouseSchema.LogsTable} WHERE {where}
ORDER BY time_unix_nano DESC LIMIT {(query.Limit <= 0 ? 200 : query.Limit)}";
        cmd.AddParameter("projectId", "UUID", query.ProjectId);
        cmd.AddParameter("start", "Int64", query.Window.StartUnixNano);
        cmd.AddParameter("end", "Int64", query.Window.EndUnixNano);
        if (query.ServiceId is not null)
            cmd.AddParameter("serviceId", "UUID", query.ServiceId.Value);
        if (query.MinSeverityNumber is not null)
            cmd.AddParameter("minSev", "Int32", query.MinSeverityNumber.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            logs.Add(MapLog(reader));
        return logs;
    }

    public Task<IssueOccurrence?> GetLatestOccurrenceAsync(Guid projectId, string fingerprint, CancellationToken ct = default) =>
        // The ClickHouse tier does not yet carry the Issue fingerprint column (that tier is disabled in the
        // current deployment); the correlation-walk occurrence lookup is unavailable here until it is added.
        Task.FromResult<IssueOccurrence?>(null);

    public Task<IssueOccurrence?> GetLatestOccurrenceBySessionAsync(Guid projectId, string sessionId, CancellationToken ct = default) =>
        Task.FromResult<IssueOccurrence?>(null);

    public Task<IssueOccurrence?> GetLatestOccurrenceByTraceAsync(Guid projectId, string traceId, CancellationToken ct = default) =>
        Task.FromResult<IssueOccurrence?>(null);

    public Task<IReadOnlyList<SeriesBucket>> GetSpanSeriesAsync(SpanQuery query, int buckets, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SeriesBucket>>([]);

    public Task<IReadOnlyList<IssueSeries>> GetIssueSeriesAsync(Guid projectId, TimeWindow window, int buckets, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IssueSeries>>([]);

    private static string SpanWhere(SpanQuery query, out bool hasService)
    {
        hasService = query.ServiceId is not null;
        var where = "project_id = {projectId:UUID} AND start_unix_nano >= {start:Int64} AND start_unix_nano < {end:Int64}";
        return hasService ? where + " AND service_id = {serviceId:UUID}" : where;
    }

    private static void BindSpanParams(ClickHouseCommand cmd, SpanQuery query, bool hasService)
    {
        cmd.AddParameter("projectId", "UUID", query.ProjectId);
        cmd.AddParameter("start", "Int64", query.Window.StartUnixNano);
        cmd.AddParameter("end", "Int64", query.Window.EndUnixNano);
        if (hasService)
            cmd.AddParameter("serviceId", "UUID", query.ServiceId!.Value);
    }

    private static IngestedSpan MapSpan(DbDataReader r) => new()
    {
        ProjectId = (Guid)r["project_id"],
        ServiceId = (Guid)r["service_id"],
        EnvironmentId = NullableGuid(r, "environment_id"),
        VersionId = (Guid)r["version_id"],
        TraceId = (string)r["trace_id"],
        SpanId = (string)r["span_id"],
        ParentSpanId = NullableString(r, "parent_span_id"),
        Name = (string)r["name"],
        Kind = Convert.ToInt32(r["kind"]),
        StartUnixNano = Convert.ToInt64(r["start_unix_nano"]),
        EndUnixNano = Convert.ToInt64(r["end_unix_nano"]),
        DurationNano = Convert.ToInt64(r["duration_nano"]),
        StatusCode = Convert.ToInt32(r["status_code"]),
        StatusMessage = NullableString(r, "status_message"),
        InstanceId = (string)r["instance_id"],
        ReceiptId = (string)r["receipt_id"],
        ReceivedAt = new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(r["received_at"]), DateTimeKind.Utc)),
    };

    private static IngestedLog MapLog(DbDataReader r) => new()
    {
        ProjectId = (Guid)r["project_id"],
        ServiceId = (Guid)r["service_id"],
        EnvironmentId = NullableGuid(r, "environment_id"),
        VersionId = (Guid)r["version_id"],
        TimeUnixNano = Convert.ToInt64(r["time_unix_nano"]),
        SeverityNumber = Convert.ToInt32(r["severity_number"]),
        SeverityText = NullableString(r, "severity_text"),
        Body = NullableString(r, "body"),
        TraceId = NullableString(r, "trace_id"),
        SpanId = NullableString(r, "span_id"),
        InstanceId = (string)r["instance_id"],
        ReceiptId = (string)r["receipt_id"],
        ReceivedAt = new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(r["received_at"]), DateTimeKind.Utc)),
    };

    private static double Dbl(object value) => value is DBNull ? 0 : Convert.ToDouble(value);

    private static Guid? NullableGuid(DbDataReader r, string col)
    {
        var o = r[col];
        return o is DBNull ? null : (Guid)o;
    }

    private static string? NullableString(DbDataReader r, string col)
    {
        var o = r[col];
        return o is DBNull ? null : (string)o;
    }
}
