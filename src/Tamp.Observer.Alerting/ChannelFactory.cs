using Tamp.Observer.Domain;

namespace Tamp.Observer.Alerting;

/// <summary>
/// Builds <see cref="INotificationChannel"/> instances from persisted <see cref="ChannelSettings"/> (ADR 0016,
/// TOBS-29). Shared by the admin API (send-test, which builds a single channel) and the evaluator host (which
/// builds every enabled channel for live dispatch), so the two never drift on what "configured" means.
/// </summary>
public static class ChannelFactory
{
    /// <summary>The channel type keys this installation supports, in display order.</summary>
    public static readonly IReadOnlyList<string> Types = ["smtp", "slack", "telegram"];

    /// <summary>True for channels that leave the installation's network (gated under enforcing/locked).</summary>
    public static bool IsReachback(string type) => type is "slack" or "telegram";

    /// <summary>The outside host a reachback channel reaches, for the "unavailable" explanation; null otherwise.</summary>
    public static string? OutsideHost(string type) => type switch
    {
        "slack" => "hooks.slack.com",
        "telegram" => "api.telegram.org",
        _ => null,
    };

    /// <summary>True when the channel has enough configuration to send, ignoring its enabled flag.</summary>
    public static bool Configured(string type, ChannelSettings s) => type switch
    {
        "smtp" => s.Smtp.Configured,
        "slack" => s.Slack.Configured,
        "telegram" => s.Telegram.Configured,
        _ => false,
    };

    /// <summary>Build one channel from settings, or null if that type is not configured.</summary>
    public static INotificationChannel? Build(string type, ChannelSettings s, HttpClient http) => type switch
    {
        "smtp" when s.Smtp.Configured => new SmtpChannel(new SmtpChannelOptions
        {
            Host = s.Smtp.Host!,
            From = s.Smtp.From!,
            To = s.Smtp.To!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        }),
        "slack" when s.Slack.Configured => new SlackChannel(http, s.Slack.WebhookUrl!),
        "telegram" when s.Telegram.Configured => new TelegramChannel(http, s.Telegram.BotToken!, s.Telegram.ChatId!),
        _ => null,
    };

    /// <summary>Build every enabled and configured channel for live dispatch.</summary>
    public static List<INotificationChannel> BuildEnabled(ChannelSettings s, HttpClient http)
    {
        var list = new List<INotificationChannel>();
        if (s.Smtp.Enabled && Build("smtp", s, http) is { } smtp) list.Add(smtp);
        if (s.Slack.Enabled && Build("slack", s, http) is { } slack) list.Add(slack);
        if (s.Telegram.Enabled && Build("telegram", s, http) is { } tg) list.Add(tg);
        return list;
    }

    /// <summary>Translate the stored routing matrix to a dispatcher map. Planned alert kinds that are not real
    /// <see cref="AlertKind"/> values are dropped (they cannot fire yet).</summary>
    public static Dictionary<AlertKind, IReadOnlySet<string>> Routing(ChannelSettings s)
    {
        var map = new Dictionary<AlertKind, IReadOnlySet<string>>();
        foreach (var (kind, channels) in s.Routing)
            if (Enum.TryParse<AlertKind>(kind, ignoreCase: true, out var k))
                map[k] = channels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return map;
    }
}
