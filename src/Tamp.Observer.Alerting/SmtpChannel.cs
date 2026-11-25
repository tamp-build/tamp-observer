using System.Net;
using System.Net.Mail;

namespace Tamp.Observer.Alerting;

/// <summary>SMTP settings for <see cref="SmtpChannel"/> (ADR 0016).</summary>
public sealed record SmtpChannelOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 25;

    /// <summary>
    /// Encrypt the connection. <b>Defaults to true</b>, deliberately: the safe configuration should be
    /// what you get by saying nothing, and the unsafe one should have to be asked for. On port 25 this
    /// means STARTTLS, which is what an internal relay normally offers.
    /// </summary>
    /// <remarks>
    /// This used to default to false, which meant an operator who configured a host, port, username and
    /// password — and nothing else — sent SMTP AUTH credentials in plaintext, with nothing in the code or
    /// the config surface signalling it, and it appeared to work perfectly (csharpsquid:S5332).
    /// </remarks>
    public bool UseSsl { get; init; } = true;

    /// <summary>
    /// Permit SMTP AUTH over an unencrypted connection. Without this, <see cref="SmtpChannel.SendAsync"/>
    /// refuses to send credentials when <see cref="UseSsl"/> is off, rather than quietly putting them on
    /// the wire. Only set it for a relay you control on a path you trust — a loopback or in-enclave MTA
    /// that terminates TLS elsewhere.
    /// </summary>
    public bool AllowPlaintextCredentials { get; init; }

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
        // Refuse before touching the network. Putting AUTH credentials on an unencrypted
        // connection is not something to do as a side effect of an unset option -- if an operator
        // genuinely wants it, AllowPlaintextCredentials says so out loud.
        if (!options.UseSsl && !string.IsNullOrEmpty(options.Username) && !options.AllowPlaintextCredentials)
            throw new InvalidOperationException(
                $"SMTP channel refuses to send credentials to {options.Host}:{options.Port} over an " +
                "unencrypted connection. Set UseSsl (preferred), or set AllowPlaintextCredentials " +
                "if the path to this relay is genuinely trusted.");

        using var message = new MailMessage { From = new MailAddress(options.From), Subject = AlertText.Format(alert), Body = AlertText.Format(alert) };
        foreach (var to in options.To)
            message.To.Add(to);

        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseSsl };
        if (!string.IsNullOrEmpty(options.Username))
            client.Credentials = new NetworkCredential(options.Username, options.Password);

        await client.SendMailAsync(message, ct);
    }
}
