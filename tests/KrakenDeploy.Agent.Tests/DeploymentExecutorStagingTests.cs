using FluentAssertions;
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
/// E8 — the staging directory model: per-step dirs are keyed by DispatchId (so a
/// superseding re-dispatch can't share the old attempt's dir), the whole staging
/// root is swept at boot, and a task's staging subtree is swept when its run ends.
/// </summary>
public sealed class DeploymentExecutorStagingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void StagingStepDir_isolates_attempts_by_dispatch_id()
    {
        var dataPath = "/data";
        var deploymentId = Guid.NewGuid();
        var attemptA = Guid.NewGuid();
        var attemptB = Guid.NewGuid();

        var dirA = DeploymentExecutor.StagingStepDir(dataPath, deploymentId, attemptA, 0);
        var dirB = DeploymentExecutor.StagingStepDir(dataPath, deploymentId, attemptB, 0);

        // Both carry the deployment id AND their own dispatch id — same task, same
        // step, different attempt ⇒ different dir (the E8 fix).
        dirA.Should().Contain(deploymentId.ToString("N"))
            .And.Contain(attemptA.ToString("N"));
        dirB.Should().Contain(attemptB.ToString("N"));
        dirA.Should().NotBe(dirB, "two attempts of the same step must not share a staging dir");
    }

    [Fact]
    public void SweepOrphanedStagingOnBoot_wipes_the_whole_staging_root()
    {
        var dataDir = Directory.CreateTempSubdirectory("kraken-agent-staging-boot-");
        try
        {
            var root = DeploymentExecutor.StagingRoot(dataDir.FullName);
            // Two orphan trees left by a previous (crashed) process.
            Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"), "0", "extracted"));
            Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"), "1"));
            Directory.Exists(root).Should().BeTrue();

            var executor = BuildExecutor(new NoopLink(), dataDir.FullName);
            executor.SweepOrphanedStagingOnBoot();

            Directory.Exists(root).Should().BeFalse("boot sweep removes the entire orphaned staging root");
        }
        finally
        {
            TryDelete(dataDir.FullName);
        }
    }

    [Fact]
    public void SweepOrphanedStagingOnBoot_is_a_noop_when_no_staging_exists()
    {
        var dataDir = Directory.CreateTempSubdirectory("kraken-agent-staging-none-");
        try
        {
            var executor = BuildExecutor(new NoopLink(), dataDir.FullName);
            var act = executor.SweepOrphanedStagingOnBoot;
            act.Should().NotThrow("a missing staging root is fine");
        }
        finally
        {
            TryDelete(dataDir.FullName);
        }
    }

    [Fact]
    public async Task ExecuteAsync_finally_sweeps_only_this_attempts_dispatch_subtree()
    {
        var dataDir = Directory.CreateTempSubdirectory("kraken-agent-staging-run-");
        try
        {
            var executor = BuildExecutor(new NoopLink(), dataDir.FullName);
            var taskId = Guid.NewGuid();
            var thisDispatch = Guid.NewGuid();
            var siblingDispatch = Guid.NewGuid();

            // A stray tree under THIS attempt's dispatch subtree (e.g. a hard-killed
            // step per-step cleanup couldn't remove).
            var thisDir = DeploymentExecutor.StagingDispatchDir(dataDir.FullName, taskId, thisDispatch);
            Directory.CreateDirectory(Path.Combine(thisDir, "leftover"));

            // A concurrent superseding attempt's dispatch subtree under the SAME
            // task — the finally must NOT touch it (deleting the shared parent would
            // race and destroy a live sibling attempt's staging).
            var siblingDir = DeploymentExecutor.StagingDispatchDir(dataDir.FullName, taskId, siblingDispatch);
            Directory.CreateDirectory(Path.Combine(siblingDir, "in-flight"));

            // A zero-step plan runs cleanly to completion; its finally sweeps its
            // own dispatch subtree only.
            await executor.ExecuteAsync(Plan(taskId, thisDispatch)).WaitAsync(TestTimeout);

            Directory.Exists(thisDir).Should().BeFalse(
                "the finally sweeps this attempt's own dispatch subtree");
            Directory.Exists(siblingDir).Should().BeTrue(
                "a sibling attempt's dispatch subtree under the same task must survive");
        }
        finally
        {
            TryDelete(dataDir.FullName);
        }
    }

    [Fact]
    public void SweepOrphanedStagingOnBoot_skips_when_lock_holds_a_live_pid()
    {
        var dataDir = Directory.CreateTempSubdirectory("kraken-agent-staging-lock-");
        try
        {
            var root = DeploymentExecutor.StagingRoot(dataDir.FullName);
            Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));

            var lockFile = Path.Combine(dataDir.FullName, "staging.lock");
            File.WriteAllText(lockFile,
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var executor = BuildExecutor(new NoopLink(), dataDir.FullName);
            executor.SweepOrphanedStagingOnBoot();

            Directory.Exists(root).Should().BeTrue(
                "sweep must be skipped when the lock file holds a PID that is still running");
        }
        finally
        {
            TryDelete(dataDir.FullName);
        }
    }

    [Fact]
    public void SweepOrphanedStagingOnBoot_sweeps_when_lock_holds_a_dead_pid()
    {
        var dataDir = Directory.CreateTempSubdirectory("kraken-agent-staging-deadpid-");
        try
        {
            var root = DeploymentExecutor.StagingRoot(dataDir.FullName);
            Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));

            var lockFile = Path.Combine(dataDir.FullName, "staging.lock");
            File.WriteAllText(lockFile, "999999999");

            var executor = BuildExecutor(new NoopLink(), dataDir.FullName);
            executor.SweepOrphanedStagingOnBoot();

            Directory.Exists(root).Should().BeFalse(
                "a dead PID in the lock file must not block the sweep");
            File.Exists(lockFile).Should().BeTrue(
                "the lock file must be rewritten with the current PID after a sweep");
        }
        finally
        {
            TryDelete(dataDir.FullName);
        }
    }

    [Fact]
    public void SweepOrphanedStagingOnBoot_writes_lock_file_after_sweep()
    {
        var dataDir = Directory.CreateTempSubdirectory("kraken-agent-staging-writelock-");
        try
        {
            var executor = BuildExecutor(new NoopLink(), dataDir.FullName);
            executor.SweepOrphanedStagingOnBoot();

            var lockFile = Path.Combine(dataDir.FullName, "staging.lock");
            File.Exists(lockFile).Should().BeTrue();
            int.Parse(File.ReadAllText(lockFile).Trim(), System.Globalization.CultureInfo.InvariantCulture).Should()
                .Be(Environment.ProcessId);
        }
        finally
        {
            TryDelete(dataDir.FullName);
        }
    }

    [Fact]
    public void SweepOrphanedStagingOnBoot_sweeps_when_lock_content_is_corrupt()
    {
        var dataDir = Directory.CreateTempSubdirectory("kraken-agent-staging-corrupt-");
        try
        {
            var root = DeploymentExecutor.StagingRoot(dataDir.FullName);
            Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));

            var lockFile = Path.Combine(dataDir.FullName, "staging.lock");
            File.WriteAllText(lockFile, "not-a-pid");

            var executor = BuildExecutor(new NoopLink(), dataDir.FullName);
            executor.SweepOrphanedStagingOnBoot();

            Directory.Exists(root).Should().BeFalse(
                "a corrupt (non-integer) lock file must not block the sweep");
        }
        finally
        {
            TryDelete(dataDir.FullName);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DeploymentExecutor BuildExecutor(IServerLink link, string dataPath) => new(
        link,
        new NullPackageSource(),
        new NullArtifactSink(),
        new StepPackageLoader(
            new ConfigurationBuilder().Build(), NullLogger<StepPackageLoader>.Instance),
        new MachineExecutionGate(),
        Options.Create(new AgentConfig { DataPath = dataPath }),
        Options.Create(new AgentUpdateConfig()),
        NullLogger<DeploymentExecutor>.Instance);

    private static DeploymentPlan Plan(Guid taskId, Guid dispatchId) => new(
        DeploymentId: taskId,
        EnvironmentName: "test",
        Steps: [],
        Variables: new Dictionary<string, string>(),
        ArrayVariables: new Dictionary<string, string[]>(),
        DispatchId: dispatchId);

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    /// <summary>Records nothing and never blocks — for plans that just run to
    /// completion so the ExecuteAsync teardown path can be observed.</summary>
    private sealed class NoopLink : IServerLink
    {
        public Task CompleteDeploymentAsync(
            Guid deploymentId, Guid dispatchId, bool success, string? errorMessage, CancellationToken ct)
            => Task.CompletedTask;

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
        public Task ReportExecutionStartedAsync(Guid deploymentId, Guid dispatchId, CancellationToken ct) => Task.CompletedTask;
        public void OnRunDeployment(Func<DeploymentPlan, Task> handler) { }
        public void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler) { }
        public void OnCancelDeployment(Func<Guid, string?, Task> handler) { }
        public void OnClosed(Func<Exception?, Task> handler) { }
        public void OnReconnected(Func<Task> handler) { }
        public void OnContractRefused(Action<bool> handler) { }
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
