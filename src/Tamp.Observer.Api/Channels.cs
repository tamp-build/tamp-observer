using Tamp.Observer.Alerting;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Api;

// Notification-channel admin surface (ADR 0016, TOBS-29, design 7.5): editable per-channel config, send-test,
// and a routing matrix. Secrets (Slack webhook, Telegram bot token) are never serialized back to the browser;
// a field reports only whether it is set, and a blank value on save keeps the stored secret.

/// <summary>The whole channels admin view: posture, each channel's config, the alert-kind catalog, and routing.</summary>
public sealed record ChannelsView(
    string Mode,
    bool ReachbackAllowed,
    IReadOnlyList<ChannelConfigView> Channels,
    IReadOnlyList<AlertKindView> AlertKinds,
    IReadOnlyList<RoutingRow> Routing);

/// <summary>One channel card: its nature, whether the current mode permits it, its fields, and last test.</summary>
public sealed record ChannelConfigView(
    string Type,
    string Label,
    bool Reachback,
    string? OutsideHost,
    bool AllowedUnderMode,
    bool Enabled,
    bool Configured,
    IReadOnlyList<ChannelFieldView> Fields,
    ChannelTestView? LastTest);

/// <summary>A single config input. Secret fields never carry <see cref="Value"/>; <see cref="Set"/> says whether
/// one is stored so the UI can show "configured" and accept a replacement.</summary>
public sealed record ChannelFieldView(string Key, string Label, string? Value, bool Secret, bool Set, string? Placeholder);

/// <summary>The outcome of the last send-test for a channel.</summary>
public sealed record ChannelTestView(DateTime AtUtc, bool Ok, long Ms, string? Error);

/// <summary>An alert kind for the routing matrix rows. Planned kinds are shown but do not fire yet.</summary>
public sealed record AlertKindView(string Key, string Name, bool Planned);

/// <summary>One routing-matrix row: an alert-kind key and the channel type keys it reaches.</summary>
public sealed record RoutingRow(string Kind, IReadOnlyList<string> Channels);

// ---- update payloads (admin writes) ----

/// <summary>A config save. A null section leaves that channel untouched. A null/blank secret keeps the stored one.</summary>
public sealed record ChannelConfigUpdate(SmtpUpdate? Smtp, SlackUpdate? Slack, TelegramUpdate? Telegram);

public sealed record SmtpUpdate(bool Enabled, string? Host, string? From, string? To);

public sealed record SlackUpdate(bool Enabled, string? WebhookUrl);

public sealed record TelegramUpdate(bool Enabled, string? BotToken, string? ChatId);

/// <summary>A routing-matrix save: the full set of rows replaces the stored matrix.</summary>
public sealed record RoutingUpdate(IReadOnlyList<RoutingRow> Rows);

/// <summary>Maps persisted <see cref="ChannelSettings"/> to the admin view and applies admin writes back.</summary>
public static class ChannelMapper
{
    /// <summary>The alert-kind catalog (design 7.5). Only the non-planned kinds actually dispatch today.</summary>
    public static readonly IReadOnlyList<AlertKindView> AlertKinds =
    [
        new("NewIssue", "New issue", false),
        new("RegressedIssue", "Regressed issue", false),
        new("Spike", "Spike (errors/min over baseline)", true),
        new("Threshold", "Threshold", true),
        new("Heartbeat", "Heartbeat (telemetry stopped)", true),
    ];

    private static string Label(string type) => type switch
    {
        "smtp" => "Email (SMTP)",
        "slack" => "Slack",
        "telegram" => "Telegram",
        _ => type,
    };

    public static ChannelsView Build(ChannelSettings s, EnforcementMode mode)
    {
        var reachbackAllowed = mode == EnforcementMode.Advisory;

        var channels = ChannelFactory.Types.Select(type =>
        {
            var reachback = ChannelFactory.IsReachback(type);
            s.LastTest.TryGetValue(type, out var test);
            return new ChannelConfigView(
                type,
                Label(type),
                reachback,
                ChannelFactory.OutsideHost(type),
                AllowedUnderMode: !reachback || reachbackAllowed,
                Enabled: Enabled(type, s),
                Configured: ChannelFactory.Configured(type, s),
                Fields: Fields(type, s),
                LastTest: test is null ? null : new ChannelTestView(test.AtUtc, test.Ok, test.Ms, test.Error));
        }).ToList();

        var routing = AlertKinds.Select(k =>
            new RoutingRow(k.Key, s.Routing.TryGetValue(k.Key, out var c) ? c : [])).ToList();

        return new ChannelsView(mode.ToString(), reachbackAllowed, channels, AlertKinds, routing);
    }

    private static bool Enabled(string type, ChannelSettings s) => type switch
    {
        "smtp" => s.Smtp.Enabled,
        "slack" => s.Slack.Enabled,
        "telegram" => s.Telegram.Enabled,
        _ => false,
    };

    private static IReadOnlyList<ChannelFieldView> Fields(string type, ChannelSettings s) => type switch
    {
        "smtp" =>
        [
            new("host", "Relay host", s.Smtp.Host, false, !string.IsNullOrWhiteSpace(s.Smtp.Host), "smtp.enclave.internal:25"),
            new("from", "From", s.Smtp.From, false, !string.IsNullOrWhiteSpace(s.Smtp.From), "observer@enclave.internal"),
            new("to", "To", s.Smtp.To, false, !string.IsNullOrWhiteSpace(s.Smtp.To), "oncall@enclave.internal"),
        ],
        "slack" =>
        [
            new("webhookUrl", "Webhook URL", null, true, s.Slack.Configured, "https://hooks.slack.com/services/…"),
        ],
        "telegram" =>
        [
            new("botToken", "Bot token", null, true, !string.IsNullOrWhiteSpace(s.Telegram.BotToken), "123456:ABC…"),
            new("chatId", "Chat id", s.Telegram.ChatId, false, !string.IsNullOrWhiteSpace(s.Telegram.ChatId), "-100…"),
        ],
        _ => [],
    };

    /// <summary>Apply a config write in place. Secrets are preserved when the incoming value is blank.</summary>
    public static void Apply(ChannelSettings s, ChannelConfigUpdate u)
    {
        if (u.Smtp is { } smtp)
        {
            s.Smtp.Enabled = smtp.Enabled;
            s.Smtp.Host = Trim(smtp.Host);
            s.Smtp.From = Trim(smtp.From);
            s.Smtp.To = Trim(smtp.To);
        }
        if (u.Slack is { } slack)
        {
            s.Slack.Enabled = slack.Enabled;
            if (!string.IsNullOrWhiteSpace(slack.WebhookUrl))
                s.Slack.WebhookUrl = slack.WebhookUrl.Trim();
        }
        if (u.Telegram is { } tg)
        {
            s.Telegram.Enabled = tg.Enabled;
            if (!string.IsNullOrWhiteSpace(tg.BotToken))
                s.Telegram.BotToken = tg.BotToken.Trim();
            s.Telegram.ChatId = Trim(tg.ChatId);
        }
    }

    /// <summary>Replace the stored routing matrix with the given rows (channel keys are normalized/validated).</summary>
    public static void ApplyRouting(ChannelSettings s, RoutingUpdate u)
    {
        var valid = AlertKinds.Select(k => k.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var types = ChannelFactory.Types.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, List<string>>();
        foreach (var row in u.Rows)
        {
            if (!valid.Contains(row.Kind))
                continue;
            map[row.Kind] = row.Channels.Where(types.Contains).Distinct().ToList();
        }
        s.Routing = map;
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
