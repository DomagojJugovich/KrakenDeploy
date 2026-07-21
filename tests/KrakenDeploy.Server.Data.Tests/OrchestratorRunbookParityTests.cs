using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// D1 engine merge — runbook runs now execute through the SAME orchestrator
/// (<see cref="KrakenDeploy.Server.Transport.DeploymentWorker"/>) as deployments,
/// gaining waves, multi-target fan-out, server-side steps, the M14 step knobs and
/// the failure modes that the degraded single-target RunbookRunWorker never had.
/// These tests drive a <c>RunbookRun</c> id through the harness's dispatch seam
/// (<see cref="OrchestratorTestHarness.RunDeploymentAsync"/>, which kind-branches
/// on the loaded task) and assert the parity behaviours against the shared
/// spine — the same state the deployment E2E suite asserts, plus the
/// runbook-specific forks (RunOnServer-on-server, RunbookRun.* audit vocabulary).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OrchestratorRunbookParityTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // ── Multi-target fan-out (was single-target only) ───────────────────────

    [Fact]
    public async Task Runbook_run_fans_out_across_targets_one_outcome_per_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rbp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rbe-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1", "t2", "t3");
        var runId = await harness.CreateRunbookRunAsync(
            project.Id, env.Id, targets, [StepBuilder.Script("deploy")]);
        foreach (var t in targets) { harness.ConnectFakeAgent(t); }

        await harness.RunDeploymentAsync(runId);

        var run = await harness.GetServerTaskAsync(runId);
        run.Kind.Should().Be(ServerTaskKind.RunbookRun);
        run.Status.Should().Be(DeploymentStatus.Succeeded,
            "the degraded worker dispatched to a SINGLE target; the unified orchestrator fans out");

        var outcomes = await harness.GetOutcomesAsync(runId);
        outcomes.Should().HaveCount(3, "one outcome row per (step, target)");
        outcomes.Select(o => o.TargetId).Should().BeEquivalentTo(targets.Select(t => (Guid?)t.Id));
        outcomes.Should().AllSatisfy(o => o.Outcome.Should().Be(StepOutcomeKind.Succeeded));
    }

    // ── RunOnServer executes on the SERVER, not the target (SECURITY fix) ────

    [Fact]
    public async Task Runbook_run_on_server_step_executes_server_side_not_on_the_target()
    {
        // SECURITY: pre-D1 a RunOnServer runbook step ran ON THE TARGET because the
        // partitioner never ran for runbook runs. Post-merge the partitioner
        // classifies it into a SERVER wave that runs in-process on the orchestrator;
        // the target agent must never receive it. Target step first (wave 1, reaches
        // the agent), server step second (wave 2, runs in-process). The server step
        // is non-required so the security invariants hold regardless of whether the
        // in-process script itself succeeds.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rbp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rbe-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        var runId = await harness.CreateRunbookRunAsync(project.Id, env.Id, targets,
            [StepBuilder.Script("target-step"), StepBuilder.ServerScript("server-step", required: false)]);
        var agent = harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(runId);

        // The server step ran server-side — its outcome is flagged IsServerSide and
        // the agent NEVER saw it (only the target-side step reached the agent).
        var outcomes = await harness.GetOutcomesAsync(runId);
        var serverOutcome = outcomes.Should().ContainSingle(o => o.StepName == "server-step").Subject;
        serverOutcome.IsServerSide.Should().BeTrue(
            "the partitioner classified the RunOnServer step server-side (the D1 security fix)");

        var stepNamesSeenByAgent = agent.ReceivedPlans
            .SelectMany(p => p.Steps.Select(s => s.Name))
            .ToList();
        stepNamesSeenByAgent.Should().NotContain("server-step",
            "a RunOnServer step must NOT be dispatched to the target agent");
        stepNamesSeenByAgent.Should().Contain("target-step",
            "the target-side step (wave 1) reaches the agent before the server wave runs");
    }

    // ── M14 run-condition knob honoured (dead for online runs pre-D1) ───────

    [Fact]
    public async Task Runbook_success_conditioned_step_skips_after_a_non_required_failure()
    {
        // Pre-D1 online runbook runs ran with orchestrateSteps:false — no condition
        // evaluation, so Condition/Required were dead. Post-merge the server drives
        // StepConditionEvaluator per wave: a non-required failure soft-fails the
        // target, so its later Condition=Success step SKIPS and the run terminates
        // SucceededWithWarnings (a state runbook runs could not reach before).
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rbp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rbe-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        var runId = await harness.CreateRunbookRunAsync(project.Id, env.Id, targets,
            [StepBuilder.Script("flaky", required: false), StepBuilder.Script("on-success")]);
        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.StepResponses["flaky"] = FakeStepResponse.Fail("non-required blip");

        await harness.RunDeploymentAsync(runId);

        var run = await harness.GetServerTaskAsync(runId);
        run.Status.Should().Be(DeploymentStatus.SucceededWithWarnings);

        var outcomes = await harness.GetOutcomesAsync(runId);
        outcomes.Single(o => o.StepName == "flaky").Outcome.Should().Be(StepOutcomeKind.Failed);
        outcomes.Single(o => o.StepName == "on-success").Outcome.Should().Be(StepOutcomeKind.Skipped,
            "a Condition=Success step skips once its target has soft-failed");
    }

    // ── Additive RunbookRun.* audit vocabulary (never Deployment.*) ──────────

    [Fact]
    public async Task Runbook_required_failure_emits_RunbookRun_audit_vocabulary()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rbp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rbe-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        var runId = await harness.CreateRunbookRunAsync(project.Id, env.Id, targets,
            [StepBuilder.Script("boom")]);
        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.StepResponses["boom"] = FakeStepResponse.Fail("required step exploded");

        await harness.RunDeploymentAsync(runId);

        var run = await harness.GetServerTaskAsync(runId);
        run.Status.Should().Be(DeploymentStatus.Failed,
            "the only target dropped on a Required step failure");

        var events = await harness.GetAuditEventTypesAsync(runId);
        events.Should().Contain(AuditEventType.RunbookRunRequiredStepFailed,
            "a runbook run emits RunbookRun.* orchestration events");
        events.Should().Contain(AuditEventType.RunbookRunTargetDropped);
        events.Should().NotContain(e => e.StartsWith("Deployment.", StringComparison.Ordinal),
            "the additive vocabulary must never leak Deployment.* names into a runbook run's " +
            "audit trail (a Deployment.* wildcard subscription would wrongly fire)");
    }

    // ── Cancel is observed at the wave boundary (parity with deployments) ────

    [Fact]
    public async Task Runbook_cancel_between_waves_stops_before_the_next_wave()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rbp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rbe-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        // Two steps → two waves (StartAfterPrevious default). Cancel lands after wave 1.
        var runId = await harness.CreateRunbookRunAsync(project.Id, env.Id, targets,
            [StepBuilder.Script("wave1"), StepBuilder.Script("wave2")]);
        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.AfterWaveAsync = async waveCount =>
        {
            if (waveCount != 1) { return; }
            // Simulate an operator cancel landing while wave 1 was in flight: the
            // guarded terminal write flips the run to Cancelled. The worker's
            // next-wave ownership check (IsTaskStillRunningAsync, kind-agnostic)
            // must observe it and stop before dispatching wave 2.
            await using var db = harness.CreateContext();
            await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == runId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.Cancelled)
                    .SetProperty(t => t.CompletedUtc, DateTimeOffset.UtcNow));
        };

        await harness.RunDeploymentAsync(runId);

        var run = await harness.GetServerTaskAsync(runId);
        run.Status.Should().Be(DeploymentStatus.Cancelled,
            "the recorded Cancelled verdict must stand — the worker never overwrites it");

        agent.ReceivedPlans.Should().ContainSingle(
            "only wave 1 dispatched; the worker halted at the wave boundary before wave 2");
        var outcomes = await harness.GetOutcomesAsync(runId);
        outcomes.Should().NotContain(o => o.StepName == "wave2",
            "wave 2 never dispatched, so no outcome row lands for it");
    }

    // ── DeployRelease server step inside a runbook run (review fix #1) ───────

    [Fact]
    public async Task Runbook_run_with_a_DeployRelease_step_creates_and_awaits_the_child_deployment()
    {
        // Regression for the review's CONFIRMED bug: DeployReleaseStepRunner loaded
        // its executing parent from db.Deployments (TPH Deployment-only), so a
        // runbook run's Octopus.DeployRelease step failed 'Parent task row vanished'
        // — DeployRelease was advertised for runbook runs but never worked. The fix
        // loads the parent via db.ServerTasks.
        await using var harness = new OrchestratorTestHarness(postgres);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var env = await harness.SeedEnvironmentAsync($"rbdr-env-{tag}");
        var childProjectId = await harness.SeedChildProjectWithReleaseAsync(
            $"rbdr-child-{tag}", StepBuilder.Script("child-step"));
        var parentProject = await harness.SeedProjectAsync($"rbdr-parent-{tag}");
        var target = (await harness.SeedTargetsAsync($"rbdr-t-{tag}"))[0];
        harness.ConnectFakeAgent(target); // serves the CHILD's target wave

        var runId = await harness.CreateRunbookRunAsync(
            parentProject.Id, env.Id, [target],
            [StepBuilder.DeployRelease("deploy-child", childProjectId)]);

        // Run through the real gate-aware loop so the child dispatches (E3 path).
        await harness.StartWorkerAsync();
        await harness.EnqueueAsync(runId);

        var run = await harness.WaitForServerTaskTerminalAsync(runId, TimeSpan.FromSeconds(30));
        run.Kind.Should().Be(ServerTaskKind.RunbookRun);
        run.Status.Should().Be(DeploymentStatus.Succeeded,
            "the runbook's DeployRelease server step loaded its parent via db.ServerTasks and its " +
            "child deployment ran to success (pre-fix the step failed 'Parent task row vanished')");

        await using var db = harness.CreateContext();
        var child = await db.Deployments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.ParentTaskId == runId);
        child.Should().NotBeNull(
            "the DeployRelease step created a child DEPLOYMENT parented to the runbook run");
    }
}
