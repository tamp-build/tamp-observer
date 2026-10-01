using System.Text.Json;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.ClickHouse;

/// <summary>
/// The ClickHouse implementation of the write contract (ADR 0006) for the telemetry tier. Persists the
/// batch's spans and logs via bulk insert. Entity provisioning (Service/Environment/Version) belongs to
/// the Postgres system of record (ADR 0005 section 7), so the entity lists are intentionally not written
/// here; a composite tiered sink fans entities to Postgres and telemetry to ClickHouse.
/// </summary>
public sealed class ClickHouseEventSink(string connectionString) : IEventSink
{
    private static readonly string[] SpanColumns =
    [
        "project_id", "service_id", "environment_id", "version_id", "trace_id", "span_id",
        "parent_span_id", "name", "kind", "start_unix_nano", "end_unix_nano", "duration_nano",
        "status_code", "status_message", "instance_id", "attributes", "receipt_id", "received_at",
    ];

    private static readonly string[] LogColumns =
    [
        "project_id", "service_id", "environment_id", "version_id", "time_unix_nano", "severity_number",
        "severity_text", "body", "trace_id", "span_id", "instance_id", "attributes", "receipt_id", "received_at",
    ];

    public async Task WriteAsync(AdmittedBatch batch, CancellationToken ct = default)
    {
        if (batch.Spans.Count == 0 && batch.Logs.Count == 0)
            return;

        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);

        if (batch.Spans.Count > 0)
            await BulkInsert(conn, ClickHouseSchema.SpansTable, SpanColumns, batch.Spans.Select(SpanRow), ct);

        if (batch.Logs.Count > 0)
            await BulkInsert(conn, ClickHouseSchema.LogsTable, LogColumns, batch.Logs.Select(LogRow), ct);
    }

    private static async Task BulkInsert(
        ClickHouseConnection conn, string table, string[] columns, IEnumerable<object?[]> rows, CancellationToken ct)
    {
        using var bulk = new ClickHouseBulkCopy(conn)
        {
            DestinationTableName = table,
            ColumnNames = columns,
            BatchSize = 100_000,
        };
        await bulk.InitAsync();
        await bulk.WriteToServerAsync(rows, ct);
    }

    private static object?[] SpanRow(IngestedSpan s) =>
    [
        s.ProjectId, s.ServiceId, (object?)s.EnvironmentId, s.VersionId, s.TraceId, s.SpanId,
        (object?)s.ParentSpanId, s.Name, s.Kind, s.StartUnixNano, s.EndUnixNano, s.DurationNano,
        s.StatusCode, (object?)s.StatusMessage, s.InstanceId, Json(s.Attributes), s.ReceiptId, s.ReceivedAt.UtcDateTime,
    ];

    private static object?[] LogRow(IngestedLog l) =>
    [
        l.ProjectId, l.ServiceId, (object?)l.EnvironmentId, l.VersionId, l.TimeUnixNano, l.SeverityNumber,
        (object?)l.SeverityText, (object?)l.Body, (object?)l.TraceId, (object?)l.SpanId, l.InstanceId,
        Json(l.Attributes), l.ReceiptId, l.ReceivedAt.UtcDateTime,
    ];

    private static string Json(Dictionary<string, string> attrs) => JsonSerializer.Serialize(attrs);
}
