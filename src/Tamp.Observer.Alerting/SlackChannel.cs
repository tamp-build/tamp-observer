using System.Net.Http.Json;

namespace Tamp.Observer.Alerting;

/// <summary>Posts to a Slack incoming webhook (ADR 0016). Reachback: a cloud channel.</summary>
public sealed class SlackChannel(HttpClient http, string webhookUrl) : INotificationChannel
{
    public string Name => "slack";
    public bool IsReachback => true;

    public async Task SendAsync(AlertEvent alert, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(webhookUrl, new { text = AlertText.Format(alert) }, ct);
        response.EnsureSuccessStatusCode();
    }
}
