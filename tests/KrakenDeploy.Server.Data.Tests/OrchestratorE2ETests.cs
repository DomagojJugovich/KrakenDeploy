using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// End-to-end orchestrator tests covering the M-RollingDeployments
/// Phase 1b / 2 / 3 behaviours that previously had zero direct coverage.
/// Each test seeds a minimum-viable Project + Environment + Release + N
/// Targets + fake agents, runs <see cref="DeploymentWorker"/>'s dispatch
/// path through the harness, and asserts against the persisted state
/// (<see cref="Deployment.Status"/> + <see cref="DeploymentStepOutcome"/>
/// rows). The harness's fake hub resolves per-target sub-plans
/// synchronously so there are no Task.Delay sleeps — each test runs
/// in &lt;100 ms barring the Postgres roundtrip.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OrchestratorE2ETests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // ── Single-target regression baseline ───────────────────────────────────

    [Fact]
    public async Task Single_target_deployment_with_all_steps_succeeding_terminates_Succeeded()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness,
            stepNames: ["s1", "s2"],
            targetNames: ["t1"]);
        harness.ConnectFakeAgent(targets[0]); // all steps succeed by default

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Succeeded);

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().HaveCount(2);
        outcomes.Should().AllSatisfy(o =>
        {
            o.Outcome.Should().Be(StepOutcomeKind.Succeeded);
            o.TargetId.Should().Be(targets[0].Id,
                because: "Phase 1a widened the outcome key to include TargetId; " +
                          "every target-side outcome should carry the target it ran on");
        });
    }

    // ── Multi-target happy path (Phase 1b) ──────────────────────────────────

    [Fact]
    public async Task Multi_target_fan_out_records_one_outcome_per_target_per_step()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness,
            stepNames: ["deploy"],
            targetNames: ["t1", "t2", "t3"]);
        foreach (var t in targets) { harness.ConnectFakeAgent(t); }

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Succeeded);

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().HaveCount(3,
            because: "one DeploymentStepOutcome row per (step, target) — Phase 1a key shape");
        outcomes.Select(o => o.TargetId).Should().BeEquivalentTo(
            targets.Select(t => (Guid?)t.Id));
        outcomes.Should().AllSatisfy(o => o.Outcome.Should().Be(StepOutcomeKind.Succeeded));
    }

    // ── Phase 3 — per-target Required gate drop-out ─────────────────────────

    [Fact]
    public async Task Required_failure_on_one_target_drops_it_and_lets_others_continue()
    {
        // The headline Phase 3 behaviour: a Required step failure on target B
        // removes B from subsequent waves but A + C complete normally. The
        // deployment terminates as SucceededWithWarnings (partial success
        // visible without scraping audit rows).
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness,
            stepNames: ["smoke", "deploy"],
            targetNames: ["A", "B", "C"]);

        harness.ConnectFakeAgent(targets[0]);
        var agentB = harness.ConnectFakeAgent(targets[1]);
        agentB.StepResponses["smoke"] = FakeStepResponse.Fail("smoke test refused");
        harness.ConnectFakeAgent(targets[2]);

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.SucceededWithWarnings,
            because: "B dropped out on a Required step failure but A + C completed cleanly — " +
                      "partial success terminates as SucceededWithWarnings");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);

        // A + C: both steps Succeeded
        outcomes.Where(o => o.TargetId == targets[0].Id)
            .Should().HaveCount(2).And.OnlyContain(o => o.Outcome == StepOutcomeKind.Succeeded);
        outcomes.Where(o => o.TargetId == targets[2].Id)
            .Should().HaveCount(2).And.OnlyContain(o => o.Outcome == StepOutcomeKind.Succeeded);

        // B: smoke Failed, deploy never recorded (B was already dropped from
        // the survivor set before the second wave dispatched).
        var bOutcomes = outcomes.Where(o => o.TargetId == targets[1].Id).ToList();
        bOutcomes.Should().HaveCount(1,
            because: "Phase 3 — once B is dropped, the second wave does not " +
                      "dispatch to it, so no second outcome row lands");
        bOutcomes[0].StepName.Should().Be("smoke");
        bOutcomes[0].Outcome.Should().Be(StepOutcomeKind.Failed);
    }

    [Fact]
    public async Task All_targets_dropping_fails_the_deployment()
    {
        // When every target Required-fails, aliveTargets goes empty and the
        // orchestrator fails the deployment (no progress possible). The
        // legacy Deployment.Failed status is preserved so existing
        // dashboards keying on it still light up.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness,
            stepNames: ["deploy"],
            targetNames: ["A", "B"]);

        foreach (var t in targets)
        {
            var agent = harness.ConnectFakeAgent(t);
            agent.DefaultResponse = FakeStepResponse.Fail("deploy refused");
        }

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed);

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().HaveCount(2);
        outcomes.Should().OnlyContain(o => o.Outcome == StepOutcomeKind.Failed);
    }

    // ── M11.C — failed started deployment enqueues a diagnosis ──────────────

    [Fact]
    public async Task Failed_started_deployment_enqueues_an_AI_diagnosis()
    {
        // FailAsync writes the deployment id to the diagnosis channel only
        // for deployments that actually started (StartedUtc set when the
        // orchestrator transitions to Running). A started→failed run should
        // enqueue exactly one diagnosis request.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness,
            stepNames: ["deploy"],
            targetNames: ["A"]);
        harness.ConnectFakeAgent(targets[0]).DefaultResponse = FakeStepResponse.Fail("boom");

        await harness.RunDeploymentAsync(deploymentId);

        (await harness.GetDeploymentAsync(deploymentId)).Status.Should().Be(DeploymentStatus.Failed);
        harness.DiagnosisChannel.Reader.TryRead(out var enqueued).Should().BeTrue(
            because: "a started deployment that failed should queue an AI diagnosis");
        enqueued.Should().Be(deploymentId);
    }

    // ── Phase 1b — non-required failure → SucceededWithWarnings ─────────────

    [Fact]
    public async Task Non_required_failure_terminates_deployment_as_SucceededWithWarnings()
    {
        // A non-Required failure flips hasFailed but doesn't abort. With
        // only one step (the failing one) the deployment finishes after it.
        // Following steps with the default Condition=Success would Skip
        // post-failure (M14.2 semantic) — that's a separate observation
        // covered by the unit-level StepConditionEvaluator tests; the
        // contract this E2E test pins is the terminal-status mapping.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness,
            stepNames: ["optional-smoke"],
            targetNames: ["t1"],
            requiredByStepName: new() { ["optional-smoke"] = false });

        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.StepResponses["optional-smoke"] = FakeStepResponse.Fail("flaky");

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.SucceededWithWarnings,
            because: "non-Required failure flips hasFailed → terminal status is " +
                      "SucceededWithWarnings, not Failed (which the Required-failure " +
                      "path produces) and not Succeeded (which a clean run produces)");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle();
        outcomes[0].Outcome.Should().Be(StepOutcomeKind.Failed);
        outcomes[0].Required.Should().BeFalse();
    }

    // ── Phase 2 — rolling window batches the fan-out ────────────────────────

    [Fact]
    public async Task Rolling_window_batches_targets_when_MaxParallelism_is_below_count()
    {
        // 4 targets + MaxParallelism=2 → 2 batches of 2. All succeed, but
        // the per-target step outcomes prove the dispatch reached every
        // target (the batching itself is observed via audit rows; this
        // test pins the end-state to keep the harness narrow).
        await using var harness = new OrchestratorTestHarness(postgres);

        var project = await harness.SeedProjectAsync("rolling-proj");
        var env = await harness.SeedEnvironmentAsync("rolling-env");
        var targets = await harness.SeedTargetsAsync("t1", "t2", "t3", "t4");

        // Step group with MaxParallelism=2 wrapping a single child script step.
        // The child's wave inherits the rolling cap via ParentStepId chain
        // through RollingWindowResolver.
        var group = StepBuilder.StepGroup("rolling-group", maxParallelism: 2);
        var child = StepBuilder.Script("deploy").InGroup(group.Id);

        var release = await harness.SeedReleaseAsync(project.Id, "1.0", group, child);
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        foreach (var t in targets) { harness.ConnectFakeAgent(t); }

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Succeeded,
            because: "all 4 targets succeeded in their respective batches");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().HaveCount(4,
            because: "MaxParallelism caps concurrency, not coverage — all 4 targets " +
                      "still run the wave (just in 2 batches of 2)");
        outcomes.Select(o => o.TargetId).Should().BeEquivalentTo(
            targets.Select(t => (Guid?)t.Id));
    }

    [Fact]
    public async Task Rolling_window_with_Required_failure_in_batch_one_keeps_running_other_batches()
    {
        // Phase 2 originally stopped subsequent batches on first Required
        // failure (canary-ish gate). Phase 3 removed that gate — each
        // target's failure drops only that target; batch 2 still runs.
        // This test pins the Phase 3 behaviour (the failing target drops,
        // the survivors complete).
        await using var harness = new OrchestratorTestHarness(postgres);

        var project = await harness.SeedProjectAsync("rolling-fail-proj");
        var env = await harness.SeedEnvironmentAsync("rolling-fail-env");
        var targets = await harness.SeedTargetsAsync("a1", "a2", "b1", "b2");

        var group = StepBuilder.StepGroup("rolling-group", maxParallelism: 2);
        var child = StepBuilder.Script("deploy").InGroup(group.Id);
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", group, child);
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        // a2 in batch 1 Required-fails; the other 3 succeed.
        harness.ConnectFakeAgent(targets[0]);
        var agentA2 = harness.ConnectFakeAgent(targets[1]);
        agentA2.DefaultResponse = FakeStepResponse.Fail("a2 refused");
        harness.ConnectFakeAgent(targets[2]);
        harness.ConnectFakeAgent(targets[3]);

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.SucceededWithWarnings,
            because: "a2 dropped, others succeeded — partial success");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Where(o => o.Outcome == StepOutcomeKind.Succeeded)
            .Select(o => o.TargetId)
            .Should().BeEquivalentTo(new Guid?[]
            {
                targets[0].Id, targets[2].Id, targets[3].Id,
            }, "Phase 3 — batch 2 still runs after batch 1's drop-out");

        outcomes.Where(o => o.Outcome == StepOutcomeKind.Failed)
            .Should().ContainSingle()
            .Which.TargetId.Should().Be(targets[1].Id);
    }

    // ── Per-target outcome attribution (Phase 1a key shape) ─────────────────

    [Fact]
    public async Task Per_target_outcomes_carry_correct_TargetId_in_three_target_run()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness,
            stepNames: ["s1"],
            targetNames: ["alpha", "beta", "gamma"]);
        foreach (var t in targets) { harness.ConnectFakeAgent(t); }

        await harness.RunDeploymentAsync(deploymentId);

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        var byTarget = outcomes.ToLookup(o => o.TargetId);
        foreach (var t in targets)
        {
            byTarget[t.Id].Should().ContainSingle()
                .Which.StepName.Should().Be("s1",
                    because: "the Phase 1a widened unique key " +
                              "(DeploymentId, StepIndex, TargetId) yields one row per target");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<(Guid DeploymentId, List<KrakenDeploy.Server.Core.Domain.Targets.DeploymentTarget> Targets)>
        SeedAsync(
            OrchestratorTestHarness harness,
            string[] stepNames,
            string[] targetNames,
            Dictionary<string, bool>? requiredByStepName = null)
    {
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync(targetNames);
        var steps = stepNames
            .Select(n =>
            {
                var required = requiredByStepName is null || !requiredByStepName.TryGetValue(n, out var r) || r;
                return StepBuilder.Script(n, required);
            })
            .ToArray();
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", steps);
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        return (deploymentId, targets);
    }
}
