using System.Diagnostics;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// F2 — the wave deadline arms when the agent reports it ACQUIRED its machine
/// execution slot, not at dispatch.
/// <para>
/// Pre-F2 the whole budget was armed at dispatch, so a sub-plan queued behind
/// another task on the same machine burned its deadline while waiting — an
/// operator's 30 s step timeout blew up purely because the box was busy. The
/// dispatch-time arm is now a BACKSTOP (execution budget +
/// <see cref="EngineOptions.MaxTargetQueueWait"/>), which keeps B3's "the wave
/// deadline is always armed" invariant true for an agent that never reports at all.
/// </para>
/// <para>
/// These tests drive the real <see cref="KrakenDeploy.Server.Transport.DeploymentWorker"/>
/// through the orchestrator harness. The fake agent models a contract-v2 agent:
/// it reports execution start before its first step, can queue first, or can be
/// wedged and never report at all.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class WaveDeadlineArmingTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    /// <summary>Generous guard so a REGRESSION fails fast instead of hanging the
    /// suite (the dispatch backstop in these tests is minutes, not the 2 h default).</summary>
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Queued_sub_plan_does_not_burn_its_wave_deadline_while_waiting()
    {
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            // Execution budget far SHORTER than the queue wait: pre-F2 this 2 s
            // clock started at dispatch and the wave died while still queued.
            MaxTargetWaveDuration    = TimeSpan.FromSeconds(2),
            MaxTargetQueueWait       = TimeSpan.FromSeconds(45),
            AgentDisconnectWaveGrace = TimeSpan.Zero,   // isolate the deadline path
        });
        var (deploymentId, targets) = await SeedAsync(harness, [StepBuilder.Script("deploy")], ["t1"]);
        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.QueueBeforeExecuting = TimeSpan.FromSeconds(5);

        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Succeeded,
            "the 2 s execution budget must start at gate acquisition, so 5 s of " +
            "queueing behind another task on the machine cannot consume it");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle().Which.Outcome.Should().Be(StepOutcomeKind.Succeeded);
    }

    [Fact]
    public async Task Explicit_step_timeout_is_measured_from_gate_acquisition()
    {
        // Same defect, operator-visible variant: an explicit per-step TimeoutSeconds
        // must bound EXECUTION, not execution-plus-queueing.
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            MaxTargetWaveDuration    = TimeSpan.FromHours(1),
            MaxTargetQueueWait       = TimeSpan.FromSeconds(45),
            AgentDisconnectWaveGrace = TimeSpan.Zero,
        });
        var (deploymentId, targets) = await SeedAsync(
            harness, [new StepBuilder { Name = "deploy", TimeoutSeconds = 2 }], ["t1"]);

        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.QueueBeforeExecuting = TimeSpan.FromSeconds(5);

        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);

        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Succeeded,
                "a 2 s step timeout must not be spent waiting for a busy machine");
    }

    [Fact]
    public async Task Hung_agent_that_started_executing_is_reaped_by_the_execution_budget()
    {
        // The re-arm must TIGHTEN, not just relax: an agent that took the slot and
        // then hung must die on the 1 s execution budget, NOT wait out the (much
        // larger) dispatch backstop.
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            MaxTargetWaveDuration    = TimeSpan.FromSeconds(1),
            MaxTargetQueueWait       = TimeSpan.FromSeconds(45),   // backstop ≈ 46 s
            AgentDisconnectWaveGrace = TimeSpan.Zero,
        });
        var (deploymentId, targets) = await SeedAsync(harness, [StepBuilder.Script("deploy")], ["t1"]);
        // NeverReport models a hung SCRIPT: it acquires the slot (reports execution
        // start) and then stalls.
        harness.ConnectFakeAgent(targets[0]).NeverReport = true;

        var sw = Stopwatch.StartNew();
        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);
        sw.Stop();

        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Failed);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
            "the deadline was re-armed to the 1 s execution budget, so the wave must " +
            "not wait out the ~46 s dispatch backstop");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle().Which.Outcome.Should().Be(StepOutcomeKind.TimedOut);
        outcomes[0].ErrorMessage.Should().Contain("server-side maximum duration");
    }

    [Fact]
    public async Task Agent_that_never_acquires_its_machine_slot_hits_the_dispatch_backstop()
    {
        // B3's invariant survives F2: an agent that stays CONNECTED but never
        // acquires its machine slot (wedged behind a non-cooperative predecessor)
        // never sends the execution-started report, so only the dispatch-time
        // backstop can reap the wave.
        await using var harness = new OrchestratorTestHarness(postgres, new EngineOptions
        {
            MaxTargetWaveDuration    = TimeSpan.FromSeconds(1),
            MaxTargetQueueWait       = TimeSpan.FromSeconds(2),   // backstop ≈ 3 s
            AgentDisconnectWaveGrace = TimeSpan.Zero,
        });
        var (deploymentId, targets) = await SeedAsync(harness, [StepBuilder.Script("deploy")], ["t1"]);
        harness.ConnectFakeAgent(targets[0]).NeverAcquireMachineSlot = true;

        using (harness.Gauge.Track())
        {
            await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);
        }
        harness.Gauge.Count.Should().Be(0,
            "a wedged agent must not hold the in-flight gauge (and blue-green drain) hostage");

        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Failed);

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle().Which.Outcome.Should().Be(StepOutcomeKind.TimedOut);
        outcomes[0].ErrorMessage.Should().Contain("never started executing",
            "the operator fix differs from a slow step — this box is busy or wedged");
    }

    // ── The per-target flag reaches the agent ───────────────────────────────

    [Fact]
    public async Task Target_flag_is_stamped_into_the_dispatched_plan()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("serial", "parallel");
        await harness.SetAllowParallelTaskExecutionAsync(targets[1].Id, true);

        var release = await harness.SeedReleaseAsync(project.Id, "1.0",
            StepBuilder.Script("deploy"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        var serialAgent = harness.ConnectFakeAgent(targets[0]);
        var parallelAgent = harness.ConnectFakeAgent(targets[1]);

        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TestGuard);

        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Succeeded);

        serialAgent.ReceivedPlans.Should().ContainSingle()
            .Which.AllowParallelTaskExecution.Should().BeFalse();
        parallelAgent.ReceivedPlans.Should().ContainSingle()
            .Which.AllowParallelTaskExecution.Should().BeTrue(
                "the flag is per target, resolved at plan-build time");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Seeds project + environment + targets + a one-release process and
    /// returns the queued deployment. Takes <see cref="StepBuilder"/>s rather than
    /// step NAMES so tests needing an explicit per-step timeout use the same helper
    /// instead of re-inlining the five-call sequence.</summary>
    private static async Task<(Guid DeploymentId, List<DeploymentTarget> Targets)> SeedAsync(
        OrchestratorTestHarness harness,
        StepBuilder[] steps,
        string[] targetNames,
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort)
    {
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync(targetNames);
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", steps);
        var deploymentId = await harness.CreateDeploymentAsync(
            release.Id, env.Id, targets, failureMode);
        return (deploymentId, targets);
    }
}
