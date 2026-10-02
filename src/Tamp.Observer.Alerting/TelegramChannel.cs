using System.Net.Http.Json;

namespace Tamp.Observer.Alerting;

/// <summary>Sends via the Telegram Bot API (ADR 0016). Reachback: a cloud channel.</summary>
public sealed class TelegramChannel(HttpClient http, string botToken, string chatId) : INotificationChannel
{
    public string Name => "telegram";
    public bool IsReachback => true;

    public async Task SendAsync(AlertEvent alert, CancellationToken ct = default)
    {
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        using var response = await http.PostAsJsonAsync(url, new { chat_id = chatId, text = AlertText.Format(alert) }, ct);
        response.EnsureSuccessStatusCode();
    }
}
