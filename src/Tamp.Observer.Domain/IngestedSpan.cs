namespace Tamp.Observer.Domain;

/// <summary>
/// A stored span, promoted by the evaluator on admit (ADR 0004). This is part of the one write model
/// (ADR 0006): flat fields, stamped with the resolved entity references so every signal is queryable
/// by Project / Service / Environment / Version. Designed flat (no deep nesting) so the same shape is
/// acceptable to the columnar top tier (ADR 0005 section 6).
/// </summary>
public class IngestedSpan
{
    public Guid Id { get; set; }

    // Resolved entity references (ADR 0007).
    public Guid ProjectId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public Guid VersionId { get; set; }

    // Span identity and shape.
    public required string TraceId { get; set; }
    public required string SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public required string Name { get; set; }
    public int Kind { get; set; }
    public long StartUnixNano { get; set; }
    public long EndUnixNano { get; set; }

    /// <summary>End minus start, stored so it is queryable/aggregatable by the read tier (ADR 0006).</summary>
    public long DurationNano { get; set; }
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }

    // Event-stamped dimensions (ADR 0007).
    public string InstanceId { get; set; } = Synthetic.UnknownInstance;

    // String-valued span attributes (kept flat for the superset write model).
    public Dictionary<string, string> Attributes { get; set; } = new();

    // Provenance from the raw envelope (ADR 0003).
    public required string ReceiptId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
