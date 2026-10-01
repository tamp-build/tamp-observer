namespace Tamp.Observer.Domain;

/// <summary>
/// A rejected event in durable, queryable custody (ADR 0004 section 5). Quarantine is distinct from
/// the transient raw bucket: raw is flow, quarantine is custody. Rejected events sit here with a
/// reason, inspectable, which is the only debugging surface for an air-gapped site you cannot
/// interrogate live.
/// </summary>
public class QuarantinedEvent
{
    /// <summary>Marten document identity.</summary>
    public Guid Id { get; set; }

    /// <summary>The receipt id from the raw envelope that landed this event.</summary>
    public required string ReceiptId { get; set; }

    /// <summary>When the collector received it (from the envelope).</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>When the evaluator quarantined it.</summary>
    public DateTimeOffset QuarantinedAt { get; set; }

    /// <summary>Signal type (traces / metrics / logs).</summary>
    public required string Signal { get; set; }

    /// <summary>Front-door / transport source (from the envelope).</summary>
    public string? Source { get; set; }

    /// <summary>Why it was rejected (e.g. unknown project, no resource).</summary>
    public required string Reason { get; set; }

    /// <summary>The project key the event claimed, if any (for triage).</summary>
    public string? ClaimedProjectKey { get; set; }

    /// <summary>Size of the raw payload that was rejected.</summary>
    public int PayloadBytes { get; set; }
}
