namespace Tamp.Observer.Domain;

/// <summary>Lifecycle of an <see cref="Issue"/> (ADR 0015).</summary>
public enum IssueStatus
{
    /// <summary>Active, not yet acted on.</summary>
    Unresolved = 0,
    /// <summary>A human marked it resolved (records the resolving version).</summary>
    Resolved = 1,
    /// <summary>A human chose to ignore it.</summary>
    Ignored = 2,
    /// <summary>A resolved issue recurred in a later version; active again.</summary>
    Regressed = 3,
}

/// <summary>
/// The core error-grouping entity (ADR 0015): N occurrences of the same bug collapsed into one record,
/// keyed by a stable fingerprint within a Service, with count, timing, affected versions, and a status.
/// The resolved/regressed state machine is computable thanks to the server-assigned Version sequence
/// (ADR 0008).
/// </summary>
public class Issue
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }
    public Guid ServiceId { get; set; }

    /// <summary>Stable grouping key (hash) within the Service.</summary>
    public required string Fingerprint { get; set; }

    /// <summary>Human-facing title (error type, or the operation/body it was derived from).</summary>
    public required string Title { get; set; }

    /// <summary>The exception type when known (from OTLP attributes).</summary>
    public string? ErrorType { get; set; }

    public IssueStatus Status { get; set; } = IssueStatus.Unresolved;

    public long Count { get; set; }
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }

    // Version axis (ADR 0008): sequences make "resolved in N / regressed in N+2" computable.
    public long FirstSeenVersionSequence { get; set; }
    public long LastSeenVersionSequence { get; set; }
    public long? ResolvedInVersionSequence { get; set; }
    public List<long> AffectedVersionSequences { get; set; } = [];
}
