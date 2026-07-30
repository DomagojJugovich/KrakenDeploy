using System.Collections.Concurrent;
using FluentAssertions;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// B6 — the executor's single-flight registry: TryCancel aborts the in-flight
/// attempt and reports a failed completion; a re-delivered copy of the SAME
/// attempt is ignored; a NEWER attempt supersedes (cancels + awaits) the old
/// one. The gate link holds the FIRST success-path completion in flight
/// (honoring the run's token), so tests can act while a task is registered.
/// <para>
/// Also covers B7's machine execution queue and F2's per-target opt-out
/// (<c>DeploymentPlan.AllowParallelTaskExecution</c>) plus the execution-started
/// report the server arms its wave deadline from.
/// </para>
/// </summary>
public sealed class DeploymentExecutorCancelTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TryCancel_aborts_the_run_and_reports_failed_completion()
    {
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var taskId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();

        var run = Task.Run(() => executor.ExecuteAsync(Plan(taskId, dispatchId)));
        await WaitUntilAsync(() => executor.IsExecuting, "the run must register in flight");

        executor.TryCancel(taskId, "operator says stop").Should().BeTrue();
        await run.WaitAsync(TestTimeout);

        var completion = link.Completions.Should().ContainSingle().Subject;
        completion.Dispatch.Should().Be(dispatchId);
        completion.Success.Should().BeFalse("a cancelled attempt must not report success");
        completion.Error.Should().Contain("operator says stop");
        executor.IsExecuting.Should().BeFalse();
        executor.TryCancel(taskId, "again").Should().BeFalse("the task is no longer in flight");
    }

    [Fact]
    public async Task Duplicate_delivery_of_the_same_attempt_is_ignored()
    {
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var taskId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();

        var original = Task.Run(() => executor.ExecuteAsync(Plan(taskId, dispatchId)));
        await WaitUntilAsync(() => executor.IsExecuting, "the original must be in flight");

        // At-least-once transport re-delivers the SAME attempt — must be a no-op.
        await executor.ExecuteAsync(Plan(taskId, dispatchId)).WaitAsync(TestTimeout);
        link.Completions.Should().BeEmpty("the duplicate must not report anything");

        link.ReleaseFirstCompletion.Release();
        await original.WaitAsync(TestTimeout);

        var completion = link.Completions.Should().ContainSingle(
            "exactly one delivery executes and reports").Subject;
        completion.Success.Should().BeTrue();
        completion.Dispatch.Should().Be(dispatchId);
    }

    [Fact]
    public async Task Newer_dispatch_supersedes_the_running_attempt()
    {
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var taskId = Guid.NewGuid();
        var oldDispatch = Guid.NewGuid();
        var newDispatch = Guid.NewGuid();

        var oldRun = Task.Run(() => executor.ExecuteAsync(Plan(taskId, oldDispatch)));
        await WaitUntilAsync(() => executor.IsExecuting, "the old attempt must be in flight");

        // The server re-dispatched the task (wave deadline / retry): the old
        // attempt is cancelled and awaited, then the new attempt runs.
        var newRun = Task.Run(() => executor.ExecuteAsync(Plan(taskId, newDispatch)));
        await Task.WhenAll(oldRun, newRun).WaitAsync(TestTimeout);

        link.Completions.Should().HaveCount(2);
        var aborted = link.Completions.First();
        aborted.Dispatch.Should().Be(oldDispatch);
        aborted.Success.Should().BeFalse();
        aborted.Error.Should().Contain("Superseded");
        var replacement = link.Completions.Last();
        replacement.Dispatch.Should().Be(newDispatch);
        replacement.Success.Should().BeTrue();
        executor.IsExecuting.Should().BeFalse();
    }

    [Fact]
    public void TryCancel_for_an_unknown_task_is_a_noop()
    {
        var executor = BuildExecutor(new GateLink());
        executor.TryCancel(Guid.NewGuid(), "nothing runs").Should().BeFalse();
    }

    // ── B7: the machine execution queue ────────────────────────────────────

    [Fact]
    public async Task Plans_for_different_tasks_serialize_FIFO()
    {
        // Two DIFFERENT tasks (B6's single-flight only dedups the SAME task):
        // the second must not execute until the first releases the machine.
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        var runA = Task.Run(() => executor.ExecuteAsync(Plan(taskA, Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the first task must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");

        var runB = Task.Run(() => executor.ExecuteAsync(Plan(taskB, Guid.NewGuid())));
        // Give B ample time to (incorrectly) run if the gate were absent —
        // its zero-step body would complete in microseconds.
        await Task.Delay(300);
        link.Completions.Should().BeEmpty(
            "task B must be QUEUED behind task A, not executing concurrently");

        link.ReleaseFirstCompletion.Release();
        await Task.WhenAll(runA, runB).WaitAsync(TestTimeout);

        link.Completions.Select(c => c.Dep).Should().Equal([taskA, taskB],
            "the machine queue is FIFO");
        link.Completions.Should().AllSatisfy(c => c.Success.Should().BeTrue());
    }

    [Fact]
    public async Task Cancel_while_queued_aborts_without_executing()
    {
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        var runA = Task.Run(() => executor.ExecuteAsync(Plan(taskA, Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the first task must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");

        var runB = Task.Run(() => executor.ExecuteAsync(Plan(taskB, Guid.NewGuid())));
        await WaitUntilAsync(() => executor.TryCancel(taskB, "operator changed their mind"),
            "task B must be registered (queued) and cancellable");
        await runB.WaitAsync(TestTimeout);

        // B's aborted completion arrives while A still holds the machine —
        // proof the cancel didn't wait for (or take) the execution slot.
        var aborted = link.Completions.Should().ContainSingle().Subject;
        aborted.Dep.Should().Be(taskB);
        aborted.Success.Should().BeFalse();
        aborted.Error.Should().Contain("operator changed their mind");

        link.ReleaseFirstCompletion.Release();
        await runA.WaitAsync(TestTimeout);
        link.Completions.Last().Dep.Should().Be(taskA);
        link.Completions.Last().Success.Should().BeTrue();
    }

    // ── F2/F5: per-target "Allow parallel task execution" = the SHARED side ──

    [Fact]
    public async Task Parallel_flagged_tasks_co_run_on_one_machine()
    {
        // Same shape as Plans_for_different_tasks_serialize_FIFO, but BOTH targets
        // opted into parallel execution, so both take the gate's SHARED side and
        // co-run. Mutual consent (locked decision P2) is what makes this legal.
        var link = new GateLink();
        using var gate = new MachineExecutionGate();
        var executor = BuildExecutor(link, gate);
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        var runA = Task.Run(() => executor.ExecuteAsync(
            Plan(taskA, Guid.NewGuid(), allowParallel: true)));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the first task must hold a shared lease — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");
        gate.ReaderCount.Should().Be(1, "the flagged plan takes the SHARED side");
        gate.IsWriteHeld.Should().BeFalse();

        var runB = Task.Run(() => executor.ExecuteAsync(
            Plan(taskB, Guid.NewGuid(), allowParallel: true)));
        await runB.WaitAsync(TestTimeout);

        // B completed while A is still executing — two readers, no exclusion.
        var completion = link.Completions.Should().ContainSingle(
            "only task B has reported; task A is still blocked holding a shared lease").Subject;
        completion.Dep.Should().Be(taskB);
        completion.Success.Should().BeTrue();

        link.ReleaseFirstCompletion.Release();
        await runA.WaitAsync(TestTimeout);
        link.Completions.Last().Dep.Should().Be(taskA);
    }

    [Fact]
    public async Task A_parallel_flagged_task_still_waits_behind_an_exclusive_one()
    {
        // F5 — the flag is NOT a bypass. Under F2 it skipped the gate outright, so a
        // single opted-in target removed same-machine protection against every task on
        // the box, including ones that had NOT opted in. Consent is mutual: an
        // exclusive holder excludes a shared waiter (Octopus ScriptIsolationMutex
        // parity — NoIsolation takes the READ side of the same lock).
        var link = new GateLink();
        using var gate = new MachineExecutionGate();
        var executor = BuildExecutor(link, gate);
        var exclusive = Guid.NewGuid();
        var shared = Guid.NewGuid();

        var runExclusive = Task.Run(() => executor.ExecuteAsync(Plan(exclusive, Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the exclusive task must hold the machine");

        // Pin the MODES, not just the ordering. Exclusive-excludes-shared and
        // shared-excludes-exclusive are symmetric, so an ordering-only assertion holds
        // even if ModeFor is inverted — which is exactly the mutation this test is
        // named after and must therefore kill.
        gate.IsWriteHeld.Should().BeTrue("the unflagged plan takes the EXCLUSIVE side");
        gate.ReaderCount.Should().Be(0);

        var runShared = Task.Run(() => executor.ExecuteAsync(
            Plan(shared, Guid.NewGuid(), allowParallel: true)));
        await WaitUntilAsync(() => gate.QueuedCount == 1,
            "the parallel-flagged plan must be QUEUED on the gate — deterministic, "
            + "unlike a fixed delay plus 'has not started yet'");

        link.ExecutionStarted.Should().HaveCount(1,
            "the parallel-flagged task is QUEUED behind the exclusive holder — it takes " +
            "the shared side of the gate, it does not skip it");
        link.Completions.Should().BeEmpty();

        link.ReleaseFirstCompletion.Release();
        await Task.WhenAll(runExclusive, runShared).WaitAsync(TestTimeout);
        link.ExecutionStarted.Select(e => e.Dep).Should().Equal([exclusive, shared]);
    }

    [Fact]
    public async Task A_shared_release_does_not_free_the_machine_for_an_exclusive_plan()
    {
        // Regression guard, restated for F5. Pre-F5 the risk was a BYPASSING plan
        // calling Release() on a semaphore it never took, handing a phantom permit to
        // the next waiter. Post-F5 every plan holds a real lease, and the equivalent
        // corruption is a shared release that drops the reader count to zero while a
        // sibling reader is still executing — which would let an exclusive plan in
        // beside it.
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var holder = Guid.NewGuid();

        // A shared plan takes a reader and blocks in its completion.
        var runHolder = Task.Run(() => executor.ExecuteAsync(
            Plan(holder, Guid.NewGuid(), allowParallel: true)));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the shared holder must hold a reader");

        // A second shared plan co-runs and runs all the way out, releasing ITS reader.
        await executor.ExecuteAsync(Plan(Guid.NewGuid(), Guid.NewGuid(), allowParallel: true))
            .WaitAsync(TestTimeout);

        // The exclusive plan must STILL be blocked: one reader is left.
        var exclusive = Guid.NewGuid();
        var runExclusive = Task.Run(() => executor.ExecuteAsync(Plan(exclusive, Guid.NewGuid())));
        await Task.Delay(300);
        link.Completions.Select(c => c.Dep).Should().NotContain(exclusive,
            "the co-runner's release must not have zeroed the reader count while the " +
            "shared holder is still executing");

        link.ReleaseFirstCompletion.Release();
        await Task.WhenAll(runHolder, runExclusive).WaitAsync(TestTimeout);
        link.Completions.Select(c => c.Dep).Should().Contain(exclusive);
    }

    // ── F2: the execution-started report drives the server's deadline arming ──

    [Fact]
    public async Task Execution_started_is_reported_only_once_the_machine_slot_is_taken()
    {
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();
        var dispatchB = Guid.NewGuid();

        var runA = Task.Run(() => executor.ExecuteAsync(Plan(taskA, Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the first task must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");
        link.ExecutionStarted.Single().Dep.Should().Be(taskA);

        var runB = Task.Run(() => executor.ExecuteAsync(Plan(taskB, dispatchB)));
        await Task.Delay(300);
        link.ExecutionStarted.Should().HaveCount(1,
            "task B is QUEUED — reporting it now would arm its wave deadline while " +
            "it is still waiting, which is exactly the defect F2 fixes");

        link.ReleaseFirstCompletion.Release();
        await Task.WhenAll(runA, runB).WaitAsync(TestTimeout);

        link.ExecutionStarted.Select(e => e.Dep).Should().Equal([taskA, taskB]);
        link.ExecutionStarted.Last().Dispatch.Should().Be(dispatchB,
            "the report must carry the attempt key so a retired attempt cannot " +
            "re-arm the live attempt's deadline");
    }

    [Fact]
    public async Task Cancel_while_queued_reports_no_execution_start()
    {
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        var runA = Task.Run(() => executor.ExecuteAsync(Plan(taskA, Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the first task must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");

        var runB = Task.Run(() => executor.ExecuteAsync(Plan(taskB, Guid.NewGuid())));
        await WaitUntilAsync(() => executor.TryCancel(taskB, "changed their mind"),
            "task B must be registered (queued) and cancellable");
        await runB.WaitAsync(TestTimeout);

        link.ExecutionStarted.Select(e => e.Dep).Should().NotContain(taskB,
            "nothing executed, so nothing may extend a deadline");

        link.ReleaseFirstCompletion.Release();
        await runA.WaitAsync(TestTimeout);
    }

    // ── E5: the self-update guard is only live if the executor is a singleton ──

    [Fact]
    public async Task DeploymentExecutor_registered_as_singleton_keeps_the_self_update_guard_live()
    {
        // E5: ServerLinkHostedService (runs deployments) and AgentUpdateService
        // (reads IsExecuting to refuse a mid-deployment binary swap) both
        // ctor-inject DeploymentExecutor. Registered Transient they got SEPARATE
        // instances, so the updater's guard read a permanently-empty _running map.
        // Registered Singleton (the fix in Program.cs) they share one instance, so
        // a deployment in flight is visible to the updater. This mirrors the
        // registration decision — Program.cs's top-level DI is not directly
        // unit-testable, so we assert the lifetime CONTRACT the fix relies on.
        var link = new GateLink();
        var services = new ServiceCollection();
        services.AddSingleton<IServerLink>(link);
        services.AddSingleton<IPackageSource>(new NullPackageSource());
        services.AddSingleton<IArtifactSink>(new NullArtifactSink());
        services.AddSingleton(new StepPackageLoader(
            new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance));
        // The PRODUCTION registration, not a copy of it — this used to mirror Program.cs's
        // factory by hand, which meant two replicas drifting together.
        services.AddLogging();
        services.AddMachineExecutionGate();
        services.AddSingleton(Options.Create(new AgentConfig()));
        services.AddSingleton(Options.Create(new AgentUpdateConfig()));
        services.AddSingleton<ILogger<DeploymentExecutor>>(NullLogger<DeploymentExecutor>.Instance);
        services.AddSingleton<DeploymentExecutor>();   // ← the lifetime under test

        await using var sp = services.BuildServiceProvider();

        // Two independent resolutions == what the two consumers each receive.
        var runnerView = sp.GetRequiredService<DeploymentExecutor>();
        var updaterView = sp.GetRequiredService<DeploymentExecutor>();
        updaterView.Should().BeSameAs(runnerView,
            "a Transient registration would hand each consumer its own instance");

        var taskId = Guid.NewGuid();
        var run = Task.Run(() => runnerView.ExecuteAsync(Plan(taskId, Guid.NewGuid())));
        await WaitUntilAsync(() => runnerView.IsExecuting, "the deployment must register in flight");

        updaterView.IsExecuting.Should().BeTrue(
            "the self-update guard reads the SAME executor, so it sees the in-flight deployment");

        link.ReleaseFirstCompletion.Release();
        await run.WaitAsync(TestTimeout);
        updaterView.IsExecuting.Should().BeFalse();
    }

    // ── Bounded gate-wait after a supersede force-detaches a stuck predecessor ──

    [Fact]
    public async Task Wedged_gate_after_supersede_force_detach_escalates_instead_of_hanging()
    {
        // A superseded old attempt that ignores cancellation keeps holding the
        // machine gate. After the (shortened) unwind timeout it is force-detached;
        // the new attempt then cannot acquire the gate and must escalate within
        // the (shortened) bounded wait rather than hang forever behind the stuck
        // step (a zombie agent heartbeating Online but never executing again).
        var link = new WedgeLink();
        var executor = new DeploymentExecutor(
            link,
            new NullPackageSource(),
            new NullArtifactSink(),
            new StepPackageLoader(
                new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
            new MachineExecutionGate(),
            Options.Create(new AgentConfig()),
            Options.Create(new AgentUpdateConfig()),
            NullLogger<DeploymentExecutor>.Instance)
        {
            SupersedeUnwindTimeout = TimeSpan.FromMilliseconds(150),
            WedgedGateAcquireTimeout = TimeSpan.FromMilliseconds(150),
        };

        var taskId = Guid.NewGuid();
        var oldDispatch = Guid.NewGuid();
        var newDispatch = Guid.NewGuid();

        // Old attempt: acquires the gate then gets stuck in its completion holding
        // the gate, IGNORING cancellation (models a non-cooperative step).
        var oldRun = Task.Run(() => executor.ExecuteAsync(Plan(taskId, oldDispatch)));
        await WaitUntilAsync(() => executor.IsExecuting, "the old attempt must hold the gate");

        // New attempt supersedes: the cancel has no effect on the stuck old attempt,
        // so it is force-detached, and the new attempt cannot take the still-held
        // gate. It must NOT hang — WaitAsync(TestTimeout) proves it returns.
        await executor.ExecuteAsync(Plan(taskId, newDispatch)).WaitAsync(TestTimeout);

        var escalation = link.Completions.Should().ContainSingle(
            "only the new attempt's wedged-gate escalation is recorded").Subject;
        escalation.Dispatch.Should().Be(newDispatch);
        escalation.Success.Should().BeFalse("an abandoned attempt cannot report success");
        // Asserts the observation, not a diagnosis. The message deliberately no longer
        // claims the agent is "wedged" or that a previous task failed to release: the slot
        // can equally be held — or, on a writer-fair gate, merely headed — by the agent's
        // own self-upgrade swap window, and telling an operator to restart a healthy agent
        // is worse than saying less.
        escalation.Error.Should().Contain("machine execution slot")
            .And.Contain("force-detached");
        executor.IsExecuting.Should().BeFalse();

        // Let the stuck old attempt unwind so the test does not leak it.
        link.ReleaseStuck.Release();
        await oldRun.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Parallel_flag_still_refuses_to_run_two_attempts_of_the_same_task()
    {
        // F2-followup 2, restated for F5. AllowParallelTaskExecution opts out of
        // serializing against OTHER tasks; it must NOT opt out of B6's same-task
        // guarantee. A stuck predecessor that ignores cancellation gets force-detached,
        // and the machine gate is then the only thing keeping the two apart — which it
        // can only do if at least ONE of the pair is exclusive. Here BOTH attempts are
        // shared, so the gate would hand the successor a second READ lease immediately
        // and both would run over the same app pool / site path / services. The new
        // attempt must therefore be ABANDONED, not started.
        // NOTE the F5 reason differs from F2's: under F2 a flagged plan took NO lease
        // (the flag was a bypass), so the refusal was "there is no slot to serialize
        // against". Now the predecessor holds a real READ lease and the refusal is "the
        // gate would admit both as readers". Same outcome, different mechanism — do not
        // simplify this away on the strength of the old rationale.
        var link = new WedgeLink();
        var executor = new DeploymentExecutor(
            link,
            new NullPackageSource(),
            new NullArtifactSink(),
            new StepPackageLoader(
                new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
            new MachineExecutionGate(),
            Options.Create(new AgentConfig()),
            Options.Create(new AgentUpdateConfig()),
            NullLogger<DeploymentExecutor>.Instance)
        {
            SupersedeUnwindTimeout = TimeSpan.FromMilliseconds(150),
            WedgedGateAcquireTimeout = TimeSpan.FromMilliseconds(150),
        };

        var taskId = Guid.NewGuid();
        var oldDispatch = Guid.NewGuid();
        var newDispatch = Guid.NewGuid();

        var oldRun = Task.Run(() => executor.ExecuteAsync(
            Plan(taskId, oldDispatch, allowParallel: true)));
        await WaitUntilAsync(() => executor.IsExecuting,
            "the old attempt must be in flight, holding a SHARED lease (F5: the flag is "
            + "no longer a bypass, so it does hold one)");

        await executor.ExecuteAsync(Plan(taskId, newDispatch, allowParallel: true))
            .WaitAsync(TestTimeout);

        var refusal = link.Completions.Should().ContainSingle(
            "only the new attempt's refusal is recorded").Subject;
        refusal.Dispatch.Should().Be(newDispatch);
        refusal.Success.Should().BeFalse();
        refusal.Error.Should().Contain("could not be serialized",
            "the refusal must name the real reason — the gate would admit both attempts "
            + "as readers — not the wedged-gate reason");
        // The stuck predecessor legitimately reported one (it acquired its shared lease
        // and entered the body); the ABANDONED attempt must not, because that report sits
        // inside the execution body it never reached.
        link.ExecutionStarted.Select(e => e.Dispatch).Should().Equal([oldDispatch],
            "only the predecessor executed — the second attempt was abandoned before "
            + "entering the execution body");

        link.ReleaseStuck.Release();
        await oldRun.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Two_shared_attempts_take_the_bounded_wait_when_the_cap_is_one()
    {
        // The cap-of-1 box, which is the whole reason the refusal asks the GATE
        // (WouldAdmitConcurrently) instead of hardcoding "both shared means both admitted".
        // With Agent:MaxConcurrentSharedWork = 1 the gate DOES serialize two readers, so the
        // same-task guarantee holds without refusing — the successor must take the bounded
        // wedged wait and escalate on expiry, exactly as it would against an exclusive
        // predecessor. Refusing here would abandon a retry the gate would have kept apart
        // correctly; admitting both would be the F2-followup-2 violation.
        //
        // Previously untested end to end: WouldAdmitConcurrently had a pure predicate test,
        // but both executor tests built a default cap-8 gate, so the only behaviour the cap
        // term actually changes had no coverage at all.
        var link = new WedgeLink();
        var executor = new DeploymentExecutor(
            link,
            new NullPackageSource(),
            new NullArtifactSink(),
            new StepPackageLoader(
                new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
            new MachineExecutionGate { MaxSharedHolders = 1 },
            Options.Create(new AgentConfig { MaxConcurrentSharedWork = 1 }),
            Options.Create(new AgentUpdateConfig()),
            NullLogger<DeploymentExecutor>.Instance)
        {
            SupersedeUnwindTimeout = TimeSpan.FromMilliseconds(150),
            WedgedGateAcquireTimeout = TimeSpan.FromMilliseconds(150),
        };

        var taskId = Guid.NewGuid();
        var oldDispatch = Guid.NewGuid();
        var newDispatch = Guid.NewGuid();

        var oldRun = Task.Run(() => executor.ExecuteAsync(
            Plan(taskId, oldDispatch, allowParallel: true)));
        await WaitUntilAsync(() => executor.IsExecuting,
            "the old attempt must be in flight holding the single SHARED slot");

        await executor.ExecuteAsync(Plan(taskId, newDispatch, allowParallel: true))
            .WaitAsync(TestTimeout);

        var escalation = link.Completions.Should().ContainSingle().Subject;
        escalation.Dispatch.Should().Be(newDispatch);
        escalation.Success.Should().BeFalse();
        escalation.Error.Should().Contain("machine execution slot",
            "with a cap of 1 the gate excludes the two readers, so the successor takes the "
            + "bounded wait and times out")
            .And.NotContain("could not be serialized",
                "the gate CAN serialize them at this cap, so the outright refusal is wrong");
        link.ExecutionStarted.Select(e => e.Dispatch).Should().Equal([oldDispatch],
            "the successor never entered the execution body");

        link.ReleaseStuck.Release();
        await oldRun.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task A_shared_retry_of_an_exclusive_stuck_attempt_escalates_rather_than_refusing()
    {
        // F5 — the refusal above must key on the PAIR of modes, not on the successor's
        // mode alone. Here the predecessor took the EXCLUSIVE side (the flag was off
        // when it was dispatched) and an operator then enabled parallel execution, so
        // the retry arrives SHARED. The gate excludes a shared waiter from an exclusive
        // holder perfectly well, so refusing outright would abandon an attempt that was
        // never at risk — and, because the NEXT re-dispatch arrives with
        // forceDetachedStuck false, would send it into the unbounded wait with no
        // escalation at all. It must take the bounded wedged-gate path instead, which is
        // distinguishable by its error text.
        var link = new WedgeLink();
        var executor = new DeploymentExecutor(
            link,
            new NullPackageSource(),
            new NullArtifactSink(),
            new StepPackageLoader(
                new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
            new MachineExecutionGate(),
            Options.Create(new AgentConfig()),
            Options.Create(new AgentUpdateConfig()),
            NullLogger<DeploymentExecutor>.Instance)
        {
            SupersedeUnwindTimeout = TimeSpan.FromMilliseconds(150),
            WedgedGateAcquireTimeout = TimeSpan.FromMilliseconds(150),
        };

        var taskId = Guid.NewGuid();
        var oldDispatch = Guid.NewGuid();
        var newDispatch = Guid.NewGuid();

        // Predecessor: flag OFF → EXCLUSIVE, and stuck.
        var oldRun = Task.Run(() => executor.ExecuteAsync(Plan(taskId, oldDispatch)));
        await WaitUntilAsync(() => executor.IsExecuting,
            "the old attempt must hold the EXCLUSIVE side");

        // Retry: flag ON → SHARED.
        await executor.ExecuteAsync(Plan(taskId, newDispatch, allowParallel: true))
            .WaitAsync(TestTimeout);

        var escalation = link.Completions.Should().ContainSingle().Subject;
        escalation.Dispatch.Should().Be(newDispatch);
        escalation.Success.Should().BeFalse();
        escalation.Error.Should().Contain("machine execution slot",
            "an exclusive predecessor DOES exclude a shared successor, so this is the "
            + "bounded acquire-timeout escalation, not the 'could not be serialized' refusal");
        escalation.Error.Should().NotContain("could not be serialized");

        link.ReleaseStuck.Release();
        await oldRun.WaitAsync(TestTimeout);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Builds an executor over a caller-supplied gate, so a test can assert
    /// which SIDE of it a plan took. Without that, mode assertions reduce to ordering
    /// assertions, which hold under an inverted mapping too.</summary>
    private static DeploymentExecutor BuildExecutor(GateLink link, MachineExecutionGate gate) => new(
        link,
        new NullPackageSource(),
        new NullArtifactSink(),
        new StepPackageLoader(
            new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
        gate,
        Options.Create(new AgentConfig()),
        Options.Create(new AgentUpdateConfig()),
        NullLogger<DeploymentExecutor>.Instance);

    private static DeploymentExecutor BuildExecutor(GateLink link) => new(
        link,
        new NullPackageSource(),
        new NullArtifactSink(),
        new StepPackageLoader(
            new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
        new MachineExecutionGate(),
        Options.Create(new AgentConfig()),
        Options.Create(new AgentUpdateConfig()),
        NullLogger<DeploymentExecutor>.Instance);

    private static DeploymentPlan Plan(
        Guid taskId, Guid dispatchId, bool allowParallel = false) => new(
        DeploymentId: taskId,
        EnvironmentName: "test",
        Steps: [],
        Variables: new Dictionary<string, string>(),
        ArrayVariables: new Dictionary<string, string[]>(),
        DispatchId: dispatchId,
        AllowParallelTaskExecution: allowParallel);

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

    /// <summary>Records completions; holds the FIRST one in flight (honoring the
    /// caller's token — the executor's success path passes the run token, so a
    /// cancel/supersede unblocks it with an OperationCanceledException exactly
    /// like a real slow send would observe).</summary>
    private sealed class GateLink : IServerLink
    {
        public ConcurrentQueue<(Guid Dep, Guid Dispatch, bool Success, string? Error)> Completions { get; } = new();
        /// <summary>F2 — every execution-started report, in order. The executor sends
        /// one per attempt right AFTER it takes (or bypasses) the machine gate.</summary>
        public ConcurrentQueue<(Guid Dep, Guid Dispatch)> ExecutionStarted { get; } = new();
        public SemaphoreSlim ReleaseFirstCompletion { get; } = new(0);
        private int _completionCalls;

        public async Task CompleteDeploymentAsync(
            Guid deploymentId, Guid dispatchId, bool success, string? errorMessage, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _completionCalls) == 1)
            {
                await ReleaseFirstCompletion.WaitAsync(ct);
            }
            Completions.Enqueue((deploymentId, dispatchId, success, errorMessage));
        }

        public bool IsConnected => true;
        public Task StartAsync(string serverUrl, Func<string?> agentJwtProvider, string? releaseId, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<AgentRegistrationResult> RegisterAsync(AgentRegistrationRequest request, CancellationToken ct)
            => Task.FromResult(new AgentRegistrationResult(true, AgentContract.CurrentVersion));
        public Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task ReportStatusAsync(string status, CancellationToken ct) => Task.CompletedTask;
        public Task AppendLogAsync(Guid deploymentId, Guid dispatchId, int stepIndex, string level, string message, CancellationToken ct) => Task.CompletedTask;
        public Task ReportStepCompletedAsync(Guid deploymentId, Guid dispatchId, int stepIndex, string stepName, bool success,
            string? errorMessage, IReadOnlyDictionary<string, string> outputVariables,
            IReadOnlyCollection<string> sensitiveOutputNames, CancellationToken ct) => Task.CompletedTask;
        public Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct) => Task.CompletedTask;
        public Task ReportExecutionStartedAsync(Guid deploymentId, Guid dispatchId, CancellationToken ct)
        {
            ExecutionStarted.Enqueue((deploymentId, dispatchId));
            return Task.CompletedTask;
        }
        public void OnRunDeployment(Func<DeploymentPlan, Task> handler) { }
        public void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler) { }
        public void OnCancelDeployment(Func<Guid, string?, Task> handler) { }
        public void OnClosed(Func<Exception?, Task> handler) { }
        public void OnReconnected(Func<Task> handler) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Models a non-cooperative step: the FIRST completion blocks
    /// holding the machine gate and IGNORES cancellation (the supersede token has
    /// no effect), forcing the old attempt to be force-detached; the test releases
    /// it at teardown. Later completions (the new attempt's wedged-gate
    /// escalation) are recorded.</summary>
    private sealed class WedgeLink : IServerLink
    {
        public ConcurrentQueue<(Guid Dep, Guid Dispatch, bool Success, string? Error)> Completions { get; } = new();
        /// <summary>F2-followup 2 — asserted: an ABANDONED attempt must report no
        /// execution start, because that report sits inside the execution body.</summary>
        public ConcurrentQueue<(Guid Dep, Guid Dispatch)> ExecutionStarted { get; } = new();
        public SemaphoreSlim ReleaseStuck { get; } = new(0);
        private int _completionCalls;

        public async Task CompleteDeploymentAsync(
            Guid deploymentId, Guid dispatchId, bool success, string? errorMessage, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _completionCalls) == 1)
            {
                // Hold the gate ignoring the run's cancellation token — released
                // only by the test at teardown (CancellationToken.None, not ct).
                // The old attempt's completion is intentionally never recorded.
                await ReleaseStuck.WaitAsync(CancellationToken.None);
                return;
            }
            Completions.Enqueue((deploymentId, dispatchId, success, errorMessage));
        }

        public bool IsConnected => true;
        public Task StartAsync(string serverUrl, Func<string?> agentJwtProvider, string? releaseId, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<AgentRegistrationResult> RegisterAsync(AgentRegistrationRequest request, CancellationToken ct)
            => Task.FromResult(new AgentRegistrationResult(true, AgentContract.CurrentVersion));
        public Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task ReportStatusAsync(string status, CancellationToken ct) => Task.CompletedTask;
        public Task AppendLogAsync(Guid deploymentId, Guid dispatchId, int stepIndex, string level, string message, CancellationToken ct) => Task.CompletedTask;
        public Task ReportStepCompletedAsync(Guid deploymentId, Guid dispatchId, int stepIndex, string stepName, bool success,
            string? errorMessage, IReadOnlyDictionary<string, string> outputVariables,
            IReadOnlyCollection<string> sensitiveOutputNames, CancellationToken ct) => Task.CompletedTask;
        public Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct) => Task.CompletedTask;
        public Task ReportExecutionStartedAsync(Guid deploymentId, Guid dispatchId, CancellationToken ct)
        {
            ExecutionStarted.Enqueue((deploymentId, dispatchId));
            return Task.CompletedTask;
        }
        public void OnRunDeployment(Func<DeploymentPlan, Task> handler) { }
        public void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler) { }
        public void OnCancelDeployment(Func<Guid, string?, Task> handler) { }
        public void OnClosed(Func<Exception?, Task> handler) { }
        public void OnReconnected(Func<Task> handler) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullPackageSource : IPackageSource
    {
        public Task<string> DownloadAsync(string packageId, string version, string destDirectory, CancellationToken ct)
            => throw new NotSupportedException("zero-step plans never download packages");
    }

    private sealed class NullArtifactSink : IArtifactSink
    {
        public Task<string?> UploadAsync(Guid deploymentId, string stepName, string filePath, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }
}
