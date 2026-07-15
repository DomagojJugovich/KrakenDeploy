using System.Collections.Concurrent;
using FluentAssertions;
using KrakenDeploy.Agent.Transport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// B2 — the report outbox must deliver strictly FIFO, retry across
/// disconnects (at-least-once), never lose completions to the log cap, drop
/// hub-rejected poison items instead of wedging the queue, and stop cleanly.
/// </summary>
public sealed class ServerLinkOutboxTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private sealed class Harness
    {
        public readonly ConcurrentQueue<OutboxItem> Sent = new();
        public volatile bool Connected = true;
        public Func<OutboxItem, Exception?>? FailWith;
        public ServerLinkOutbox Outbox { get; }

        public Harness()
        {
            Outbox = new ServerLinkOutbox(
                (item, _) =>
                {
                    var ex = FailWith?.Invoke(item);
                    if (ex is not null)
                    {
                        return Task.FromException(ex);
                    }
                    Sent.Enqueue(item);
                    return Task.CompletedTask;
                },
                () => Connected,
                NullLogger.Instance);
        }

        public async Task WaitForSentAsync(int atLeast)
        {
            var deadline = DateTime.UtcNow + TestTimeout;
            while (Sent.Count < atLeast)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Expected ≥{atLeast} sends; saw {Sent.Count}.");
                }
                await Task.Delay(10);
            }
        }
    }

    private static OutboxItem.Log LogLine(int n) => new(Guid.NewGuid(), -1, "info", $"line {n}");

    [Fact]
    public async Task Items_are_delivered_in_fifo_order_across_kinds()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        var pump = h.Outbox.PumpAsync(cts.Token);

        var deploymentId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();
        h.Outbox.Enqueue(new OutboxItem.Log(deploymentId, 0, "info", "step output"));
        h.Outbox.Enqueue(new OutboxItem.StepCompleted(
            deploymentId, dispatchId, 0, "Deploy", true, null, [], []));
        h.Outbox.Enqueue(new OutboxItem.DeploymentCompleted(deploymentId, dispatchId, true, null));

        await h.WaitForSentAsync(3);

        h.Sent.Select(i => i.GetType().Name).Should().Equal(
            [nameof(OutboxItem.Log), nameof(OutboxItem.StepCompleted), nameof(OutboxItem.DeploymentCompleted)],
            "a wave's step reports must be acknowledged before its completion goes out");

        cts.Cancel();
        await pump;
    }

    [Fact]
    public async Task Items_buffered_while_disconnected_flush_on_reconnect()
    {
        var h = new Harness { Connected = false };
        using var cts = new CancellationTokenSource();
        var pump = h.Outbox.PumpAsync(cts.Token);

        var deploymentId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();
        h.Outbox.Enqueue(new OutboxItem.StepCompleted(
            deploymentId, dispatchId, 0, "Deploy", true, null, [], []));
        h.Outbox.Enqueue(new OutboxItem.DeploymentCompleted(deploymentId, dispatchId, true, null));

        // Disconnected: nothing may be sent.
        await Task.Delay(300);
        h.Sent.Should().BeEmpty();

        h.Connected = true;

        await h.WaitForSentAsync(2);
        h.Sent.Last().Should().BeOfType<OutboxItem.DeploymentCompleted>();

        cts.Cancel();
        await pump;
    }

    [Fact]
    public async Task A_send_that_faults_is_retried_until_acknowledged()
    {
        // At-least-once: the fault may have been an ack lost AFTER the server
        // processed the call — the retry is exactly the duplicate the
        // DispatchId key exists to absorb.
        var h = new Harness();
        var failures = 0;
        h.FailWith = _ => Interlocked.Increment(ref failures) <= 2
            ? new IOException("connection dropped mid-invoke")
            : null;

        using var cts = new CancellationTokenSource();
        var pump = h.Outbox.PumpAsync(cts.Token);

        h.Outbox.Enqueue(new OutboxItem.DeploymentCompleted(Guid.NewGuid(), Guid.NewGuid(), true, null));

        await h.WaitForSentAsync(1);
        failures.Should().Be(3, "two faulted attempts + the successful one");

        cts.Cancel();
        await pump;
    }

    [Fact]
    public async Task Hub_rejection_gets_capped_retries_then_drops_and_the_queue_keeps_moving()
    {
        // A HubException may be a TRANSIENT server fault (DB blip inside the hub
        // method) — it gets the same capped retries as any failure; only a
        // persistent rejection is dropped, so the queue can never wedge.
        var h = new Harness();
        var attempts = 0;
        var poison = new OutboxItem.Log(Guid.NewGuid(), -1, "info", "poison");
        h.FailWith = item =>
        {
            if (ReferenceEquals(item, poison))
            {
                Interlocked.Increment(ref attempts);
                return new HubException("method rejected the payload");
            }
            return null;
        };

        using var cts = new CancellationTokenSource();
        var pump = h.Outbox.PumpAsync(cts.Token);

        h.Outbox.Enqueue(poison);
        h.Outbox.Enqueue(new OutboxItem.DeploymentCompleted(Guid.NewGuid(), Guid.NewGuid(), true, null));

        await h.WaitForSentAsync(1);
        attempts.Should().Be(ServerLinkOutbox.MaxSendAttemptsPerItem);
        h.Sent.Single().Should().BeOfType<OutboxItem.DeploymentCompleted>(
            "the persistently rejected item is dropped, not retried forever in front of the queue");

        cts.Cancel();
        await pump;
    }

    [Fact]
    public async Task Persistent_connected_failures_drop_the_item_as_poison()
    {
        var h = new Harness();
        var attempts = 0;
        var victim = new OutboxItem.Log(Guid.NewGuid(), -1, "info", "always fails");
        h.FailWith = item =>
        {
            if (ReferenceEquals(item, victim))
            {
                Interlocked.Increment(ref attempts);
                return new IOException("permanently broken serialization");
            }
            return null;
        };

        using var cts = new CancellationTokenSource();
        var pump = h.Outbox.PumpAsync(cts.Token);

        h.Outbox.Enqueue(victim);
        h.Outbox.Enqueue(new OutboxItem.DeploymentCompleted(Guid.NewGuid(), Guid.NewGuid(), true, null));

        await h.WaitForSentAsync(1);
        attempts.Should().Be(ServerLinkOutbox.MaxSendAttemptsPerItem);
        h.Sent.Single().Should().BeOfType<OutboxItem.DeploymentCompleted>();

        cts.Cancel();
        await pump;
    }

    [Fact]
    public void Log_lines_over_the_cap_are_dropped_and_counted_but_completions_always_queue()
    {
        // No pump running — everything stays queued, like a long outage.
        var h = new Harness { Connected = false };

        for (var i = 0; i < ServerLinkOutbox.LogCapacity; i++)
        {
            h.Outbox.Enqueue(LogLine(i)).Should().BeTrue();
        }

        h.Outbox.Enqueue(LogLine(-1)).Should().BeFalse("the cap is reached");
        h.Outbox.Enqueue(LogLine(-2)).Should().BeFalse();
        h.Outbox.DroppedLogCount.Should().Be(2);

        // Completions are never subject to the log cap.
        h.Outbox.Enqueue(new OutboxItem.DeploymentCompleted(Guid.NewGuid(), Guid.NewGuid(), true, null))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Draining_log_items_frees_cap_space()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        var pump = h.Outbox.PumpAsync(cts.Token);

        for (var i = 0; i < ServerLinkOutbox.LogCapacity; i++)
        {
            h.Outbox.Enqueue(LogLine(i));
        }
        await h.WaitForSentAsync(ServerLinkOutbox.LogCapacity);

        h.Outbox.Enqueue(LogLine(-1)).Should().BeTrue("delivered items no longer count against the cap");

        cts.Cancel();
        await pump;
    }

    [Fact]
    public async Task Pump_exits_promptly_on_cancellation_while_disconnected()
    {
        var h = new Harness { Connected = false };
        using var cts = new CancellationTokenSource();
        var pump = h.Outbox.PumpAsync(cts.Token);

        h.Outbox.Enqueue(LogLine(0));
        await Task.Delay(100);

        cts.Cancel();
        var finished = await Task.WhenAny(pump, Task.Delay(TestTimeout));
        finished.Should().BeSameAs(pump, "the pump must honour shutdown while waiting for a reconnect");
        h.Sent.Should().BeEmpty();
    }
}
