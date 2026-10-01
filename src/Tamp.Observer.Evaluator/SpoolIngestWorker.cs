using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tamp.Observer.Evaluator;

/// <summary>
/// Hosts the evaluator as a polling worker that drains the spool on an interval (ADR 0004). The
/// poll cadence is fine because the rawfile exporter's durable landing means nothing is lost between
/// polls; a future tier can switch to a push/stream drain without changing the evaluator.
/// </summary>
public sealed class SpoolIngestWorker(
    IngestEvaluator evaluator,
    ILogger<SpoolIngestWorker> logger) : BackgroundService
{
    /// <summary>How often to drain the spool.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("spool ingest worker started (interval {Interval})", Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var stats = await evaluator.DrainAsync(stoppingToken);
                if (stats.Total > 0)
                    logger.LogInformation("drained {Total} (admitted {Admitted}, quarantined {Quarantined})",
                        stats.Total, stats.Admitted, stats.Quarantined);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "spool drain failed; will retry next interval");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
