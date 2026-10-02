using System.Net;
using Tamp.Observer.Alerting;
using Xunit;

namespace Tamp.Observer.Alerting.Tests;

/// <summary>Unit tests for the HTTP channels' request shape, via a capturing handler (no network). Fast lane.</summary>
public sealed class HttpChannelTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? Url { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static AlertEvent Sample() =>
        new(AlertKind.RegressedIssue, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fp", "NullRef", "NullReferenceException", 7, 4);

    [Fact]
    public async Task Slack_posts_formatted_text_to_the_webhook()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var channel = new SlackChannel(http, "https://hooks.slack.test/abc");

        await channel.SendAsync(Sample());

        Assert.Equal("https://hooks.slack.test/abc", handler.Url!.ToString());
        Assert.Contains("REGRESSED", handler.Body);
        Assert.Contains("NullReferenceException", handler.Body);
        Assert.True(channel.IsReachback);
    }

    [Fact]
    public async Task Telegram_posts_chat_id_and_text_to_the_bot_api()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var channel = new TelegramChannel(http, "TOKEN123", "chat-9");

        await channel.SendAsync(Sample());

        Assert.Equal("https://api.telegram.org/botTOKEN123/sendMessage", handler.Url!.ToString());
        Assert.Contains("chat-9", handler.Body);
        Assert.Contains("REGRESSED", handler.Body);
        Assert.True(channel.IsReachback);
    }
}
