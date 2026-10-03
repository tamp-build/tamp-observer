using Tamp.Observer.Domain;

namespace Tamp.Observer.Storage.Abstractions;

/// <summary>A half-open time window in Unix nanoseconds: [StartUnixNano, EndUnixNano).</summary>
public readonly record struct TimeWindow(long StartUnixNano, long EndUnixNano);

/// <summary>Scope for an analytical span query: a Project, a window, and an optional Service filter.</summary>
public sealed record SpanQuery(Guid ProjectId, TimeWindow Window, Guid? ServiceId = null);

/// <summary>Latency percentiles over a set of spans (nanoseconds).</summary>
public sealed record LatencyPercentiles(long Count, double P50, double P95, double P99);

/// <summary>Per-operation frequency and error count.</summary>
public sealed record OperationStat(string Operation, long Count, long ErrorCount);

/// <summary>The spans and logs sharing a trace id (the correlation walk, ADR 0010).</summary>
public sealed record TraceView(IReadOnlyList<IngestedSpan> Spans, IReadOnlyList<IngestedLog> Logs);

/// <summary>Scope for a log query: a Project, a window, and optional Service / minimum-severity filters.</summary>
public sealed record LogQuery(
    Guid ProjectId, TimeWindow Window, Guid? ServiceId = null, int? MinSeverityNumber = null, int Limit = 200);

/// <summary>
/// The capability-based read interface over the tiered store (ADR 0006). Operations are expressed as
/// INTENT, not SQL: each provider (Postgres/Marten today, DuckDB/ClickHouse later) translates intent to
/// its native dialect. The baseline surface is sized to the weakest intended provider; providers may add
/// provider-specific extensions above it.
/// </summary>
public interface IObservabilityStore
{
    /// <summary>Latency percentiles (p50/p95/p99) of span duration over the query window.</summary>
    Task<LatencyPercentiles> GetLatencyPercentilesAsync(SpanQuery query, CancellationToken ct = default);

    /// <summary>Top operations (span names) by frequency over the window, with their error counts.</summary>
    Task<IReadOnlyList<OperationStat>> GetTopOperationsAsync(SpanQuery query, int limit = 10, CancellationToken ct = default);

    /// <summary>All spans and logs for a trace within a Project (the error-to-trace-to-log walk).</summary>
    Task<TraceView> GetTraceAsync(Guid projectId, string traceId, CancellationToken ct = default);

    /// <summary>Recent logs for a Project over the window, newest first (the log explorer, ADR 0006).</summary>
    Task<IReadOnlyList<IngestedLog>> GetLogsAsync(LogQuery query, CancellationToken ct = default);
}
