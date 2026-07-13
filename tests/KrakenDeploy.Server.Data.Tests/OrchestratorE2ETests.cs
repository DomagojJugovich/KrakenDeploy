using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;
using Microsoft.EntityFrameworkCore;

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
        // removes B from subsequent waves but A + C complete normally. This is
        // the default BestEffort failure mode, so the deployment terminates as
        // SucceededWithWarnings (partial success — survivors deployed).
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
            because: "BestEffort: B dropped on a Required step failure but A + C completed — " +
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
    public async Task Atomic_mode_required_failure_fails_deployment_and_taints_survivors()
    {
        // Contrast with the BestEffort test above. In Atomic mode a Required-step
        // failure on B fails the WHOLE deployment and puts every surviving target
        // into the failing state, so the survivor A's LATER Condition=Success step
        // is SKIPPED — the same hook that makes Condition=Failure/Always cleanup
        // run farm-wide (e.g. roll back a half-applied version). Terminal = Failed.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("atomic-proj");
        var env     = await harness.SeedEnvironmentAsync("atomic-env");
        var targets = await harness.SeedTargetsAsync("A", "B");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Script("smoke"),
            StepBuilder.Script("deploy"));
        var deploymentId = await harness.CreateDeploymentAsync(
            release.Id, env.Id, targets, DeploymentFailureMode.Atomic);

        harness.ConnectFakeAgent(targets[0]); // A succeeds
        var agentB = harness.ConnectFakeAgent(targets[1]);
        agentB.StepResponses["smoke"] = FakeStepResponse.Fail("smoke refused"); // B fails required smoke

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed,
            because: "Atomic: a Required-step failure on any target fails the whole deployment");

        var aOutcomes = (await harness.GetOutcomesAsync(deploymentId))
            .Where(o => o.TargetId == targets[0].Id).ToList();
        aOutcomes.Should().Contain(o => o.StepName == "smoke" && o.Outcome == StepOutcomeKind.Succeeded);
        aOutcomes.Should().Contain(o => o.StepName == "deploy" && o.Outcome == StepOutcomeKind.Skipped,
            because: "Atomic mode taints the survivor: A's later Condition=Success step is skipped");
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
        // The channel now carries TenantWorkItem(AccountId, Id); single-instance
        // harness leaves AccountId == Guid.Empty.
        enqueued.Id.Should().Be(deploymentId);
        enqueued.AccountId.Should().Be(Guid.Empty);
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
        // the survivors complete) under the default BestEffort failure mode:
        // partial success → SucceededWithWarnings.
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
            because: "BestEffort: a2 dropped, others succeeded — partial success");

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

    // ── Cross-Space execution regression (multi-Space dispatch fix) ─────────

    // A fixed Space id distinct from WellKnown.DefaultSpaceId (the fixture's
    // ambient context always resolves Default).
    private static readonly Guid NonDefaultSpaceId =
        Guid.Parse("0000ffff-0000-0000-0000-00000000d15a");

    [Fact]
    public async Task Deployment_in_a_non_default_Space_dispatches_and_succeeds()
    {
        // Regression for the latent multi-Space execution bug: the worker scope
        // has no active Space (no HttpContext → DefaultSpaceId), so before the
        // fix the global query filter hid a deployment created in a non-Default
        // Space — DispatchAsync's load returned null ("not found") and the
        // deployment sat Queued forever. The worker now resolves the
        // deployment's Space filter-free and runs the unit of work under
        // ISpaceContext.WithSpace; this proves a non-Default-Space deployment
        // loads, dispatches to its agent, and finalises.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedInSpaceAsync(
            harness, NonDefaultSpaceId,
            stepNames: ["s1", "s2"],
            targetNames: ["t1"]);
        harness.ConnectFakeAgent(targets[0]); // all steps succeed by default

        await harness.RunDeploymentAsync(deploymentId);

        // Assert with IgnoreQueryFilters — the fixture's query helpers run under
        // the Default Space and would themselves filter out this row.
        await using var db = harness.CreateContext();
        var deployment = await db.Deployments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deploymentId);
        deployment.Should().NotBeNull();
        deployment!.Status.Should().Be(DeploymentStatus.Succeeded,
            because: "a non-Default-Space deployment must load + dispatch + finalise " +
                      "now that the worker scopes its unit of work to the deployment's Space");
        deployment.SpaceId.Should().Be(NonDefaultSpaceId);

        var outcomes = await db.TaskStepOutcomes.IgnoreQueryFilters()
            .Where(o => o.TaskId == deploymentId)
            .ToListAsync();
        outcomes.Should().HaveCount(2);
        outcomes.Should().AllSatisfy(o =>
        {
            o.Outcome.Should().Be(StepOutcomeKind.Succeeded);
            o.SpaceId.Should().Be(NonDefaultSpaceId,
                because: "step-outcome rows the worker writes must inherit the " +
                          "deployment's Space, not be mis-stamped DefaultSpaceId");
        });
    }

    [Fact]
    public async Task Re_reporting_a_step_outcome_in_a_non_default_Space_updates_not_throws()
    {
        // Regression: UpsertStepOutcomeAsync read `existing` through the Space
        // query filter, but the worker runs under DefaultSpaceId while the row
        // carries the task's real (non-Default) Space. A re-report (retry, agent
        // re-callback) therefore missed the existing row and attempted a duplicate
        // INSERT that the (task_id, step_index, target_id) unique index rejects.
        // The lookup now uses IgnoreQueryFilters, so the re-report UPDATEs.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, _) = await SeedInSpaceAsync(
            harness, NonDefaultSpaceId, stepNames: ["s1"], targetNames: ["t1"]);

        // First report — INSERT (stamps the deployment's real Space). The harness
        // context runs under DefaultSpaceId, exactly like the worker.
        await using (var db = harness.CreateContext())
        {
            await DeploymentWorker.UpsertStepOutcomeAsync(
                db, deploymentId, stepIndex: 0, stepName: "s1",
                outcome: StepOutcomeKind.Failed, attemptCount: 1, errorMessage: "boom",
                startedUtc: DateTimeOffset.UtcNow, completedUtc: DateTimeOffset.UtcNow,
                isServerSide: true, required: true, ct: CancellationToken.None, targetId: null);
            await db.SaveChangesAsync();
        }

        // Re-report the SAME (task, step, null-target) — must UPDATE, not throw.
        await using (var db = harness.CreateContext())
        {
            var act = async () =>
            {
                await DeploymentWorker.UpsertStepOutcomeAsync(
                    db, deploymentId, stepIndex: 0, stepName: "s1",
                    outcome: StepOutcomeKind.Succeeded, attemptCount: 2, errorMessage: null,
                    startedUtc: DateTimeOffset.UtcNow, completedUtc: DateTimeOffset.UtcNow,
                    isServerSide: true, required: true, ct: CancellationToken.None, targetId: null);
                await db.SaveChangesAsync();
            };
            await act.Should().NotThrowAsync(
                "the filter-free lookup finds the non-Default-Space row and updates it");
        }

        await using (var verify = harness.CreateContext())
        {
            var rows = await verify.TaskStepOutcomes.IgnoreQueryFilters()
                .Where(o => o.TaskId == deploymentId && o.StepIndex == 0)
                .ToListAsync();
            rows.Should().ContainSingle("the re-report updates in place, it does not insert a duplicate");
            rows[0].Outcome.Should().Be(StepOutcomeKind.Succeeded);
            rows[0].AttemptCount.Should().Be(2);
            rows[0].SpaceId.Should().Be(NonDefaultSpaceId);
        }
    }

    /// <summary>
    /// Seeds a minimal Project + Environment + Release + Target(s) + Deployment
    /// entirely in <paramref name="spaceId"/> (explicit SpaceId — the
    /// interceptor preserves caller-set values), bypassing the harness's
    /// Default-Space seed helpers. task_target_assignments is now ISpaceScoped
    /// with composite FKs, so its rows are stamped with <paramref name="spaceId"/>
    /// too (production stamps them from the deployment's Space context).
    /// </summary>
    private static async Task<(Guid DeploymentId, List<DeploymentTarget> Targets)>
        SeedInSpaceAsync(
            OrchestratorTestHarness harness,
            Guid spaceId,
            string[] stepNames,
            string[] targetNames)
    {
        await using var db = harness.CreateContext();

        if (!await db.Spaces.IgnoreQueryFilters().AnyAsync(s => s.Id == spaceId))
        {
            db.Spaces.Add(new Space
            {
                Id = spaceId, Slug = $"sp-{spaceId:N}"[..12], Name = "Non-Default",
            });
        }

        var project = new Project
        {
            SpaceId        = spaceId,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
            Name           = $"xp-{Guid.NewGuid():N}"[..12],
            Slug           = $"xp-{Guid.NewGuid():N}"[..12],
        };
        var env = new DeploymentEnvironment
        {
            SpaceId   = spaceId,
            Name      = $"xe-{Guid.NewGuid():N}"[..12],
            Slug      = $"xe-{Guid.NewGuid():N}"[..12],
            SortOrder = 1,
        };
        db.Projects.Add(project);
        db.Environments.Add(env);

        var targets = new List<DeploymentTarget>(targetNames.Length);
        foreach (var n in targetNames)
        {
            var t = new DeploymentTarget
            {
                SpaceId       = spaceId,
                Name          = n,
                Roles         = ["web"],
                TransportMode = TransportMode.Reverse,
                Status        = TargetStatus.Online,
            };
            db.DeploymentTargets.Add(t);
            targets.Add(t);
        }
        await db.SaveChangesAsync();

        var snapshot = new List<StepSnapshot>(stepNames.Length);
        for (var i = 0; i < stepNames.Length; i++)
        {
            snapshot.Add(StepBuilder.Script(stepNames[i]).ToSnapshot(i));
        }
        var release = new Release
        {
            SpaceId                    = spaceId,
            ProjectId                  = project.Id,
            Version                    = "1.0",
            ProcessSnapshot            = snapshot,
            VariableSnapshot           = [],
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        var deployment = new Deployment
        {
            SpaceId       = spaceId,
            ProjectId     = project.Id,
            ReleaseId     = release.Id,
            EnvironmentId = env.Id,
            Status        = DeploymentStatus.Queued,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        // Strictly increasing AddedUtc microseconds (timestamptz precision)
        // preserve assignment order (targets[0] = canonical), mirroring
        // DeploymentService.CreateAsync.
        var addedUtc = DateTimeOffset.UtcNow;
        for (var i = 0; i < targets.Count; i++)
        {
            db.TaskTargetAssignments.Add(new TaskTargetAssignment
            {
                SpaceId      = spaceId,
                TaskId       = deployment.Id,
                TargetId     = targets[i].Id,
                AddedUtc     = addedUtc.AddMicroseconds(i),
            });
        }
        await db.SaveChangesAsync();

        return (deployment.Id, targets);
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
