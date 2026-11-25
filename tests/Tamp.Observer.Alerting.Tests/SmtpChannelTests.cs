using Tamp.Observer.Alerting;
using Xunit;

namespace Tamp.Observer.Alerting.Tests;

/// <summary>
/// tamp-observer#15: SMTP TLS used to be opt-in, so a host + credentials configuration sent AUTH in
/// plaintext with nothing signalling it (csharpsquid:S5332). These pin the two things that fix:
/// the default is now secure, and credentials are never put on an unencrypted connection by accident.
///
/// Both assertions run without an SMTP server on purpose — the refusal happens before any network
/// call, and the default is a property. Nothing here needs a relay to be meaningful.
/// </summary>
public sealed class SmtpChannelTests
{
    private static SmtpChannelOptions Options(bool? useSsl = null, string? username = null,
                                              bool allowPlaintext = false) =>
        new()
        {
            Host = "relay.invalid",
            From = "alerts@tamp.local",
            To = ["ops@tamp.local"],
            Username = username,
            Password = username is null ? null : "pw",
            AllowPlaintextCredentials = allowPlaintext,
            UseSsl = useSsl ?? true,
        };

    [Fact]
    public void Tls_is_on_by_default()
    {
        // The whole point of #15. If this ever flips back, credentials go out in the clear again
        // and nothing else in the system would notice.
        var options = new SmtpChannelOptions
        {
            Host = "relay.invalid",
            From = "alerts@tamp.local",
            To = ["ops@tamp.local"],
        };

        Assert.True(options.UseSsl);
        Assert.False(options.AllowPlaintextCredentials);
    }

    [Fact]
    public async Task Credentials_over_plaintext_are_refused_rather_than_sent()
    {
        var channel = new SmtpChannel(Options(useSsl: false, username: "alerts"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => channel.SendAsync(Alert()));

        // The message has to name the way out, or the refusal just looks like a bug.
        Assert.Contains("UseSsl", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AllowPlaintextCredentials", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plaintext_without_credentials_is_not_refused()
    {
        // An unauthenticated relay over plaintext is a real deployment (loopback, in-enclave MTA).
        // There are no credentials to leak, so the guard must not fire -- it gets past the check and
        // fails later trying to reach relay.invalid, which is what proves it was not refused.
        var channel = new SmtpChannel(Options(useSsl: false));

        var ex = await Record.ExceptionAsync(() => channel.SendAsync(Alert()));

        Assert.NotNull(ex);
        Assert.IsNotType<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task Plaintext_credentials_are_allowed_when_asked_for_explicitly()
    {
        var channel = new SmtpChannel(Options(useSsl: false, username: "alerts", allowPlaintext: true));

        var ex = await Record.ExceptionAsync(() => channel.SendAsync(Alert()));

        Assert.NotNull(ex);
        Assert.IsNotType<InvalidOperationException>(ex);
    }

    // Same shape as AlertDispatcherTests.Sample, so these read alongside each other.
    private static AlertEvent Alert() =>
        new(AlertKind.NewIssue, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fp", "NullRef",
            "NullReferenceException", 3, 2);
}
