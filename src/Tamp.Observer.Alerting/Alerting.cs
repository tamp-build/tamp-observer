using Tamp.Observer.Domain;

namespace Tamp.Observer.Alerting;

/// <summary>What happened to an Issue that is worth alerting on (ADR 0016).</summary>
public enum AlertKind
{
    NewIssue,
    RegressedIssue,
}

/// <summary>A channel-agnostic alert produced by the alerting core.</summary>
public sealed record AlertEvent(
    AlertKind Kind,
    Guid ProjectId,
    Guid ServiceId,
    Guid IssueId,
    string Fingerprint,
    string Title,
    string? ErrorType,
    long Count,
    long VersionSequence);

/// <summary>Renders an alert to a short human line shared by all channels.</summary>
public static class AlertText
{
    public static string Format(AlertEvent a)
    {
        var tag = a.Kind == AlertKind.NewIssue ? "NEW" : "REGRESSED";
        var what = a.ErrorType ?? a.Title;
        return $"[{tag}] {what} (count {a.Count}, version seq {a.VersionSequence})";
    }
}

/// <summary>
/// A notification sink (ADR 0016). Implementations ship for SMTP, Telegram, Slack; the operator enables
/// one or more. <see cref="IsReachback"/> marks a channel that leaves the installation's network (a cloud
/// channel), which enforcement can gate off under lockdown (ADR 0002).
/// </summary>
public interface INotificationChannel
{
    string Name { get; }
    bool IsReachback { get; }
    Task SendAsync(AlertEvent alert, CancellationToken ct = default);
}

/// <summary>
/// The alerting core's fan-out (ADR 0016): sends each alert to each enabled channel. Reachback channels
/// are skipped when the enforcement gate refuses <see cref="Loosening.ComponentReachback"/> (locked /
/// air-gapped). Per-channel failures are isolated; delivery is best-effort and never blocks ingest.
/// </summary>
public interface IAlertDispatcher
{
    Task DispatchAsync(IReadOnlyList<AlertEvent> alerts, IEnforcementGate gate, CancellationToken ct = default);
}

/// <summary>Fans out to the enabled channels, honoring the enforcement gate for reachback channels.</summary>
public sealed class AlertDispatcher(IEnumerable<INotificationChannel> channels, Action<string, Exception>? onError = null)
    : IAlertDispatcher
{
    private readonly IReadOnlyList<INotificationChannel> _channels = [.. channels];

    public async Task DispatchAsync(IReadOnlyList<AlertEvent> alerts, IEnforcementGate gate, CancellationToken ct = default)
    {
        if (_channels.Count == 0 || alerts.Count == 0)
            return;

        foreach (var alert in alerts)
        {
            foreach (var channel in _channels)
            {
                if (channel.IsReachback && !gate.Allows(Loosening.ComponentReachback, advisoryOverride: true))
                    continue; // reachback refused under enforcing/locked: the path is absent.

                try
                {
                    await channel.SendAsync(alert, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best-effort: one channel's failure never blocks the others or the pipeline.
                    onError?.Invoke(channel.Name, ex);
                }
            }
        }
    }
}
