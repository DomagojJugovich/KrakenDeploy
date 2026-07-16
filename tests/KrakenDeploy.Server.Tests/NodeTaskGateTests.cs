using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// B7 — the node task cap. The gate is the unit under test; the worker wires
/// it around every fire-and-forget dispatch (slot before gauge, so queued
/// deployments never block blue-green drain).
/// </summary>
public sealed class NodeTaskGateTests
{
    [Fact]
    public async Task Never_admits_more_than_the_capacity()
    {
        using var gate = new NodeTaskGate(3);
        var running = 0;
        var maxObserved = 0;
        var gates = new object();

        var workers = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            using var slot = await gate.AcquireAsync(CancellationToken.None);
            lock (gates)
            {
                running++;
                maxObserved = Math.Max(maxObserved, running);
            }
            await Task.Delay(25);
            lock (gates)
            {
                running--;
            }
        })).ToArray();

        await Task.WhenAll(workers);

        maxObserved.Should().BeLessThanOrEqualTo(3,
            "the cap bounds concurrent orchestrations");
        maxObserved.Should().BeGreaterThan(1, "the cap must still allow parallelism");
        gate.InUse.Should().Be(0, "all slots return after the work");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void Non_positive_capacity_falls_back_to_the_default(int configured)
    {
        using var gate = new NodeTaskGate(configured);
        gate.Capacity.Should().Be(NodeTaskGate.DefaultMaxConcurrentTasks);
    }

    [Fact]
    public async Task Releaser_is_idempotent()
    {
        using var gate = new NodeTaskGate(2);
        var slot = await gate.AcquireAsync(CancellationToken.None);
        slot.Dispose();
        slot.Dispose(); // double dispose must not over-release

        gate.InUse.Should().Be(0);
        // If the double dispose over-released, a third acquire would push
        // CurrentCount past capacity — SemaphoreSlim would throw on Release
        // later or InUse would go negative. Take all slots to prove the pool
        // is exactly its capacity.
        using var a = await gate.AcquireAsync(CancellationToken.None);
        using var b = await gate.AcquireAsync(CancellationToken.None);
        gate.InUse.Should().Be(2);
    }

    [Fact]
    public async Task Waiting_acquire_honors_cancellation()
    {
        using var gate = new NodeTaskGate(1);
        using var held = await gate.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(50);
        var act = () => gate.AcquireAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "a queued dispatch must abandon its wait on shutdown");
    }
}
