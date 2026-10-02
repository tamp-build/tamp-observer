using System.Net;
using System.Net.Mail;

namespace Tamp.Observer.Alerting;

/// <summary>SMTP settings for <see cref="SmtpChannel"/> (ADR 0016).</summary>
public sealed record SmtpChannelOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 25;
    public bool UseSsl { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required string From { get; init; }
    public required IReadOnlyList<string> To { get; init; }
}

/// <summary>
/// Emails alerts via SMTP (ADR 0016). Treated as a non-reachback channel: the air-gap-appropriate case is
/// an internal relay. (An operator pointing it at an external SMTP service is choosing reachback; the
/// common internal-relay case is modelled here.)
/// </summary>
public sealed class SmtpChannel(SmtpChannelOptions options) : INotificationChannel
{
    public string Name => "smtp";
    public bool IsReachback => false;

    public async Task SendAsync(AlertEvent alert, CancellationToken ct = default)
    {
        using var message = new MailMessage { From = new MailAddress(options.From), Subject = AlertText.Format(alert), Body = AlertText.Format(alert) };
        foreach (var to in options.To)
            message.To.Add(to);

        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseSsl };
        if (!string.IsNullOrEmpty(options.Username))
            client.Credentials = new NetworkCredential(options.Username, options.Password);

        await client.SendMailAsync(message, ct);
    }
}
