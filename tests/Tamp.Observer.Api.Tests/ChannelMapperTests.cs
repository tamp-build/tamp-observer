using Tamp.Observer.Api;
using Tamp.Observer.Domain;
using Xunit;

namespace Tamp.Observer.Api.Tests;

/// <summary>
/// ChannelMapper is the admin API's view of notification settings (ADR 0016). It was at 0% coverage
/// (tamp-observer#19) despite holding three things that fail quietly if they break:
///
///   1. Secret masking. ChannelSettings stores the Slack webhook and Telegram bot token, and the admin
///      API must never return them to the browser -- only whether they are SET. A regression here
///      leaks credentials into an HTTP response that looks completely normal.
///   2. Secret preservation. A config write with a blank secret means "leave it alone", not "clear
///      it". Getting that backwards silently de-configures a working channel the next time an admin
///      saves the form without re-typing the token.
///   3. Routing validation. Unknown alert kinds and unknown channel types must be dropped rather than
///      stored, or the evaluator later tries to dispatch to a channel that does not exist.
///
/// All pure functions over a settings object, so none of this needs a host.
/// </summary>
public sealed class ChannelMapperTests
{
    private static ChannelSettings Configured() => new()
    {
        Smtp = new SmtpConfig { Enabled = true, Host = "relay.internal:25", From = "o@x", To = "ops@x" },
        Slack = new SlackConfig { Enabled = true, WebhookUrl = "https://hooks.slack.com/services/T/B/secret" },
        Telegram = new TelegramConfig { Enabled = true, BotToken = "123456:SECRET", ChatId = "-100123" },
    };

    // ---- 1. masking ----

    [Fact]
    public void Secret_field_values_are_never_returned_only_whether_they_are_set()
    {
        var view = ChannelMapper.Build(Configured(), EnforcementMode.Advisory);

        foreach (var field in view.Channels.SelectMany(c => c.Fields).Where(f => f.Secret))
        {
            Assert.Null(field.Value);   // the value itself must not travel
            Assert.True(field.Set);     // but the UI still needs to know one exists
        }
    }

    [Fact]
    public void No_stored_secret_appears_anywhere_in_the_serialized_view()
    {
        // The blunt check that survives refactoring: whatever shape the view takes, these strings
        // must not be in it. A new non-secret field carrying a token would slip past the per-field
        // assertion above but not past this.
        var json = System.Text.Json.JsonSerializer.Serialize(
            ChannelMapper.Build(Configured(), EnforcementMode.Advisory));

        // Matched on the DISTINCTIVE part of each stored value, not the host. The Slack field's
        // placeholder is legitimately "https://hooks.slack.com/services/..." -- asserting on the host
        // flagged that UI hint as a leak, which is a false positive in the test, not a bug.
        Assert.DoesNotContain("/T/B/secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456:SECRET", json, StringComparison.Ordinal);

        // And prove the assertion can actually fail, so it is not passing vacuously on a typo.
        Assert.Contains("/T/B/secret", Configured().Slack.WebhookUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_secret_fields_do_carry_their_values_so_the_form_can_round_trip()
    {
        var view = ChannelMapper.Build(Configured(), EnforcementMode.Advisory);
        var smtp = view.Channels.Single(c => c.Type == "smtp");

        Assert.Equal("relay.internal:25", smtp.Fields.Single(f => f.Key == "host").Value);
        Assert.All(smtp.Fields, f => Assert.False(f.Secret));
    }

    // ---- 2. preservation ----

    [Fact]
    public void A_blank_secret_on_write_preserves_the_stored_one()
    {
        // This is what the admin form posts when the user edits the enabled flag and does not
        // re-type the token. Clearing it here would silently break a working channel.
        var s = Configured();

        ChannelMapper.Apply(s, new ChannelConfigUpdate(
            Smtp: null,
            Slack: new SlackUpdate(Enabled: false, WebhookUrl: null),
            Telegram: new TelegramUpdate(Enabled: false, BotToken: "   ", ChatId: "-100123")));

        Assert.Equal("https://hooks.slack.com/services/T/B/secret", s.Slack.WebhookUrl);
        Assert.Equal("123456:SECRET", s.Telegram.BotToken);
        Assert.False(s.Slack.Enabled);     // the non-secret change still applied
        Assert.False(s.Telegram.Enabled);
    }

    [Fact]
    public void A_non_blank_secret_on_write_replaces_the_stored_one_and_is_trimmed()
    {
        var s = Configured();

        ChannelMapper.Apply(s, new ChannelConfigUpdate(
            Smtp: null,
            Slack: new SlackUpdate(true, "  https://hooks.slack.com/services/NEW  "),
            Telegram: new TelegramUpdate(true, " 999:NEW ", " -200 ")));

        Assert.Equal("https://hooks.slack.com/services/NEW", s.Slack.WebhookUrl);
        Assert.Equal("999:NEW", s.Telegram.BotToken);
        Assert.Equal("-200", s.Telegram.ChatId);
    }

    [Fact]
    public void Blank_non_secret_fields_are_cleared_to_null_rather_than_kept_as_whitespace()
    {
        // Non-secrets are genuinely clearable -- the asymmetry with secrets is the point.
        var s = Configured();

        ChannelMapper.Apply(s, new ChannelConfigUpdate(
            Smtp: new SmtpUpdate(Enabled: true, Host: "   ", From: "", To: null),
            Slack: null, Telegram: null));

        Assert.Null(s.Smtp.Host);
        Assert.Null(s.Smtp.From);
        Assert.Null(s.Smtp.To);
        Assert.False(s.Smtp.Configured);
    }

    [Fact]
    public void An_omitted_channel_block_is_left_entirely_alone()
    {
        var s = Configured();

        ChannelMapper.Apply(s, new ChannelConfigUpdate(Smtp: null, Slack: null, Telegram: null));

        Assert.Equal("relay.internal:25", s.Smtp.Host);
        Assert.True(s.Smtp.Enabled);
        Assert.Equal("https://hooks.slack.com/services/T/B/secret", s.Slack.WebhookUrl);
    }

    // ---- 3. routing validation ----

    [Fact]
    public void Routing_drops_unknown_alert_kinds_and_unknown_channel_types()
    {
        var s = Configured();

        ChannelMapper.ApplyRouting(s, new RoutingUpdate(
        [
            new RoutingRow("NewIssue", ["smtp", "slack"]),
            new RoutingRow("NotAKind", ["smtp"]),                 // unknown kind -> dropped
            new RoutingRow("RegressedIssue", ["smtp", "carrier-pigeon"]), // unknown channel -> dropped
        ]));

        Assert.Equal(["smtp", "slack"], s.Routing["NewIssue"]);
        Assert.False(s.Routing.ContainsKey("NotAKind"));
        Assert.Equal(["smtp"], s.Routing["RegressedIssue"]);
    }

    [Fact]
    public void Routing_de_duplicates_repeated_channels()
    {
        var s = Configured();

        ChannelMapper.ApplyRouting(s, new RoutingUpdate(
            [new RoutingRow("NewIssue", ["smtp", "smtp", "slack"])]));

        Assert.Equal(["smtp", "slack"], s.Routing["NewIssue"]);
    }

    [Fact]
    public void Routing_replaces_the_matrix_rather_than_merging_into_it()
    {
        // A row the admin removed must actually disappear, not linger from the previous save.
        var s = Configured();
        s.Routing = new Dictionary<string, List<string>> { ["NewIssue"] = ["smtp"], ["RegressedIssue"] = ["slack"] };

        ChannelMapper.ApplyRouting(s, new RoutingUpdate([new RoutingRow("NewIssue", ["slack"])]));

        Assert.Single(s.Routing);
        Assert.Equal(["slack"], s.Routing["NewIssue"]);
    }

    // ---- the enforcement-mode gate ----

    [Fact]
    public void Reachback_channels_are_disallowed_outside_advisory_mode()
    {
        // Slack and Telegram post to the public internet; under a hardened posture the UI must show
        // them as not allowed rather than offering a control that would breach the enclave.
        var enforced = ChannelMapper.Build(Configured(), EnforcementMode.Enforcing);

        Assert.False(enforced.ReachbackAllowed);
        foreach (var c in enforced.Channels.Where(c => c.Reachback))
            Assert.False(c.AllowedUnderMode);
        // SMTP is the non-reachback case and stays available.
        Assert.True(enforced.Channels.Single(c => c.Type == "smtp").AllowedUnderMode);
    }

    [Fact]
    public void Every_alert_kind_gets_a_routing_row_even_when_nothing_is_routed()
    {
        // The UI renders one row per kind; a missing key would drop a row silently.
        var view = ChannelMapper.Build(new ChannelSettings(), EnforcementMode.Advisory);

        Assert.Equal(ChannelMapper.AlertKinds.Count, view.Routing.Count);
        Assert.All(view.Routing, r => Assert.Empty(r.Channels));
    }
}
