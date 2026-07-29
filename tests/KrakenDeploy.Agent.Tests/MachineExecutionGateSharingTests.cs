using System.Collections.Concurrent;
using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Agent.Adhoc;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// F2/F5 — every kind of work on one machine shares ONE execution gate.
/// <para>
/// Pre-F2 the gate lived inside <see cref="DeploymentExecutor"/> and ad-hoc scripts
/// bypassed it entirely (an audited defect): an operator-approved diagnostic script
/// could run straight into a deployment's file / IIS / service operations on the same
/// box. F2 moved it into the shared <see cref="MachineExecutionGate"/> singleton, but
/// left <c>AllowParallelTaskExecution</c> meaning "skip the gate", which reopened the
/// same hole for any opted-in target. F5 made the gate a reader-writer lock, so the
/// flag only chooses a SIDE — and put the self-upgrade under the same gate, closing
/// the audit CLASH where a binary swap killed running ad-hoc work.
/// </para>
/// <para>
/// These tests wire the deployment, ad-hoc and updater paths from ONE gate instance,
/// exactly as <c>Program.cs</c> and <c>ServerLinkHostedService</c> do.
/// </para>
/// </summary>
public sealed class MachineExecutionGateSharingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Adhoc_script_waits_its_turn_behind_a_running_deployment()
    {
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var deployments = BuildDeploymentExecutor(link, gate);
        var (priv, pem) = NewKeyPair();
        using var _ = priv;
        var runner = new SignallingRunner();
        var adhoc = BuildAdhocExecutor(link, gate, pem, runner);

        // A deployment takes the machine and stalls (blocked reporting completion).
        var deployTask = Task.Run(() => deployments.ExecuteAsync(Plan(Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the deployment must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");

        // The operator-approved script arrives while the deployment holds the slot.
        var sessionId = Guid.NewGuid();
        var adhocTask = Task.Run(() => adhoc.HandleAsync(Command(sessionId, priv)));

        await Task.Delay(300);
        runner.Invocations.Should().Be(0,
            "the script must QUEUE behind the deployment, not interleave with its " +
            "file / IIS / service operations");
        link.AdhocResults.Should().BeEmpty();

        // Deployment finishes → the script gets the machine and runs.
        link.ReleaseFirstCompletion.Release();
        await Task.WhenAll(deployTask, adhocTask).WaitAsync(TestTimeout);

        runner.Invocations.Should().Be(1);
        var result = link.AdhocResults.Should().ContainSingle().Subject;
        result.AgentError.Should().BeNull();
        result.SessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task Adhoc_script_refuses_rather_than_running_after_the_server_gave_up()
    {
        // The gate wait is bounded and deliberately shorter than the server's
        // per-target adhoc timeout, so a script the dispatcher already resolved as
        // "timed out" never executes late. Otherwise an operator who saw the
        // timeout and approved a fresh iteration would get BOTH scripts.
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var deployments = BuildDeploymentExecutor(link, gate);
        var (priv, pem) = NewKeyPair();
        using var _ = priv;
        var runner = new SignallingRunner();
        var adhoc = BuildAdhocExecutor(
            link, gate, pem, runner, gateWait: TimeSpan.FromMilliseconds(200));

        var deployTask = Task.Run(() => deployments.ExecuteAsync(Plan(Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the deployment must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");

        await adhoc.HandleAsync(Command(Guid.NewGuid(), priv)).WaitAsync(TestTimeout);

        runner.Invocations.Should().Be(0, "the script MUST NOT run after the wait expired");
        var result = link.AdhocResults.Should().ContainSingle().Subject;
        result.ExitCode.Should().Be(-1);
        result.AgentError.Should().Contain("held this machine for the whole");

        // The refusal must not have released a slot it never held.
        link.ReleaseFirstCompletion.Release();
        await deployTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Adhoc_script_with_the_shared_flag_still_waits_behind_an_exclusive_deployment()
    {
        // F5 — AllowParallelTaskExecution chooses the gate's SIDE, it is not a bypass.
        // Pre-F5 this exact command ran immediately, straight into the deployment's
        // file / IIS / service operations. Consent is mutual: the deployment did not
        // opt in, so the script waits.
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var deployments = BuildDeploymentExecutor(link, gate);
        var (priv, pem) = NewKeyPair();
        using var _ = priv;
        var runner = new SignallingRunner();
        var adhoc = BuildAdhocExecutor(link, gate, pem, runner);

        var deployTask = Task.Run(() => deployments.ExecuteAsync(Plan(Guid.NewGuid())));
        await WaitUntilAsync(() => link.ExecutionStarted.Count == 1,
            "the deployment must hold the machine — its execution-started report is "
            + "emitted right after acquisition, whereas IsExecuting flips at registration");

        var adhocTask = Task.Run(() =>
            adhoc.HandleAsync(Command(Guid.NewGuid(), priv, allowParallel: true)));

        await Task.Delay(300);
        runner.Invocations.Should().Be(0,
            "a SHARED ad-hoc script is still excluded by an EXCLUSIVE deployment");
        link.AdhocResults.Should().BeEmpty();

        link.ReleaseFirstCompletion.Release();
        await Task.WhenAll(deployTask, adhocTask).WaitAsync(TestTimeout);

        runner.Invocations.Should().Be(1, "it runs once the deployment lets go");
        link.AdhocResults.Should().ContainSingle().Which.AgentError.Should().BeNull();
    }

    [Fact]
    public async Task Two_shared_adhoc_scripts_co_run()
    {
        // The AI ad-hoc flow is READ-always (locked decision P5), so two approved
        // diagnostics on one box must not serialize against each other — that was the
        // motivation for a reader-writer gate rather than a plain mutex.
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var (priv, pem) = NewKeyPair();
        using var _ = priv;

        var blocking = new SignallingRunner { BlockUntilReleased = true };
        var firstAdhoc = BuildAdhocExecutor(link, gate, pem, blocking);
        var firstTask = Task.Run(() =>
            firstAdhoc.HandleAsync(Command(Guid.NewGuid(), priv, allowParallel: true)));
        await WaitUntilAsync(() => blocking.Invocations == 1,
            "the first script must be running (holding a shared lease)");

        var second = new SignallingRunner();
        var secondAdhoc = BuildAdhocExecutor(link, gate, pem, second);
        await secondAdhoc.HandleAsync(Command(Guid.NewGuid(), priv, allowParallel: true))
            .WaitAsync(TestTimeout);

        second.Invocations.Should().Be(1,
            "both scripts hold the SHARED side, so the second does not queue");
        gate.ReaderCount.Should().Be(1, "the first script is still executing");

        blocking.Release.Release();
        await firstTask.WaitAsync(TestTimeout);
        gate.IsHeld.Should().BeFalse();
    }

    [Fact]
    public async Task An_exclusive_adhoc_script_excludes_a_shared_one()
    {
        // WP16's script console default: the per-run "allow running concurrently"
        // checkbox unchecked → false → EXCLUSIVE. A hand-written script has no mode
        // gate, so exclusive-by-default is the safe default and it must exclude even
        // the read-always AI flow.
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var (priv, pem) = NewKeyPair();
        using var _ = priv;

        var exclusiveRunner = new SignallingRunner { BlockUntilReleased = true };
        var exclusiveAdhoc = BuildAdhocExecutor(link, gate, pem, exclusiveRunner);
        var exclusiveTask = Task.Run(() =>
            exclusiveAdhoc.HandleAsync(Command(Guid.NewGuid(), priv)));
        await WaitUntilAsync(() => exclusiveRunner.Invocations == 1,
            "the console-style script must be running (holding the exclusive side)");
        gate.IsWriteHeld.Should().BeTrue();

        var sharedRunner = new SignallingRunner();
        var sharedAdhoc = BuildAdhocExecutor(link, gate, pem, sharedRunner);
        var sharedTask = Task.Run(() =>
            sharedAdhoc.HandleAsync(Command(Guid.NewGuid(), priv, allowParallel: true)));

        await Task.Delay(300);
        sharedRunner.Invocations.Should().Be(0,
            "an exclusive ad-hoc script excludes a shared one just as a deployment would");

        exclusiveRunner.Release.Release();
        await Task.WhenAll(exclusiveTask, sharedTask).WaitAsync(TestTimeout);
        sharedRunner.Invocations.Should().Be(1);
    }

    // ── F5 / P8: the self-upgrade participates in the same gate ─────────────

    [Fact]
    public async Task The_updater_waits_for_adhoc_work_that_IsExecuting_cannot_see()
    {
        // The 2026-07-25 parallel-safety audit CLASH. AgentUpdateService gated the
        // binary swap on DeploymentExecutor.IsExecuting, which only ever reflects
        // deployments and runbook runs — an operator's ad-hoc script was invisible, so
        // a maintenance-window swap killed it mid-run. The swap now takes the gate's
        // EXCLUSIVE side, which every kind of work participates in.
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var deployments = BuildDeploymentExecutor(link, gate);
        var (priv, pem) = NewKeyPair();
        using var _ = priv;

        var runner = new SignallingRunner { BlockUntilReleased = true };
        var adhoc = BuildAdhocExecutor(link, gate, pem, runner);
        var adhocTask = Task.Run(() =>
            adhoc.HandleAsync(Command(Guid.NewGuid(), priv, allowParallel: true)));
        await WaitUntilAsync(() => runner.Invocations == 1, "the script must be running");

        deployments.IsExecuting.Should().BeFalse(
            "this is the blind spot: the ad-hoc script is invisible to the old guard");

        var (busyLease, busyOutcome) = await AgentUpdateService.AcquireSwapGateAsync(
            gate, TimeSpan.FromMilliseconds(200), default);
        busyOutcome.Should().Be(AgentUpdateService.SwapGate.Busy,
            "the swap must NOT proceed while an ad-hoc script is running");
        busyLease.Should().BeNull();

        runner.Release.Release();
        await adhocTask.WaitAsync(TestTimeout);

        var (freeLease, freeOutcome) = await AgentUpdateService.AcquireSwapGateAsync(
            gate, TimeSpan.FromSeconds(5), default);
        freeOutcome.Should().Be(AgentUpdateService.SwapGate.Acquired,
            "once the machine is idle the swap window opens");
        freeLease.Should().NotBeNull();
        freeLease!.Dispose();
    }

    [Fact]
    public async Task The_updater_blocks_new_work_while_it_holds_the_swap_window()
    {
        // The other half of P8: the pre-F5 check-to-swap gap was a TOCTOU — work could
        // start between reading IsExecuting and moving the directory. Holding the
        // EXCLUSIVE side closes it, and because the gate is writer-fair a QUEUED
        // updater already blocks new work from starting.
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var deployments = BuildDeploymentExecutor(link, gate);
        var (priv, pem) = NewKeyPair();
        using var _ = priv;
        var runner = new SignallingRunner();
        var adhoc = BuildAdhocExecutor(link, gate, pem, runner);

        var (lease, outcome) = await AgentUpdateService.AcquireSwapGateAsync(
            gate, TimeSpan.FromSeconds(5), default);
        outcome.Should().Be(AgentUpdateService.SwapGate.Acquired);

        using (lease)
        {
            var taskId = Guid.NewGuid();
            var deployTask = Task.Run(() => deployments.ExecuteAsync(Plan(taskId)));
            var adhocTask = Task.Run(() =>
                adhoc.HandleAsync(Command(Guid.NewGuid(), priv, allowParallel: true)));

            await Task.Delay(300);
            link.ExecutionStarted.Should().BeEmpty(
                "no deployment may start inside the swap window");
            runner.Invocations.Should().Be(0,
                "no ad-hoc script may start inside the swap window either");

            lease!.Dispose(); // the real updater exits the process instead
            link.ReleaseFirstCompletion.Release();
            await Task.WhenAll(deployTask, adhocTask).WaitAsync(TestTimeout);
        }

        link.ExecutionStarted.Should().ContainSingle("the deployment resumed after the swap");
        runner.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task A_deployment_waits_behind_a_running_adhoc_script()
    {
        // The symmetry matters: bringing adhoc under the gate is only a real fix if
        // it also BLOCKS a deployment, not just gets blocked by one.
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var deployments = BuildDeploymentExecutor(link, gate);
        var (priv, pem) = NewKeyPair();
        using var _ = priv;
        var runner = new SignallingRunner { BlockUntilReleased = true };
        var adhoc = BuildAdhocExecutor(link, gate, pem, runner);

        var adhocTask = Task.Run(() => adhoc.HandleAsync(Command(Guid.NewGuid(), priv)));
        await WaitUntilAsync(() => runner.Invocations == 1,
            "the script must be running (holding the machine)");

        var taskId = Guid.NewGuid();
        var deployTask = Task.Run(() => deployments.ExecuteAsync(Plan(taskId)));
        await Task.Delay(300);
        link.ExecutionStarted.Should().BeEmpty(
            "the deployment is queued behind the script, so it has not started executing");

        runner.Release.Release();
        // The deployment's own completion send is the link's blocking one; let it
        // through so the run can finish once it inherits the machine.
        link.ReleaseFirstCompletion.Release();
        await Task.WhenAll(adhocTask, deployTask).WaitAsync(TestTimeout);

        link.ExecutionStarted.Should().ContainSingle().Which.Dep.Should().Be(taskId);
    }

    [Fact]
    public async Task Adhoc_script_that_outruns_its_budget_is_killed_and_releases_the_machine()
    {
        // F2-followup 3. F2 bounded only the gate WAIT, so a script that acquired the
        // slot then hung held the machine FOREVER: the invoker got
        // CancellationToken.None, ScriptRunner has no internal timeout, and no ad-hoc
        // abort exists on the wire. Every later deployment to that box then failed at
        // the server's backstop, repeatedly, until the agent was restarted.
        using var gate = new MachineExecutionGate();
        var link = new SharedLink();
        var (priv, pem) = NewKeyPair();
        using var _ = priv;
        // Never returns on its own — only the budget can end it.
        var runner = new SignallingRunner { BlockUntilReleased = true };
        var adhoc = BuildAdhocExecutor(
            link, gate, pem, runner, gateWait: TimeSpan.FromMilliseconds(300));

        await adhoc.HandleAsync(Command(Guid.NewGuid(), priv)).WaitAsync(TestTimeout);

        runner.Invocations.Should().Be(1, "the script did start — it acquired the slot");
        var result = link.AdhocResults.Should().ContainSingle().Subject;
        result.ExitCode.Should().Be(-1);
        result.AgentError.Should().Contain("did not finish within its",
            "the operator must see that the agent terminated it, not a bare server timeout");
        gate.IsHeld.Should().BeFalse(
            "the machine must be free again — a hung script must not strand the slot");

        // A deployment can now take the machine immediately.
        var deployments = BuildDeploymentExecutor(link, gate);
        var taskId = Guid.NewGuid();
        link.ReleaseFirstCompletion.Release();
        await deployments.ExecuteAsync(Plan(taskId)).WaitAsync(TestTimeout);
        link.ExecutionStarted.Should().Contain(e => e.Dep == taskId);

        runner.Release.Release(); // let the fake's own wait unwind
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DeploymentExecutor BuildDeploymentExecutor(
        IServerLink link, MachineExecutionGate gate) => new(
        link,
        new NullPackageSource(),
        new NullArtifactSink(),
        new StepPackageLoader(
            new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
        gate,
        Options.Create(new AgentConfig()),
        NullLogger<DeploymentExecutor>.Instance);

    private static AdhocScriptExecutor BuildAdhocExecutor(
        IServerLink link, MachineExecutionGate gate, string publicPem,
        IAdhocScriptInvoker runner, TimeSpan? gateWait = null)
        => new(
            link,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Adhoc:TrustedPublicKey"] = publicPem,
                })
                .Build(),
            runner,
            gate,
            NullLogger<AdhocScriptExecutor>.Instance)
        {
            // Pass the nullable straight through: TimeSpan.Zero is now a MEANINGFUL
            // value ("expire immediately"), not the "unset" sentinel it used to be,
            // so flattening null to zero here would make every test refuse at once.
            MaxTotalDuration = gateWait,
        };

    private static (RSA Private, string PublicPem) NewKeyPair()
    {
        var priv = RSA.Create(2048);
        return (priv, priv.ExportSubjectPublicKeyInfoPem());
    }

    private const string Script = "Get-Date";

    private static AdhocScriptCommand Command(
        Guid sessionId, RSA priv, bool allowParallel = false)
        => new(
            SessionId:  sessionId,
            IterNumber: 1,
            Script:     Script,
            Signature:  AdhocScriptSigner.Sign(sessionId, 1, Script, priv),
            AllowParallelTaskExecution: allowParallel);

    private static DeploymentPlan Plan(Guid taskId) => new(
        DeploymentId: taskId,
        EnvironmentName: "test",
        Steps: [],
        Variables: new Dictionary<string, string>(),
        ArrayVariables: new Dictionary<string, string[]>(),
        DispatchId: Guid.NewGuid());

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

    /// <summary>Serves both executors. The FIRST deployment completion blocks
    /// (holding the machine slot) so a test can act while it is in flight.</summary>
    private sealed class SharedLink : IServerLink
    {
        public ConcurrentQueue<(Guid Dep, Guid Dispatch)> ExecutionStarted { get; } = new();
        public ConcurrentQueue<AdhocScriptResult> AdhocResults { get; } = new();
        public SemaphoreSlim ReleaseFirstCompletion { get; } = new(0);
        private int _completionCalls;

        public async Task CompleteDeploymentAsync(
            Guid deploymentId, Guid dispatchId, bool success, string? errorMessage, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _completionCalls) == 1)
            {
                await ReleaseFirstCompletion.WaitAsync(ct);
            }
        }

        public Task ReportExecutionStartedAsync(Guid deploymentId, Guid dispatchId, CancellationToken ct)
        {
            ExecutionStarted.Enqueue((deploymentId, dispatchId));
            return Task.CompletedTask;
        }

        public Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct)
        {
            AdhocResults.Enqueue(result);
            return Task.CompletedTask;
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
        public void OnRunDeployment(Func<DeploymentPlan, Task> handler) { }
        public void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler) { }
        public void OnCancelDeployment(Func<Guid, string?, Task> handler) { }
        public void OnClosed(Func<Exception?, Task> handler) { }
        public void OnReconnected(Func<Task> handler) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Counts invocations; optionally blocks inside the "script" so the
    /// adhoc run holds the machine slot while a test dispatches a deployment.</summary>
    private sealed class SignallingRunner : IAdhocScriptInvoker
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);
        public bool BlockUntilReleased { get; init; }
        public SemaphoreSlim Release { get; } = new(0);

        public async Task<int> InvokeAsync(
            string script, string workingDirectory,
            IReadOnlyDictionary<string, string> envVars,
            Func<string, string, Task> onOutput, CancellationToken ct)
        {
            Interlocked.Increment(ref _invocations);
            if (BlockUntilReleased)
            {
                await Release.WaitAsync(ct);
            }
            return 0;
        }
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
