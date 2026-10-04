namespace Tamp.Observer.Domain;

/// <summary>
/// A stored metric gauge/sum data point, promoted by the evaluator on admit (ADR 0004, TOBS-43). Flat and
/// stamped with resolved entity references like spans/logs (one write model, ADR 0006). Scope is gauge/sum
/// number points today (e.g. Sample gauge, per-service up/down); histograms/summaries are deferred.
/// </summary>
public class IngestedMetric
{
    public Guid Id { get; set; }

    // Resolved entity references (ADR 0007).
    public Guid ProjectId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public Guid VersionId { get; set; }

    /// <summary>The metric name (e.g. "sample.active_count", "up").</summary>
    public required string Name { get; set; }

    /// <summary>The numeric value at <see cref="TimeUnixNano"/> (int points are widened to double).</summary>
    public double Value { get; set; }
    public long TimeUnixNano { get; set; }

    public string InstanceId { get; set; } = Synthetic.UnknownInstance;

    // String-valued data-point attributes (kept flat for the superset write model).
    public Dictionary<string, string> Attributes { get; set; } = [];

    // Provenance from the raw envelope (ADR 0003).
    public required string ReceiptId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
