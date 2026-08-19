using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP3 — the manual-intervention gate end to end through the real orchestrator.
/// <list type="number">
///   <item><b>Pause</b> — the task parks <c>Paused</c> before the gated wave
///     dispatches, writes a checkpoint, DROPS its lease and frees its
///     <c>NodeTaskGate</c> slot.</item>
///   <item><b>Reaper exemption</b> — a paused, lease-less task survives a full
///     reconciler pass. This is the assertion that matters most: the B1 orphan arm
///     reaps a <c>Running</c> row with a null lease within the minute, so if
///     <c>Paused</c> ever became <c>Running</c>-shaped, every approval window would
///     die after 60 seconds.</item>
///   <item><b>Approve resumes from the checkpoint</b> — the pre-gate wave does NOT
///     re-run and the post-gate wave does.</item>
///   <item><b>Reject / timeout fail cleanly</b> — <c>Condition=Always</c> cleanup
///     steps still run, and the verdict is <c>Failed</c> (not
///     <c>SucceededWithWarnings</c>).</item>
/// </list>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ManualInterventionTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // ── 1. Pause ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gated_wave_pauses_the_task_before_dispatching_and_drops_its_lease()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "pa");

        // before → gate → after. The gate is its own wave (StartAfterPrevious is the
        // default), so the pause lands between "before" and "after".
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Script("before"),
            StepBuilder.Manual("approve-me", instructions: "Check the backup ran."),
            StepBuilder.Script("after"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        var agent = harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);

        var task = await harness.GetServerTaskAsync(deploymentId);
        task.Status.Should().Be(DeploymentStatus.Paused);
        task.CompletedUtc.Should().BeNull(because: "Paused is not terminal");
        task.StartedUtc.Should().NotBeNull(because: "the run started before it paused");

        // The lease MUST be released: a paused task has no owning process, and B1's
        // reconciler decides liveness by lease expiry.
        task.LeaseUntil.Should().BeNull();
        task.ClaimedBy.Should().BeNull();
        task.PauseCheckpointEncrypted.Should().NotBeNullOrEmpty(
            because: "the resume path has nothing else to reconstruct wave state from");

        agent.WaveCount.Should().Be(1,
            because: "only the pre-gate wave dispatched — the gate pauses BEFORE its own wave");

        var gates = await harness.GetInterruptionsAsync(deploymentId);
        var gate = gates.Should().ContainSingle().Subject;
        gate.Status.Should().Be(InterruptionStatus.Pending);
        gate.StepName.Should().Be("approve-me");
        gate.Instructions.Should().Be("Check the backup ran.");
        gate.ExpiresUtc.Should().NotBeNull(
            because: "the 72 h engine default applies when the step sets no timeout");
        gate.ResponsibleTeamIds.Should().BeEmpty(
            because: "an empty list means anyone with the respond permission");

        (await harness.GetAuditEventTypesAsync(deploymentId))
            .Should().Contain(AuditEventType.DeploymentPaused);
    }

    [Fact]
    public async Task Pausing_frees_the_node_task_gate_slot_for_another_deployment()
    {
        // A one-slot node. If the pause parked the orchestration in-process (holding
        // its slot for the whole approval window) the second, unrelated deployment
        // could never start — which is the failure mode this test exists to catch.
        await using var harness = new OrchestratorTestHarness(
            postgres, new EngineOptions { MaxConcurrentTasks = 1 });

        var env = await harness.SeedEnvironmentAsync($"ge-{Guid.NewGuid():N}"[..14]);
        var targets = await harness.SeedTargetsAsync($"gt-{Guid.NewGuid():N}"[..14]);
        harness.ConnectFakeAgent(targets[0]);
        // The second deployment gets its OWN target: a Paused task HOLDS its
        // targets (F6 — it is InFlightAfterClaim), so a same-box peer would be
        // target-blocked at claim and this test would measure the wrong gate.
        // The NodeTaskGate under test is node-global, so a second box still
        // contends for the one slot.
        var freeTargets = await harness.SeedTargetsAsync($"gt2-{Guid.NewGuid():N}"[..14]);
        harness.ConnectFakeAgent(freeTargets[0]);

        // Two DIFFERENT projects so F1's (project, env, tenant) serialization is not
        // what lets the second one through — the freed gate slot is.
        var gatedProject = await harness.SeedProjectAsync($"gp1-{Guid.NewGuid():N}"[..14]);
        var gatedRelease = await harness.SeedReleaseAsync(
            gatedProject.Id, "1.0", StepBuilder.Manual("hold"), StepBuilder.Script("after"));
        var gatedId = await harness.CreateDeploymentAsync(gatedRelease.Id, env.Id, targets);

        var freeProject = await harness.SeedProjectAsync($"gp2-{Guid.NewGuid():N}"[..14]);
        var freeRelease = await harness.SeedReleaseAsync(
            freeProject.Id, "1.0", StepBuilder.Script("work"));
        var freeId = await harness.CreateDeploymentAsync(freeRelease.Id, env.Id, freeTargets);

        await harness.StartWorkerAsync();
        await harness.EnqueueAsync(gatedId);
        await harness.WaitForPausedAsync(gatedId, TimeSpan.FromSeconds(30));

        await harness.EnqueueAsync(freeId);
        var free = await harness.WaitForTerminalAsync(freeId, TimeSpan.FromSeconds(30));

        free.Status.Should().Be(DeploymentStatus.Succeeded,
            because: "the paused deployment must not be holding the node's only slot");
        (await harness.GetServerTaskAsync(gatedId)).Status
            .Should().Be(DeploymentStatus.Paused);
    }

    // ── 2. Reaper exemption ─────────────────────────────────────────────────

    [Fact]
    public async Task Paused_task_survives_a_reconciler_pass_despite_having_no_lease()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "rp");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0", StepBuilder.Manual("hold"), StepBuilder.Script("after"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);
        (await harness.GetServerTaskAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Paused);

        // The reconciler's orphan arm reaps a Running row whose lease is expired OR
        // NULL. Our row has a null lease, so it is only spared because the predicate
        // is scoped to Running. Run the whole job — twice, well past any grace — and
        // assert the row is untouched.
        var reconciler = NewReconciler(out _);
        await reconciler.ExecuteAsync(CancellationToken.None);
        await reconciler.ExecuteAsync(CancellationToken.None);

        var task = await harness.GetServerTaskAsync(deploymentId);
        task.Status.Should().Be(DeploymentStatus.Paused,
            because: "a paused task has no owner by design and must not be reaped as an orphan");
        task.CompletedUtc.Should().BeNull();
        (await harness.GetAuditEventTypesAsync(deploymentId))
            .Should().NotContain(AuditEventType.DeploymentInterrupted);
    }

    [Fact]
    public async Task Reconciler_re_signals_a_paused_task_whose_gate_was_answered()
    {
        // The crash-safety arm: an approve enqueues a wake-up, but that channel item
        // dies with a server restart — and a restart inside a multi-day approval
        // window is likely. Without this arm the task would sit Paused forever.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "rs");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0", StepBuilder.Manual("hold"), StepBuilder.Script("after"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);
        var gate = (await harness.GetInterruptionsAsync(deploymentId)).Single();

        // Resolve WITHOUT enqueuing anything — simulating the lost wake-up.
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Approved);

        var reconciler = NewReconciler(out var queue);
        await reconciler.ExecuteAsync(CancellationToken.None);

        queue.Reader.TryRead(out var item).Should().BeTrue(
            because: "an answered gate on a Paused task must be re-signalled");
        item.Id.Should().Be(deploymentId);

        // A still-PENDING gate must NOT be re-signalled — that would resume a task
        // nobody has answered.
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Pending);
        while (queue.Reader.TryRead(out _)) { }
        await reconciler.ExecuteAsync(CancellationToken.None);
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    // ── 3. Approve ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Approving_resumes_from_the_checkpoint_without_re_running_earlier_waves()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "ap");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Script("before"),
            StepBuilder.Manual("approve-me"),
            StepBuilder.Script("after"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        var agent = harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);
        agent.WaveCount.Should().Be(1);

        var gate = (await harness.GetInterruptionsAsync(deploymentId)).Single();
        await harness.ResolveInterruptionAsync(
            gate.Id, InterruptionStatus.Approved, notes: "looks good", actedBy: "alice");

        // Resume through the very same dispatch entry point the wake-up drives.
        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Succeeded);
        deployment.PauseCheckpointEncrypted.Should().BeNull(
            because: "a resumed (and now terminal) task must not carry a checkpoint");

        agent.WaveCount.Should().Be(2,
            because: "'before' must NOT re-run — the resume starts at the gated wave, " +
                     "so exactly one further wave ('after') dispatches");
        agent.ReceivedPlans[^1].Steps.Should().ContainSingle()
            .Which.Name.Should().Be("after");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle(o => o.StepName == "approve-me")
            .Which.Outcome.Should().Be(StepOutcomeKind.ManualInterventionApproved);
        outcomes.Should().Contain(o => o.StepName == "before");
        outcomes.Should().Contain(o => o.StepName == "after");
    }

    // ── 4. Reject / timeout ─────────────────────────────────────────────────

    [Theory]
    [InlineData(InterruptionStatus.Rejected, StepOutcomeKind.ManualInterventionRejected)]
    [InlineData(InterruptionStatus.TimedOut, StepOutcomeKind.ManualInterventionTimedOut)]
    public async Task Rejection_and_timeout_fail_the_task_but_still_run_cleanup_steps(
        InterruptionStatus resolution, StepOutcomeKind expectedOutcome)
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "rj");

        // A Condition=Always cleanup step AFTER the gate. Refusing the change must
        // still let the process tidy up — that is the whole reason rejection resumes
        // the orchestration instead of failing it on the spot.
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("approve-me"),
            StepBuilder.Script("main"),
            new StepBuilder
            {
                Name      = "cleanup",
                Condition = KrakenDeploy.Execution.StepCondition.Always,
            });
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        var agent = harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);
        var gate = (await harness.GetInterruptionsAsync(deploymentId)).Single();
        await harness.ResolveInterruptionAsync(gate.Id, resolution, notes: "no");

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed,
            because: "a refused change is Failed in EVERY failure mode — hasFailed alone " +
                     "would resolve SucceededWithWarnings, which is the wrong verdict");

        var dispatchedSteps = agent.ReceivedPlans
            .SelectMany(p => p.Steps.Select(s => s.Name))
            .ToList();
        dispatchedSteps.Should().NotContain("main",
            because: "the work BEHIND the gate must not run once it is refused");
        dispatchedSteps.Should().Contain("cleanup",
            because: "Condition=Always cleanup steps run per the failure mode");

        (await harness.GetOutcomesAsync(deploymentId))
            .Should().ContainSingle(o => o.StepName == "approve-me")
            .Which.Outcome.Should().Be(expectedOutcome);
    }

    [Fact]
    public async Task A_step_timeout_override_wins_over_the_engine_default()
    {
        await using var harness = new OrchestratorTestHarness(
            postgres, new EngineOptions { DefaultInterventionTimeout = TimeSpan.FromHours(72) });
        var (project, env, targets) = await SeedAsync(harness, "to");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("quick", timeoutHours: 2),
            StepBuilder.Script("after"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);

        var gate = (await harness.GetInterruptionsAsync(deploymentId)).Single();
        gate.ExpiresUtc.Should().NotBeNull();
        (gate.ExpiresUtc!.Value - gate.CreatedUtc)
            .Should().BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Zero_step_timeout_falls_back_to_the_engine_default_rather_than_waiting_forever()
    {
        // WP3-b reversal. 0 used to mean "no auto-fail", producing a NULL ExpiresUtc.
        // InterruptionTimeoutJob filters on `ExpiresUtc != null`, so such a gate was never
        // reaped — and Paused is in InFlightAfterClaim, so its task held the
        // (project, environment, tenant) key for as long as it waited. A step author with
        // only ProcessEdit could block every later release of that project+environment
        // until someone with TaskCancel intervened. 0 is now refused at process save, and
        // a process that reached the gate carrying one anyway falls back to the engine
        // default: EVERY gate gets a deadline.
        var engineDefault = TimeSpan.FromHours(5);
        await using var harness = new OrchestratorTestHarness(
            postgres, new EngineOptions { DefaultInterventionTimeout = engineDefault });
        var (project, env, targets) = await SeedAsync(harness, "tz");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("forever", timeoutHours: 0),
            StepBuilder.Script("after"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);

        var gate = (await harness.GetInterruptionsAsync(deploymentId)).Single();
        gate.ExpiresUtc.Should().NotBeNull(
            because: "an unexpiring gate is never reaped and parks its task on the F1 slot");
        (gate.ExpiresUtc!.Value - gate.CreatedUtc).Should().BeCloseTo(
            engineDefault, TimeSpan.FromMinutes(1),
            because: "an unusable per-step value falls back to the engine default");
    }

    // ── 5. Misconfiguration fails closed ────────────────────────────────────

    [Fact]
    public async Task Unresolvable_responsible_team_ids_fail_the_task_rather_than_widening_it()
    {
        // The security-relevant case: a process imported from Octopus carries Octopus
        // team ids ("teams-123"). Dropping them would turn "only these teams may
        // approve" into "anyone with the permission may approve" — a silent
        // privilege widening on import. It must fail instead.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "tm");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate", responsibleTeamIds: "teams-123,teams-456"),
            StepBuilder.Script("after"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed);
        (await harness.GetInterruptionsAsync(deploymentId)).Should().BeEmpty(
            because: "no gate is created when its approver set cannot be resolved");
    }

    [Fact]
    public async Task A_paused_task_without_a_checkpoint_fails_instead_of_resuming_blind()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "nc");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0", StepBuilder.Manual("hold"), StepBuilder.Script("after"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        var agent = harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(deploymentId);
        var gate = (await harness.GetInterruptionsAsync(deploymentId)).Single();
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Approved);

        // Corrupt the invariant: Paused with no checkpoint. Resuming would silently
        // restart at wave 0 with empty failure/output state.
        await using (var db = harness.CreateContext())
        {
            await db.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == deploymentId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    t => t.PauseCheckpointEncrypted, (string?)null));
        }

        await harness.RunDeploymentAsync(deploymentId);

        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Failed,
                because: "a violated invariant must throw, not resume with guessed state");
        agent.ReceivedPlans.SelectMany(p => p.Steps.Select(s => s.Name))
            .Should().NotContain("after");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<(Project Project, DeploymentEnvironment Env, List<DeploymentTarget> Targets)>
        SeedAsync(OrchestratorTestHarness harness, string prefix)
    {
        var suffix = $"{Guid.NewGuid():N}"[..8];
        return (
            await harness.SeedProjectAsync($"{prefix}p-{suffix}"),
            await harness.SeedEnvironmentAsync($"{prefix}e-{suffix}"),
            await harness.SeedTargetsAsync($"{prefix}t-{suffix}"));
    }

    /// <summary>
    /// A reconciler wired to a FRESH channel so a test can assert exactly what the
    /// job signalled, independent of the harness's own dispatch channel.
    /// </summary>
    private ScheduledDeploymentDispatchJob NewReconciler(out Channel<TenantWorkItem> queue)
    {
        queue = Channel.CreateUnbounded<TenantWorkItem>();
        return new ScheduledDeploymentDispatchJob(
            postgres, queue, TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(), new NullAuditLog(),
            NullLogger<ScheduledDeploymentDispatchJob>.Instance);
    }
}
