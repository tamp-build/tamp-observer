namespace Tamp.Observer.Domain;

/// <summary>
/// A stored log record, promoted by the evaluator on admit (ADR 0004). Part of the one write model
/// (ADR 0006), stamped with resolved entity references and carrying the trace/span correlation keys so
/// the error-to-trace-to-log walk (the correlation experience) is computable later.
/// </summary>
public class IngestedLog
{
    public Guid Id { get; set; }

    // Resolved entity references (ADR 0007).
    public Guid ProjectId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public Guid VersionId { get; set; }

    // Log record fields.
    public long TimeUnixNano { get; set; }
    public int SeverityNumber { get; set; }
    public string? SeverityText { get; set; }
    public string? Body { get; set; }

    // Correlation keys (ADR 0010): empty when the log is not tied to a trace.
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }

    public string InstanceId { get; set; } = Synthetic.UnknownInstance;

    public Dictionary<string, string> Attributes { get; set; } = new();

    // Provenance from the raw envelope (ADR 0003).
    public required string ReceiptId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
