namespace Tamp.Observer.Domain;

/// <summary>
/// Discovered from OTLP <c>service.name</c> and auto-registered on first sight (ADR 0007). The
/// application/service grain. Identity is <c>service.name</c> scoped within a Project.
/// </summary>
public class Service
{
    /// <summary>Marten document identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning Project (the trust root this Service was discovered within).</summary>
    public Guid ProjectId { get; set; }

    /// <summary>OTLP <c>service.name</c>.</summary>
    public required string ServiceName { get; set; }

    /// <summary>
    /// OTLP <c>service.namespace</c>, when present. Whether this folds into Service identity is an open
    /// sub-decision (ADR 0007 §4); leaning toward folding it in when present, with a synthetic default
    /// when absent. Kept here so the data is captured either way.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>When this Service was first seen at the collector.</summary>
    public DateTimeOffset FirstSeenAtUtc { get; set; }
}
