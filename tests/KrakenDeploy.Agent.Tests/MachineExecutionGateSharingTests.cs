using System.Collections.Concurrent;
using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Agent.Adhoc;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// F2 — ad-hoc scripts share the deployment path's machine execution slot.
/// <para>
/// Pre-F2 the gate lived inside <see cref="DeploymentExecutor"/> and ad-hoc
/// scripts bypassed it entirely (an audited defect): an operator-approved
/// diagnostic script could run straight into a deployment's file / IIS / service
/// operations on the same box. The slot now lives in the shared
/// <see cref="MachineExecutionGate"/> singleton, and these tests wire BOTH
/// executors from one instance exactly as <c>Program.cs</c> and
/// <c>ServerLinkHostedService</c> do.
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
    public async Task Adhoc_script_bypasses_the_gate_when_the_target_allows_parallel_execution()
    {
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

        await adhoc.HandleAsync(Command(Guid.NewGuid(), priv, allowParallel: true))
            .WaitAsync(TestTimeout);

        runner.Invocations.Should().Be(1,
            "the target opted into parallel task execution, so the script does not queue");
        link.AdhocResults.Should().ContainSingle().Which.AgentError.Should().BeNull();

        link.ReleaseFirstCompletion.Release();
        await deployTask.WaitAsync(TestTimeout);
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
