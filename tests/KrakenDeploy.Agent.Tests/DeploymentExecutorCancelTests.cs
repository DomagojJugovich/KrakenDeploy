using System.Collections.Concurrent;
using FluentAssertions;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
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

    // ── F2: per-target "Allow parallel task execution" ─────────────────────

    [Fact]
    public async Task Parallel_flag_lets_two_tasks_interleave_on_one_machine()
    {
        // Same shape as Plans_for_different_tasks_serialize_FIFO, but task B's
        // target opted into parallel execution: it must NOT wait for A.
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        var runA = Task.Run(() => executor.ExecuteAsync(Plan(taskA, Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the first task must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");

        var runB = Task.Run(() => executor.ExecuteAsync(
            Plan(taskB, Guid.NewGuid(), allowParallel: true)));
        await runB.WaitAsync(TestTimeout);

        // B completed while A still holds the slot — the gate was bypassed.
        var completion = link.Completions.Should().ContainSingle(
            "only task B has reported; task A is still blocked holding the machine").Subject;
        completion.Dep.Should().Be(taskB);
        completion.Success.Should().BeTrue();

        link.ReleaseFirstCompletion.Release();
        await runA.WaitAsync(TestTimeout);
        link.Completions.Last().Dep.Should().Be(taskA);
    }

    [Fact]
    public async Task Parallel_flag_does_not_release_a_gate_it_never_took()
    {
        // Regression guard: a bypassing plan must not Release() the semaphore on
        // its way out — that would hand a phantom permit to the next waiter and
        // silently break serialization for every later task on this machine.
        var link = new GateLink();
        var executor = BuildExecutor(link);
        var holder = Guid.NewGuid();

        var runHolder = Task.Run(() => executor.ExecuteAsync(Plan(holder, Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the first task must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");

        // A bypassing plan runs to completion while the holder keeps the slot.
        await executor.ExecuteAsync(Plan(Guid.NewGuid(), Guid.NewGuid(), allowParallel: true))
            .WaitAsync(TestTimeout);

        // A SERIAL plan must still be blocked behind the holder.
        var serial = Guid.NewGuid();
        var runSerial = Task.Run(() => executor.ExecuteAsync(Plan(serial, Guid.NewGuid())));
        await Task.Delay(300);
        link.Completions.Select(c => c.Dep).Should().NotContain(serial,
            "the bypassing plan must not have leaked a permit to the serial queue");

        link.ReleaseFirstCompletion.Release();
        await Task.WhenAll(runHolder, runSerial).WaitAsync(TestTimeout);
        link.Completions.Select(c => c.Dep).Should().Contain(serial);
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
        services.AddSingleton<MachineExecutionGate>();
        services.AddSingleton(Options.Create(new AgentConfig()));
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
        escalation.Success.Should().BeFalse("a wedged attempt cannot report success");
        escalation.Error.Should().Contain("wedged");
        executor.IsExecuting.Should().BeFalse();

        // Let the stuck old attempt unwind so the test does not leak it.
        link.ReleaseStuck.Release();
        await oldRun.WaitAsync(TestTimeout);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DeploymentExecutor BuildExecutor(GateLink link) => new(
        link,
        new NullPackageSource(),
        new NullArtifactSink(),
        new StepPackageLoader(
            new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
        new MachineExecutionGate(),
        Options.Create(new AgentConfig()),
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
            => Task.CompletedTask;
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
