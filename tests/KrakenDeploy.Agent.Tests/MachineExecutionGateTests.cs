using FluentAssertions;
using KrakenDeploy.Agent.Deployment;
using Mode = KrakenDeploy.Agent.Deployment.MachineExecutionGate.Mode;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// F5 — the machine execution gate's reader-writer semantics, unit level.
/// <para>
/// Pre-F5 the gate was a <c>SemaphoreSlim(1, 1)</c> and the per-target
/// <c>AllowParallelTaskExecution</c> flag meant "skip it entirely" — so opting one
/// target in removed same-machine protection outright. It is now a fair async
/// reader-writer lock (Octopus <c>ScriptIsolationMutex</c> parity: their
/// <c>NoIsolation</c> takes the READ side of the very same lock), and the flag only
/// chooses a SIDE. The properties that matter, and that the exclusion tests below
/// pin in BOTH directions, are:
/// </para>
/// <list type="bullet">
///   <item>Shared ∥ Shared co-run.</item>
///   <item>Exclusive excludes Shared, and Shared excludes Exclusive.</item>
///   <item>A writer is never starved by a stream of readers.</item>
///   <item>A bounded wait that expires — or a cancel while queued — leaves NOTHING
///         held, and hands the gate on correctly.</item>
/// </list>
/// </summary>
public sealed class MachineExecutionGateTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(200);

    // ── Co-running ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_shared_holders_co_run()
    {
        using var gate = new MachineExecutionGate();

        using var first = await Take(gate, Mode.Shared);
        using var second = await gate.TryAcquireNowAsync(Mode.Shared, default);

        second.Should().NotBeNull("shared work co-runs with shared work");
        gate.ReaderCount.Should().Be(2);
        gate.IsWriteHeld.Should().BeFalse();
        gate.IsHeld.Should().BeTrue();
    }

    [Fact]
    public async Task Reader_count_drops_back_to_zero_as_shared_holders_release()
    {
        using var gate = new MachineExecutionGate();

        var first = await Take(gate, Mode.Shared);
        var second = await Take(gate, Mode.Shared);
        gate.ReaderCount.Should().Be(2);

        first.Dispose();
        gate.ReaderCount.Should().Be(1);
        gate.IsHeld.Should().BeTrue("one reader is still executing");

        second.Dispose();
        gate.ReaderCount.Should().Be(0);
        gate.IsHeld.Should().BeFalse();
    }

    [Fact]
    public async Task Releasing_a_lease_twice_is_a_no_op()
    {
        // The idempotency B7 relied on: every call site disposes inside a `using`,
        // and some paths could otherwise release a lease the gate already handed on.
        using var gate = new MachineExecutionGate();

        var lease = await Take(gate, Mode.Exclusive);
        lease.Dispose();
        lease.Dispose();

        gate.IsHeld.Should().BeFalse();
        // A phantom release would have left the gate grantable to TWO writers.
        using var next = await Take(gate, Mode.Exclusive);
        gate.IsWriteHeld.Should().BeTrue();
    }

    // ── Exclusion, both directions ──────────────────────────────────────────

    [Fact]
    public async Task An_exclusive_holder_excludes_a_shared_waiter()
    {
        using var gate = new MachineExecutionGate();
        using var writer = await Take(gate, Mode.Exclusive);

        (await gate.TryAcquireNowAsync(Mode.Shared, default)).Should().BeNull();
        (await gate.AcquireAsync(Mode.Shared, ShortWait, default)).Should().BeNull(
            "a shared reader must NOT slip in beside an exclusive holder — this is the " +
            "direction F2's bypass got wrong");

        gate.ReaderCount.Should().Be(0);
        gate.IsWriteHeld.Should().BeTrue();
    }

    [Fact]
    public async Task A_shared_holder_excludes_an_exclusive_waiter()
    {
        using var gate = new MachineExecutionGate();
        using var reader = await Take(gate, Mode.Shared);

        (await gate.TryAcquireNowAsync(Mode.Exclusive, default)).Should().BeNull();
        (await gate.AcquireAsync(Mode.Exclusive, ShortWait, default)).Should().BeNull(
            "consent is mutual — one side opting into sharing cannot force the other to");

        gate.IsWriteHeld.Should().BeFalse();
        gate.ReaderCount.Should().Be(1);
    }

    [Fact]
    public async Task An_exclusive_holder_excludes_another_exclusive_waiter()
    {
        using var gate = new MachineExecutionGate();
        using var writer = await Take(gate, Mode.Exclusive);

        (await gate.AcquireAsync(Mode.Exclusive, ShortWait, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_shared_waiter_is_granted_as_soon_as_the_writer_releases()
    {
        using var gate = new MachineExecutionGate();
        var writer = await Take(gate, Mode.Exclusive);

        var queued = gate.AcquireAsync(Mode.Shared, default);
        await WaitUntilAsync(() => gate.QueuedCount == 1, "the reader must be queued");
        queued.IsCompleted.Should().BeFalse();

        writer.Dispose();

        using var lease = await queued.WaitAsync(TestTimeout);
        lease.Mode.Should().Be(Mode.Shared);
        gate.ReaderCount.Should().Be(1);
    }

    // ── Fairness: no writer starvation ──────────────────────────────────────

    [Fact]
    public async Task A_queued_writer_is_not_starved_by_a_stream_of_readers()
    {
        // The property that ruled out the obvious SemaphoreSlim recipes. A reader is
        // executing and a writer queues behind it; readers keep arriving. If a later
        // reader could barge — legal on the raw state, since a reader IS compatible
        // with the current reader — ReaderCount would never reach 0 and the writer
        // would wait forever.
        using var gate = new MachineExecutionGate();
        var reader = await Take(gate, Mode.Shared);

        var writer = gate.AcquireAsync(Mode.Exclusive, default);
        await WaitUntilAsync(() => gate.QueuedCount == 1, "the writer must be queued");

        // Ten more readers arrive AFTER the writer. Every one must queue behind it.
        var late = Enumerable.Range(0, 10)
            .Select(_ => gate.AcquireAsync(Mode.Shared, default))
            .ToArray();
        await WaitUntilAsync(() => gate.QueuedCount == 11, "the late readers must queue too");

        gate.ReaderCount.Should().Be(1, "no late reader may barge past the queued writer");
        writer.IsCompleted.Should().BeFalse();

        // The only in-flight reader leaves → the writer is next, alone.
        reader.Dispose();
        using var writeLease = await writer.WaitAsync(TestTimeout);
        gate.IsWriteHeld.Should().BeTrue();
        gate.ReaderCount.Should().Be(0,
            "the writer must hold the gate alone, with all ten readers still queued");

        writeLease.Dispose();

        // Now the whole reader convoy is admitted at once.
        var leases = await Task.WhenAll(late).WaitAsync(TestTimeout);
        gate.ReaderCount.Should().Be(10);
        foreach (var lease in leases) { lease.Dispose(); }
        gate.IsHeld.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquireNow_does_not_barge_past_a_queued_writer()
    {
        // TryAcquireNowAsync is the fast path both executors probe with before they
        // announce a wait. It must respect the queue, or it becomes the barge.
        using var gate = new MachineExecutionGate();
        using var reader = await Take(gate, Mode.Shared);

        var writer = gate.AcquireAsync(Mode.Exclusive, default);
        await WaitUntilAsync(() => gate.QueuedCount == 1, "the writer must be queued");

        (await gate.TryAcquireNowAsync(Mode.Shared, default)).Should().BeNull(
            "compatible with the current state, but the queued writer arrived first");

        // Unblock so the test leaks nothing.
        reader.Dispose();
        (await writer.WaitAsync(TestTimeout)).Dispose();
    }

    // ── Giving up: nothing stranded, nothing leaked ─────────────────────────

    [Fact]
    public async Task An_expired_bounded_wait_leaves_the_queue_empty()
    {
        // The classic hand-rolled-lock bug: a timed-out waiter left in the queue is
        // later "granted" the gate that nobody then releases.
        using var gate = new MachineExecutionGate();
        using var writer = await Take(gate, Mode.Exclusive);

        (await gate.AcquireAsync(Mode.Shared, ShortWait, default)).Should().BeNull();
        gate.QueuedCount.Should().Be(0, "the expired waiter must have left the queue");

        writer.Dispose();
        gate.IsHeld.Should().BeFalse(
            "the release must not have been handed to the departed waiter");

        using var next = await Take(gate, Mode.Exclusive);
        gate.IsWriteHeld.Should().BeTrue();
    }

    [Fact]
    public async Task A_cancel_while_queued_throws_and_holds_nothing()
    {
        using var gate = new MachineExecutionGate();
        using var writer = await Take(gate, Mode.Exclusive);
        using var cts = new CancellationTokenSource();

        var queued = gate.AcquireAsync(Mode.Shared, cts.Token);
        await WaitUntilAsync(() => gate.QueuedCount == 1, "the reader must be queued");

        await cts.CancelAsync();

        await FluentActions.Awaiting(() => queued)
            .Should().ThrowAsync<OperationCanceledException>();
        gate.QueuedCount.Should().Be(0);
        gate.ReaderCount.Should().Be(0);
    }

    [Fact]
    public async Task A_departing_writer_unblocks_the_readers_queued_behind_it()
    {
        // Liveness: readers queued behind a writer are blocked by the FAIRNESS rule,
        // not by the gate's state. When the writer gives up they must be granted at
        // once, not left waiting for the current holder to release.
        using var gate = new MachineExecutionGate();
        using var holder = await Take(gate, Mode.Shared);

        var writer = gate.AcquireAsync(Mode.Exclusive, ShortWait, default);
        await WaitUntilAsync(() => gate.QueuedCount == 1, "the writer must be queued");

        var behind = gate.AcquireAsync(Mode.Shared, default);
        await WaitUntilAsync(() => gate.QueuedCount == 2, "the reader must queue behind it");
        behind.IsCompleted.Should().BeFalse();

        (await writer.WaitAsync(TestTimeout)).Should().BeNull("the bounded wait expired");

        using var lease = await behind.WaitAsync(TestTimeout);
        gate.ReaderCount.Should().Be(2, "the shared holder plus the freshly-granted reader");
    }

    [Fact]
    public async Task Disposing_the_gate_unblocks_queued_waiters()
    {
        // SemaphoreSlim.Dispose does NOT signal pending waiters, so pre-F5 a plan
        // parked on the gate at shutdown never resumed and its finally never ran —
        // which is why every call site links the host-stopping token into its wait.
        // They still do; this is the backstop for the race.
        var gate = new MachineExecutionGate();
        using var writer = await Take(gate, Mode.Exclusive);

        var queued = gate.AcquireAsync(Mode.Shared, default);
        await WaitUntilAsync(() => gate.QueuedCount == 1, "the reader must be queued");

        gate.Dispose();

        await FluentActions.Awaiting(() => queued)
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Releasing_a_lease_after_disposal_does_not_throw()
    {
        // Host shutdown can dispose the gate while an in-flight run is still
        // unwinding toward its release — that must not fault the run's finally.
        var gate = new MachineExecutionGate();
        var lease = await Take(gate, Mode.Exclusive);

        gate.Dispose();

        FluentActions.Invoking(lease.Dispose).Should().NotThrow();
    }

    [Fact]
    public async Task Acquiring_after_disposal_throws()
    {
        var gate = new MachineExecutionGate();
        gate.Dispose();

        await FluentActions.Awaiting(() => gate.TryAcquireNowAsync(Mode.Shared, default))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<MachineExecutionGate.Releaser> Take(
        MachineExecutionGate gate, Mode mode)
        => await gate.TryAcquireNowAsync(mode, default)
           ?? throw new InvalidOperationException(
               $"Test setup: expected the gate to be free for {mode}.");

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }
            await Task.Delay(10);
        }
    }
}
