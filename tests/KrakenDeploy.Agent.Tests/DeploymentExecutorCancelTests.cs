using System.Collections.Concurrent;
using FluentAssertions;
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
/// B6 — the executor's single-flight registry: TryCancel aborts the in-flight
/// attempt and reports a failed completion; a re-delivered copy of the SAME
/// attempt is ignored; a NEWER attempt supersedes (cancels + awaits) the old
/// one. The gate link holds the FIRST success-path completion in flight
/// (honoring the run's token), so tests can act while a task is registered.
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

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DeploymentExecutor BuildExecutor(GateLink link) => new(
        link,
        new NullPackageSource(),
        new NullArtifactSink(),
        new StepPackageLoader(
            new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
        Options.Create(new AgentConfig()),
        NullLogger<DeploymentExecutor>.Instance);

    private static DeploymentPlan Plan(Guid taskId, Guid dispatchId) => new(
        DeploymentId: taskId,
        EnvironmentName: "test",
        Steps: [],
        Variables: new Dictionary<string, string>(),
        ArrayVariables: new Dictionary<string, string[]>(),
        DispatchId: dispatchId);

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
