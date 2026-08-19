using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// F6 — server-side per-plan target exclusion at claim time
/// (<see cref="ServerTaskTargetExclusion"/> + the F6 arm of
/// <see cref="ServerTaskLease.TryClaimAsync"/>): no two tasks operate on the
/// same SERIAL target concurrently, for the whole plan duration. These tests
/// pin the mutual-consent model (target flag OR source consent, conflict when
/// not both sides Shared), FIFO-by-overlap ordering, the kind symmetry
/// (deployment ↔ runbook run), the DeployRelease ancestor exemption, the
/// global-advisory-lock race, and the reason surface (blocker description +
/// exactly one first-deferral log line).
/// <para>
/// Every test uses DIFFERENT projects for the contending tasks so the F1
/// (project, env, tenant) rule — checked BEFORE the target arm and covered by
/// <see cref="ServerTaskLeaseTests"/> — can never be the thing deciding.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class TargetExclusionTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // ── Core exclusion + FIFO ─────────────────────────────────────────────────

    [Fact]
    public async Task Exclusive_vs_exclusive_on_shared_target_defers_in_fifo_order()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));
        var dA = await SeedDeploymentAsync(harness, env.Id, targets);
        var dB = await SeedDeploymentAsync(harness, env.Id, targets);
        var dC = await SeedDeploymentAsync(harness, env.Id, targets);

        await using var db = postgres.CreateContext();
        // Deterministic queue order for the FIFO half.
        var baseUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SetCreatedUtc(db, dA, baseUtc);
        await SetCreatedUtc(db, dB, baseUtc.AddMinutes(1));
        await SetCreatedUtc(db, dC, baseUtc.AddMinutes(2));

        (await ServerTaskLease.TryClaimAsync(db, dA, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, dB, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked,
                "an exclusive plan holds its target for the WHOLE plan — a second " +
                "exclusive plan on the same box must wait");
        (await StatusOf(db, dB)).Should().Be(DeploymentStatus.Queued,
            "a refused claim leaves the row Queued for the minutely re-signal");

        // FIFO by overlap: with dA terminal, the OLDER queued conflicting task
        // (dB) goes first — dC keeps deferring to it even though the target is free.
        await SetStatus(db, dA, DeploymentStatus.Succeeded);
        (await ServerTaskLease.TryClaimAsync(db, dC, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked, "dB is queued earlier and conflicts");
        (await ServerTaskLease.TryClaimAsync(db, dB, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed, "the oldest conflicting queued task claims first");

        await SetStatus(db, dB, DeploymentStatus.Succeeded);
        (await ServerTaskLease.TryClaimAsync(db, dC, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
    }

    [Fact]
    public async Task Both_consenting_sources_co_claim_on_a_shared_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));
        var dA = await SeedDeploymentAsync(harness, env.Id, targets, projectConsents: true);
        var dB = await SeedDeploymentAsync(harness, env.Id, targets, projectConsents: true);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, dA, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, dB, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "mutual consent (both projects opted in) makes the shared target a " +
                "mutual-Shared overlap — it neither defers nor orders");
    }

    [Fact]
    public async Task Target_level_consent_alone_co_claims()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));
        // The target flag removes the box from EVERY exclusion set — no source
        // consent needed ("if we allow parallel execution, all is allowed").
        await harness.SetAllowParallelTaskExecutionAsync(targets[0].Id, true);
        var dA = await SeedDeploymentAsync(harness, env.Id, targets);
        var dB = await SeedDeploymentAsync(harness, env.Id, targets);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, dA, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, dB, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "a parallel-consenting target is never a serial target");
    }

    [Fact]
    public async Task One_sided_consent_still_defers_in_both_directions()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));
        var consenting = await SeedDeploymentAsync(harness, env.Id, targets, projectConsents: true);
        var exclusive  = await SeedDeploymentAsync(harness, env.Id, targets);

        await using var db = postgres.CreateContext();
        // Direction 1: the consenting plan runs; the exclusive one must wait
        // (it did not opt in — consent is mutual, not contagious).
        (await ServerTaskLease.TryClaimAsync(db, consenting, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, exclusive, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked,
                "an exclusive plan never co-runs, regardless of the peer's consent");

        // Direction 2: with the exclusive plan running, a fresh consenting one
        // must wait too — one Exclusive side is enough for a conflict.
        await SetStatus(db, consenting, DeploymentStatus.Succeeded);
        (await ServerTaskLease.TryClaimAsync(db, exclusive, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        var consenting2 = await SeedDeploymentAsync(harness, env.Id, targets, projectConsents: true);
        (await ServerTaskLease.TryClaimAsync(db, consenting2, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked,
                "a consenting plan still waits behind an exclusive holder");
    }

    // ── Kind symmetry (C4) ───────────────────────────────────────────────────

    [Fact]
    public async Task Runbook_run_and_deployment_exclude_each_other_symmetrically()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));

        // Direction 1: a running DEPLOYMENT blocks a runbook run on the box.
        var deployment = await SeedDeploymentAsync(harness, env.Id, targets);
        var projectB = await harness.SeedProjectAsync(UniqueName("p"));
        var run = await harness.CreateRunbookRunAsync(
            projectB.Id, env.Id, targets, [StepBuilder.Script("s1")]);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, deployment, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, run, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked,
                "runbook runs are F1-exempt but fully participate in the target exclusion");

        // Direction 2: with the deployment terminal the run claims; a fresh
        // deployment then waits behind the RUNNING RUN.
        await SetStatus(db, deployment, DeploymentStatus.Succeeded);
        (await ServerTaskLease.TryClaimAsync(db, run, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        var deployment2 = await SeedDeploymentAsync(harness, env.Id, targets);
        (await ServerTaskLease.TryClaimAsync(db, deployment2, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked,
                "the exclusion is symmetric — an in-flight runbook run holds the target too");
    }

    // ── Exemptions ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployRelease_child_claims_while_its_parent_is_in_flight_on_the_same_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));
        var parent = await SeedDeploymentAsync(harness, env.Id, targets);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, parent, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);

        // An UNRELATED task queues on the box while the parent runs — the
        // routine ingredient of the child deadlock: it is OLDER than the child
        // (created before it) yet can never claim while the parent is in-flight.
        var olderQueued = await SeedDeploymentAsync(harness, env.Id, targets);
        (await ServerTaskLease.TryClaimAsync(db, olderQueued, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked,
                "the exemption is per-chain, not per-status — an unrelated task " +
                "waits behind the running parent");

        // The child (different project, SAME target) is the parent's continuation:
        // blocking it would strand the parent's WaitForChildAsync — and deferring
        // it to the OLDER QUEUED unrelated task is a three-way circular wait
        // (child → olderQueued → in-flight parent → child), so the child skips
        // the FIFO arm and defers only to in-flight conflicts.
        var childProject = await harness.SeedProjectAsync(UniqueName("p"));
        var childRelease = await harness.SeedReleaseAsync(childProject.Id, "1.0", StepBuilder.Script("s1"));
        var child = await harness.CreateDeploymentAsync(
            childRelease.Id, env.Id, targets, parentTaskId: parent);

        (await ServerTaskLease.TryClaimAsync(db, child, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "a child never conflicts with its ancestor chain, and never defers " +
                "to a queued task that is itself deferring to the child's parent");
    }

    [Fact]
    public async Task Disjoint_targets_never_defer()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targetsA = await harness.SeedTargetsAsync(UniqueName("ta"));
        var targetsB = await harness.SeedTargetsAsync(UniqueName("tb"));
        var dA = await SeedDeploymentAsync(harness, env.Id, targetsA);
        var dB = await SeedDeploymentAsync(harness, env.Id, targetsB);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, dA, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, dB, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "the exclusion is per shared target — disjoint sets are unaffected");
    }

    // ── The advisory-lock race ───────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_claimants_with_overlapping_target_sets_cannot_both_pass()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var t1 = await harness.SeedTargetsAsync(UniqueName("t1"));
        var t2 = await harness.SeedTargetsAsync(UniqueName("t2"));
        var t3 = await harness.SeedTargetsAsync(UniqueName("t3"));
        // Overlap on t2 only — the exact shape F1's per-key lock could NOT
        // serialize (different projects, different F1 keys) and the reason the
        // claim-decision lock is GLOBAL.
        var dA = await SeedDeploymentAsync(harness, env.Id, [t1[0], t2[0]]);
        var dB = await SeedDeploymentAsync(harness, env.Id, [t2[0], t3[0]]);

        await using var dbA = postgres.CreateContext();
        await using var dbB = postgres.CreateContext();
        var results = await Task.WhenAll(
            ServerTaskLease.TryClaimAsync(dbA, dA, TimeProvider.System),
            ServerTaskLease.TryClaimAsync(dbB, dB, TimeProvider.System));

        results.Count(r => r == ServerTaskClaimResult.Claimed).Should().Be(1,
            "the global advisory lock serializes the check+claim, so two claimants " +
            "sharing one serial target cannot both see it free");
        results.Count(r => r == ServerTaskClaimResult.TargetBlocked).Should().Be(1);
    }

    // ── Reason surface ───────────────────────────────────────────────────────

    [Fact]
    public async Task Blocked_task_carries_the_blocker_reason_with_queue_position()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));
        var running = await SeedDeploymentAsync(harness, env.Id, targets);
        var queuedAhead = await SeedDeploymentAsync(harness, env.Id, targets);
        var blocked = await SeedDeploymentAsync(harness, env.Id, targets);

        await using var db = postgres.CreateContext();
        var baseUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SetCreatedUtc(db, running, baseUtc);
        await SetCreatedUtc(db, queuedAhead, baseUtc.AddMinutes(1));
        await SetCreatedUtc(db, blocked, baseUtc.AddMinutes(2));
        (await ServerTaskLease.TryClaimAsync(db, running, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, blocked, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked);

        var conflict = await ServerTaskTargetExclusion.DescribeConflictAsync(
            db, blocked, DateTimeOffset.UtcNow);

        conflict.Should().NotBeNull("a refused claim must be explainable");
        conflict!.BlockerTaskId.Should().Be(running,
            "the IN-FLIGHT conflict outranks the queued one as the shown blocker");
        conflict.BlockerInFlight.Should().BeTrue();
        conflict.TargetId.Should().Be(targets[0].Id);
        conflict.TargetName.Should().Be(targets[0].Name);
        conflict.QueuedAhead.Should().Be(1, "one older conflicting task is queued ahead");

        var message = ServerTaskTargetExclusion.Format(conflict);
        message.Should().StartWith(ServerTaskTargetExclusion.MessagePrefix);
        message.Should().Contain(targets[0].Name).And.Contain("busy with")
            .And.Contain(running.ToString()[..8]).And.Contain("1 task ahead");
    }

    [Fact]
    public async Task First_deferral_log_line_is_written_exactly_once_even_under_a_race()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));
        var running = await SeedDeploymentAsync(harness, env.Id, targets);
        var blocked = await SeedDeploymentAsync(harness, env.Id, targets);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, running, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, blocked, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked);

        var conflict = await ServerTaskTargetExclusion.DescribeConflictAsync(
            db, blocked, DateTimeOffset.UtcNow);
        var message = ServerTaskTargetExclusion.Format(conflict!);

        // Duplicate wake-ups race the append on separate connections — the
        // per-task advisory lock + probe must let exactly one line through.
        await using var db1 = postgres.CreateContext();
        await using var db2 = postgres.CreateContext();
        var appended = await Task.WhenAll(
            ServerTaskTargetExclusion.TryAppendFirstDeferralLogAsync(
                db1, blocked, message, TimeProvider.System),
            ServerTaskTargetExclusion.TryAppendFirstDeferralLogAsync(
                db2, blocked, message, TimeProvider.System));
        appended.Count(a => a).Should().Be(1, "exactly one racer appends");

        // A later deferral (the minutely re-signal) appends nothing new.
        (await ServerTaskTargetExclusion.TryAppendFirstDeferralLogAsync(
                db, blocked, message, TimeProvider.System))
            .Should().BeFalse("the first-deferral line is one-time");

        var lines = await db.TaskLogLive.AsNoTracking()
            .Where(l => l.TaskId == blocked)
            .ToListAsync();
        lines.Should().HaveCount(1);
        lines[0].Message.Should().Be(message);
        lines[0].StepIndex.Should().Be(-1, "it is a task-level banner line");
        lines[0].Level.Should().Be(ServerTaskTargetExclusion.TargetWaitLogLevel,
            "the dedicated level is the durable dedup marker — the message copy is free to change");
    }

    [Fact]
    public async Task Cancelled_task_never_receives_a_deferral_log_line()
    {
        // The claim's conflict check is status-blind about the claiming row
        // itself, so a pending wake-up racing an operator's cancel can still be
        // told TargetBlocked — the durable write must re-check Queued.
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var targets = await harness.SeedTargetsAsync(UniqueName("t"));
        var running = await SeedDeploymentAsync(harness, env.Id, targets);
        var blocked = await SeedDeploymentAsync(harness, env.Id, targets);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, running, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, blocked, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.TargetBlocked);

        var conflict = await ServerTaskTargetExclusion.DescribeConflictAsync(
            db, blocked, DateTimeOffset.UtcNow);
        await SetStatus(db, blocked, DeploymentStatus.Cancelled);

        (await ServerTaskTargetExclusion.TryAppendFirstDeferralLogAsync(
                db, blocked, ServerTaskTargetExclusion.Format(conflict!), TimeProvider.System))
            .Should().BeFalse("a task no longer Queued must not get a permanent waiting line");
        (await db.TaskLogLive.AsNoTracking().AnyAsync(l => l.TaskId == blocked))
            .Should().BeFalse();
    }

    // ── The Tasks page ?target= filter (assignment-joined reads) ─────────────

    [Fact]
    public async Task Target_filtered_task_reads_return_exactly_the_assignment_joined_rows()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var env = await harness.SeedEnvironmentAsync(UniqueName("env"));
        var t1 = await harness.SeedTargetsAsync(UniqueName("t1"));
        var t2 = await harness.SeedTargetsAsync(UniqueName("t2"));
        var onT1 = await SeedDeploymentAsync(harness, env.Id, t1);
        var onT2 = await SeedDeploymentAsync(harness, env.Id, t2);
        var projectR = await harness.SeedProjectAsync(UniqueName("p"));
        var runOnT1 = await harness.CreateRunbookRunAsync(
            projectR.Id, env.Id, t1, [StepBuilder.Script("s1")]);

        var queue = Channel.CreateUnbounded<TenantWorkItem>();
        var deployments = new DeploymentService(postgres, queue, TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            new AllowAllPermissionEvaluator());
        var runbooks = new RunbookService(postgres, queue, TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            new AllowAllPermissionEvaluator());

        // The ?target= filter resolves through these assignment-joined reads —
        // row titles never reliably contain machine names, so this join is the
        // only correct resolution.
        var depRows = await deployments.GetForTargetAsync(t1[0].Id);
        depRows.Select(d => d.Id).Should().BeEquivalentTo([onT1],
            "only the deployment assigned to t1 may match");
        depRows.Should().NotContain(d => d.Id == onT2);

        var runRows = await runbooks.GetRunsForTargetAsync(t1[0].Id);
        runRows.Select(r => r.Id).Should().BeEquivalentTo([runOnT1]);

        (await deployments.GetForTargetAsync(t2[0].Id)).Select(d => d.Id)
            .Should().BeEquivalentTo([onT2]);
        (await runbooks.GetRunsForTargetAsync(t2[0].Id)).Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Seeds a FRESH project (+ release) per deployment so contenders
    /// never share an F1 key — every deferral these tests observe is the F6
    /// target arm. Optionally stamps the project's parallel-execution consent.</summary>
    private static async Task<Guid> SeedDeploymentAsync(
        OrchestratorTestHarness harness,
        Guid envId,
        IReadOnlyList<DeploymentTarget> targets,
        bool projectConsents = false)
    {
        var project = await harness.SeedProjectAsync(UniqueName("p"));
        if (projectConsents)
        {
            await harness.SetProjectAllowParallelTaskExecutionAsync(project.Id, true);
        }
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        return await harness.CreateDeploymentAsync(release.Id, envId, targets);
    }

    private static string UniqueName(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(16, prefix.Length + 12)];

    private static async Task SetCreatedUtc(KrakenDbContext db, Guid id, DateTimeOffset createdUtc)
        => await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedUtc, createdUtc));

    private static async Task SetStatus(KrakenDbContext db, Guid id, DeploymentStatus status)
        => await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status));

    private static async Task<DeploymentStatus> StatusOf(KrakenDbContext db, Guid id)
        => await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == id)
            .Select(t => t.Status)
            .FirstAsync();
}
