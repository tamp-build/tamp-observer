using Tamp.Observer.Alerting;
using Tamp.Observer.Domain;
using Xunit;

namespace Tamp.Observer.Alerting.Tests;

/// <summary>Unit tests for the alerting fan-out and enforcement gating (ADR 0016). Fast lane.</summary>
public sealed class AlertDispatcherTests
{
    private static AlertEvent Sample(AlertKind kind = AlertKind.NewIssue) =>
        new(kind, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fp", "NullRef", "NullReferenceException", 3, 2);

    private sealed class CollectingChannel(string name, bool reachback) : INotificationChannel
    {
        public string Name => name;
        public bool IsReachback => reachback;
        public List<AlertEvent> Received { get; } = [];
        public Task SendAsync(AlertEvent alert, CancellationToken ct = default)
        {
            Received.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingChannel : INotificationChannel
    {
        public string Name => "boom";
        public bool IsReachback => false;
        public Task SendAsync(AlertEvent alert, CancellationToken ct = default) => throw new InvalidOperationException("nope");
    }

    [Fact]
    public async Task Fans_out_to_every_enabled_channel()
    {
        var a = new CollectingChannel("a", reachback: false);
        var b = new CollectingChannel("b", reachback: false);
        var dispatcher = new AlertDispatcher([a, b]);

        await dispatcher.DispatchAsync([Sample()], new EnforcementGate(EnforcementMode.Advisory));

        Assert.Single(a.Received);
        Assert.Single(b.Received);
    }

    [Fact]
    public async Task Reachback_channel_is_skipped_under_enforcing()
    {
        var cloud = new CollectingChannel("slack", reachback: true);
        var local = new CollectingChannel("smtp", reachback: false);
        var dispatcher = new AlertDispatcher([cloud, local]);

        await dispatcher.DispatchAsync([Sample()], new EnforcementGate(EnforcementMode.Enforcing));

        Assert.Empty(cloud.Received);   // reachback refused under enforcing
        Assert.Single(local.Received);  // internal channel still delivers
    }

    [Fact]
    public async Task Reachback_channel_delivers_under_advisory()
    {
        var cloud = new CollectingChannel("slack", reachback: true);
        var dispatcher = new AlertDispatcher([cloud]);

        await dispatcher.DispatchAsync([Sample()], new EnforcementGate(EnforcementMode.Advisory));

        Assert.Single(cloud.Received);
    }

    [Fact]
    public async Task A_failing_channel_does_not_block_the_others()
    {
        var good = new CollectingChannel("good", reachback: false);
        var errors = new List<string>();
        var dispatcher = new AlertDispatcher([new ThrowingChannel(), good], (name, _) => errors.Add(name));

        await dispatcher.DispatchAsync([Sample()], new EnforcementGate(EnforcementMode.Advisory));

        Assert.Single(good.Received);
        Assert.Contains("boom", errors);
    }
}
