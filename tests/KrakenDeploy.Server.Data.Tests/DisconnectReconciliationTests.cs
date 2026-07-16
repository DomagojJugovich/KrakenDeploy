using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B3 (T0-3) — a deployment must never strand in Running because an agent went
/// silent. Pre-B3, a wave with the default step config (TimeoutSeconds = 0)
/// awaited its sub-plan TCS with no deadline; the worker's lease renewal kept
/// the B1 reconciler away (the process IS alive), and the in-flight gauge
/// blocked blue-green retirement indefinitely. These tests drive the real
/// <see cref="KrakenDeploy.Server.Transport.DeploymentWorker"/> through the
/// orchestrator harness with agents that hang (stay connected, never report)
/// or vanish (drop the connection mid-wave), against short engine ceilings.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DisconnectReconciliationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // Generous guard so a REGRESSION (renewed unbounded wait) fails the test
    // quickly instead of hanging the suite.
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Hung_agent_hits_the_server_side_wave_deadline_instead_of_waiting_forever()
    {
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            MaxTargetWaveDuration = TimeSpan.FromSeconds(1),
            // Isolate the deadline path — the agent stays connected anyway.
            AgentDisconnectWaveGrace = TimeSpan.Zero,
        });
        var (deploymentId, targets) = await SeedAsync(harness, ["deploy"], ["t1"]);
        harness.ConnectFakeAgent(targets[0]).NeverReport = true;

        // Mirror the production TrackedDispatchAsync gauge wrapper: the gauge
        // must return to 0 once the deadline unhangs the dispatch — this is
        // exactly what unblocks ReleaseDrainDecision.ShouldRetire (blue-green).
        using (harness.Gauge.Track())
        {
            await harness.RunDeploymentAsync(deploymentId)
                .WaitAsync(TestGuard);
        }
        harness.Gauge.Count.Should().Be(0,
            "a silent agent must not hold the in-flight gauge (and blue-green drain) hostage");

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed);
        deployment.CompletedUtc.Should().NotBeNull();

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle().Which.Outcome.Should().Be(StepOutcomeKind.TimedOut);
        outcomes[0].ErrorMessage.Should().Contain("server-side maximum duration",
            "the ceiling-based timeout must be distinguishable from a configured step timeout");
    }

    [Fact]
    public async Task Explicit_step_timeout_is_honoured_and_reported_as_a_step_timeout()
    {
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            // Ceiling much larger than the explicit step timeout — the step's
            // own value must win and produce the classic timeout message.
            MaxTargetWaveDuration = TimeSpan.FromHours(1),
            AgentDisconnectWaveGrace = TimeSpan.Zero,
        });
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0",
            new StepBuilder { Name = "deploy", TimeoutSeconds = 1 });
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]).NeverReport = true;

        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle().Which.Outcome.Should().Be(StepOutcomeKind.TimedOut);
        outcomes[0].ErrorMessage.Should().Contain("timed out after 1s");
    }

    [Fact]
    public async Task Vanished_agent_wave_is_cancelled_after_the_disconnect_grace()
    {
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            // Deadline deliberately huge: only the disconnect monitor can
            // unhang this wave within the test guard.
            MaxTargetWaveDuration = TimeSpan.FromHours(1),
            AgentDisconnectWaveGrace = TimeSpan.FromMilliseconds(500),
        });
        var (deploymentId, targets) = await SeedAsync(harness, ["deploy"], ["t1"]);
        harness.ConnectFakeAgent(targets[0]).VanishBeforeReporting = true;

        using (harness.Gauge.Track())
        {
            await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);
        }
        harness.Gauge.Count.Should().Be(0);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed);

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle().Which.Outcome.Should().Be(StepOutcomeKind.Failed,
            "a disconnect is a failure, not a timeout — the agent is gone, not slow");
        outcomes[0].ErrorMessage.Should().Contain("disconnected mid-wave");
    }

    [Fact]
    public async Task BestEffort_survivors_continue_when_one_agent_vanishes_mid_wave()
    {
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            AgentDisconnectWaveGrace = TimeSpan.FromMilliseconds(300),
        });
        var (deploymentId, targets) = await SeedAsync(
            harness, ["deploy"], ["t1", "t2"], DeploymentFailureMode.BestEffort);
        harness.ConnectFakeAgent(targets[0]).VanishBeforeReporting = true;
        harness.ConnectFakeAgent(targets[1]); // healthy

        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.SucceededWithWarnings,
            "BestEffort drops the vanished target and lets the survivor finish");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Single(o => o.TargetId == targets[0].Id).Outcome
            .Should().Be(StepOutcomeKind.Failed);
        outcomes.Single(o => o.TargetId == targets[1].Id).Outcome
            .Should().Be(StepOutcomeKind.Succeeded);
    }

    [Fact]
    public async Task Atomic_mode_fails_the_whole_deployment_when_one_agent_vanishes()
    {
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            AgentDisconnectWaveGrace = TimeSpan.FromMilliseconds(300),
        });
        var (deploymentId, targets) = await SeedAsync(
            harness, ["deploy"], ["t1", "t2"], DeploymentFailureMode.Atomic);
        harness.ConnectFakeAgent(targets[0]).VanishBeforeReporting = true;
        harness.ConnectFakeAgent(targets[1]);

        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed,
            "Atomic mode: one target's disconnect fails the deployment farm-wide");
    }

    [Fact]
    public async Task Wave_retries_are_abandoned_when_the_agent_stays_offline()
    {
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            MaxTargetWaveDuration = TimeSpan.FromHours(1),
            AgentDisconnectWaveGrace = TimeSpan.FromMilliseconds(300),
        });
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        // MaxRetries=3 would pre-B3 burn FOUR full deadline windows dispatching
        // into the dead connection id; the per-attempt connection refresh must
        // abandon the retries after the first disconnect-cancel instead.
        var release = await harness.SeedReleaseAsync(project.Id, "1.0",
            new StepBuilder { Name = "deploy", MaxRetries = 3 });
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]).VanishBeforeReporting = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);
        sw.Stop();

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed);

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle().Which.ErrorMessage
            .Should().Contain("retries abandoned");

        // One grace window (+ slack), NOT four: retries must not re-dispatch
        // into the void.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<(Guid DeploymentId, List<DeploymentTarget> Targets)> SeedAsync(
        OrchestratorTestHarness harness,
        string[] stepNames,
        string[] targetNames,
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort)
    {
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync(targetNames);
        var steps = stepNames.Select(n => StepBuilder.Script(n)).ToArray();
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", steps);
        var deploymentId = await harness.CreateDeploymentAsync(
            release.Id, env.Id, targets, failureMode);
        return (deploymentId, targets);
    }
}
