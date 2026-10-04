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

/// <summary>One bucket of a time series: the bucket's start (Unix nanos), the volume + errors in it, and (when
/// served from the rollup, TOBS-25) the p95 latency in nanos and the bytes ingested in the bucket. P95Nano/Bytes
/// default to 0 for providers/signals that do not carry them.</summary>
public sealed record SeriesBucket(long StartUnixNano, long Count, long ErrorCount, long P95Nano = 0, long Bytes = 0);

/// <summary>An Issue's occurrence series over a window: dense per-bucket counts and distinct session count (TOBS-25).</summary>
public sealed record IssueSeries(string Fingerprint, IReadOnlyList<long> Buckets, int Sessions);

/// <summary>A top operation with its calls/errors, p95 latency (nanos), and a dense call-volume series over the
/// window (TOBS-25). Drives the Overview operations table's per-op p95 and calls sparkline.</summary>
public sealed record OperationSeries(string Operation, long Count, long ErrorCount, long P95Nano, IReadOnlyList<SeriesBucket> Buckets);

/// <summary>The latest value of a gauge/sum metric for a (name, service) over the window (TOBS-43). Drives the
/// current gauge value and per-service up/down tiles.</summary>
public sealed record MetricLatest(string Name, Guid ServiceId, double Value, long TimeUnixNano);

/// <summary>One bucket of a metric series: the bucket start (Unix nanos) and the gauge's last value in it (TOBS-43).</summary>
public sealed record MetricBucket(long StartUnixNano, double Value);

/// <summary>The exception detail of an Issue's latest occurrence: the OTLP exception.* attributes, when present
/// (TOBS-27). Stacktrace is the raw string; the API parses it into frames.</summary>
public sealed record ExceptionDetail(string? Type, string? Message, string? Stacktrace);

/// <summary>The spans and logs sharing a trace id (the correlation walk, ADR 0010).</summary>
public sealed record TraceView(IReadOnlyList<IngestedSpan> Spans, IReadOnlyList<IngestedLog> Logs);

/// <summary>
/// Scope for a log query (TOBS-38 logs explorer). A Project + window, plus optional narrowing filters:
/// Service / Environment / Version entity refs, minimum OTLP severity, exact logger Category
/// (<c>attributes["log.category"]</c>), case-insensitive free-text <see cref="Search"/> over the body, and the
/// correlation keys TraceId / SessionId. <see cref="BeforeUnixNano"/> pages OLDER records (keyset pagination:
/// return rows strictly before this time, newest first) so the explorer can "load older" without OFFSET scans.
/// <see cref="AfterUnixNano"/> + <see cref="Ascending"/> drive live tail: return rows strictly NEWER than the
/// cursor, oldest-first, so the client appends and advances the cursor each poll (pull-based tail, no persistent
/// connection -- consistent with the air-gap posture).
/// All filters are optional; the baseline query (project + window, newest-first, bounded by Limit) is unchanged.
/// </summary>
public sealed record LogQuery(
    Guid ProjectId,
    TimeWindow Window,
    Guid? ServiceId = null,
    int? MinSeverityNumber = null,
    int Limit = 200,
    string? Category = null,
    string? Search = null,
    Guid? EnvironmentId = null,
    Guid? VersionId = null,
    string? TraceId = null,
    string? SessionId = null,
    long? BeforeUnixNano = null,
    long? AfterUnixNano = null,
    bool Ascending = false);

/// <summary>The latest occurrence of an Issue: the span or log that most recently matched it, with the
/// correlation keys needed for the walk (ADR 0014). Fingerprint links it back to its Issue (needed when the
/// walk is anchored on a trace or session rather than an issue). TraceId/SessionId are null when not captured.</summary>
public sealed record IssueOccurrence(
    string Source, long AtUnixNano, string? TraceId, string? SpanId, string? SessionId, Guid ServiceId, string? Fingerprint);

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

    /// <summary>The latest error occurrence (span or log) stamped with the given Issue fingerprint, or null if
    /// none is stored. Drives the correlation walk: from an Issue to its most recent trace and session.</summary>
    Task<IssueOccurrence?> GetLatestOccurrenceAsync(Guid projectId, string fingerprint, CancellationToken ct = default);

    /// <summary>A dense time series of span volume + error count over the window, split into <paramref name="buckets"/>
    /// equal buckets (TOBS-25). Drives the overview error-rate chart and request/error sparklines.</summary>
    Task<IReadOnlyList<SeriesBucket>> GetSpanSeriesAsync(SpanQuery query, int buckets, CancellationToken ct = default);

    /// <summary>Per-Issue occurrence series over the window: for each fingerprint, a dense bucket count plus the
    /// number of distinct sessions that hit it (TOBS-25). Drives the per-issue sparkline and session counts.</summary>
    Task<IReadOnlyList<IssueSeries>> GetIssueSeriesAsync(Guid projectId, TimeWindow window, int buckets, CancellationToken ct = default);

    /// <summary>Top operations over the window with calls/errors, p95 latency, and a dense call-volume series each
    /// (TOBS-25). Providers back this from their rollup; the default is empty until a provider implements it.</summary>
    Task<IReadOnlyList<OperationSeries>> GetOperationSeriesAsync(SpanQuery query, int buckets, int limit = 10, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OperationSeries>>([]);

    /// <summary>Latest value per (metric name, service) over the window (TOBS-43). Default empty until a provider
    /// implements the metric store reads.</summary>
    Task<IReadOnlyList<MetricLatest>> GetLatestMetricsAsync(Guid projectId, TimeWindow window, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MetricLatest>>([]);

    /// <summary>Dense per-bucket last value of a named gauge over the window (TOBS-43), optionally for one service.
    /// Default empty until a provider implements it.</summary>
    Task<IReadOnlyList<MetricBucket>> GetMetricSeriesAsync(Guid projectId, string name, TimeWindow window, int buckets, Guid? serviceId = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MetricBucket>>([]);

    /// <summary>The exception detail (type/message/stacktrace attributes) of the most recent occurrence carrying
    /// one for the fingerprint, or null (TOBS-27). Drives the stack-trace panel.</summary>
    Task<ExceptionDetail?> GetLatestExceptionAsync(Guid projectId, string fingerprint, CancellationToken ct = default);

    /// <summary>The latest error occurrence tied to a session id, or null. Anchors the correlation walk on a
    /// session (the replay page) and resolves back to its Issue via the occurrence fingerprint.</summary>
    Task<IssueOccurrence?> GetLatestOccurrenceBySessionAsync(Guid projectId, string sessionId, CancellationToken ct = default);

    /// <summary>The latest error occurrence within a trace, or null. Anchors the correlation walk on a trace
    /// (the trace page) and resolves back to its Issue via the occurrence fingerprint.</summary>
    Task<IssueOccurrence?> GetLatestOccurrenceByTraceAsync(Guid projectId, string traceId, CancellationToken ct = default);
}
