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

/// <summary>One bucket of a time series: the bucket's start (Unix nanos) and the volume + errors in it (TOBS-25).</summary>
public sealed record SeriesBucket(long StartUnixNano, long Count, long ErrorCount);

/// <summary>An Issue's occurrence series over a window: dense per-bucket counts and distinct session count (TOBS-25).</summary>
public sealed record IssueSeries(string Fingerprint, IReadOnlyList<long> Buckets, int Sessions);

/// <summary>The spans and logs sharing a trace id (the correlation walk, ADR 0010).</summary>
public sealed record TraceView(IReadOnlyList<IngestedSpan> Spans, IReadOnlyList<IngestedLog> Logs);

/// <summary>Scope for a log query: a Project, a window, and optional Service / minimum-severity filters.</summary>
public sealed record LogQuery(
    Guid ProjectId, TimeWindow Window, Guid? ServiceId = null, int? MinSeverityNumber = null, int Limit = 200);

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

    /// <summary>The latest error occurrence tied to a session id, or null. Anchors the correlation walk on a
    /// session (the replay page) and resolves back to its Issue via the occurrence fingerprint.</summary>
    Task<IssueOccurrence?> GetLatestOccurrenceBySessionAsync(Guid projectId, string sessionId, CancellationToken ct = default);

    /// <summary>The latest error occurrence within a trace, or null. Anchors the correlation walk on a trace
    /// (the trace page) and resolves back to its Issue via the occurrence fingerprint.</summary>
    Task<IssueOccurrence?> GetLatestOccurrenceByTraceAsync(Guid projectId, string traceId, CancellationToken ct = default);
}
