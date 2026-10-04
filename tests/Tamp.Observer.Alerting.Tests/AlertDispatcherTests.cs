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

    [Fact]
    public async Task Routing_matrix_delivers_a_kind_only_to_its_mapped_channels()
    {
        var smtp = new CollectingChannel("smtp", reachback: false);
        var slack = new CollectingChannel("slack", reachback: false);
        // New issue -> smtp only; regressed issue -> both.
        var routing = new Dictionary<AlertKind, IReadOnlySet<string>>
        {
            [AlertKind.NewIssue] = new HashSet<string> { "smtp" },
            [AlertKind.RegressedIssue] = new HashSet<string> { "smtp", "slack" },
        };
        var dispatcher = new AlertDispatcher([smtp, slack], routing: routing);

        await dispatcher.DispatchAsync(
            [Sample(AlertKind.NewIssue), Sample(AlertKind.RegressedIssue)],
            new EnforcementGate(EnforcementMode.Advisory));

        Assert.Equal(2, smtp.Received.Count);                                 // both kinds
        Assert.Single(slack.Received);                                        // regressed only
        Assert.Equal(AlertKind.RegressedIssue, slack.Received[0].Kind);
    }

    [Fact]
    public async Task Routing_matrix_with_no_entry_for_a_kind_drops_it()
    {
        var smtp = new CollectingChannel("smtp", reachback: false);
        var routing = new Dictionary<AlertKind, IReadOnlySet<string>>
        {
            [AlertKind.NewIssue] = new HashSet<string> { "smtp" },
        };
        var dispatcher = new AlertDispatcher([smtp], routing: routing);

        await dispatcher.DispatchAsync([Sample(AlertKind.RegressedIssue)], new EnforcementGate(EnforcementMode.Advisory));

        Assert.Empty(smtp.Received); // no row for regressed-issue means no delivery
    }
}
