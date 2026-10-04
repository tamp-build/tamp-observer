namespace Tamp.Observer.Domain;

/// <summary>
/// Installation-wide notification channel configuration (ADR 0016, TOBS-29). One document per installation,
/// the shared source of truth for both the admin API (display, send-test, routing) and the evaluator (live
/// alert dispatch). Secrets (Slack webhook, Telegram bot token) are stored here but never returned to the
/// browser; the admin API masks them.
/// </summary>
public class ChannelSettings
{
    /// <summary>Singleton identity; there is one ChannelSettings per installation.</summary>
    public string Id { get; set; } = SingletonId;

    public const string SingletonId = "channels";

    public SmtpConfig Smtp { get; set; } = new();
    public SlackConfig Slack { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();

    /// <summary>Routing matrix: alert-kind key (<see cref="AlertKind"/> name, or a planned kind) to the set of
    /// channel type keys ("smtp"/"slack"/"telegram") it should reach.</summary>
    public Dictionary<string, List<string>> Routing { get; set; } = new();

    /// <summary>Last send-test outcome per channel type key.</summary>
    public Dictionary<string, ChannelTestResult> LastTest { get; set; } = new();
}

/// <summary>SMTP relay settings. Non-reachback when pointed at an in-enclave relay (ADR 0016).</summary>
public class SmtpConfig
{
    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public string? From { get; set; }

    /// <summary>Comma-separated recipient list.</summary>
    public string? To { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From) && !string.IsNullOrWhiteSpace(To);
}

/// <summary>Slack incoming-webhook settings. Reachback: posts to hooks.slack.com (ADR 0016).</summary>
public class SlackConfig
{
    public bool Enabled { get; set; }
    public string? WebhookUrl { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(WebhookUrl);
}

/// <summary>Telegram Bot API settings. Reachback: posts to api.telegram.org (ADR 0016).</summary>
public class TelegramConfig
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
    public string? ChatId { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

/// <summary>The outcome of a channel send-test, kept so the admin page can show "last test" per channel.</summary>
public class ChannelTestResult
{
    public DateTime AtUtc { get; set; }
    public bool Ok { get; set; }
    public long Ms { get; set; }
    public string? Error { get; set; }
}
