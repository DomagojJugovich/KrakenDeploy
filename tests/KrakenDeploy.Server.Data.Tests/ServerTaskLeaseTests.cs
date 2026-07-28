using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B1 durable dispatch — the atomic <c>Queued→Running</c> claim + lease.
/// Wake-ups are at-least-once (create-time enqueue, dispatch job, reconciler);
/// these tests pin the property that makes execution exactly-once: only ONE
/// claim wins a row, a cancelled row can never be claimed, and the claim stamps
/// the lease + clears <c>ScheduledFor</c> so the scheduled-dispatch job can
/// never re-match a claimed task.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ServerTaskLeaseTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Claim_wins_once_and_stamps_running_lease_and_clears_schedule()
    {
        var id = await SeedQueuedDeploymentAsync(scheduledFor: DateTimeOffset.UtcNow.AddMinutes(-5));

        await using var db = postgres.CreateContext();
        var first = await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System);
        var second = await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System);

        first.Should().Be(ServerTaskClaimResult.Claimed, "the first wake-up claims the row");
        second.Should().Be(ServerTaskClaimResult.NotQueued,
            "a duplicate wake-up must lose the claim and bail");

        var task = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == id);
        task.Status.Should().Be(DeploymentStatus.Running);
        task.StartedUtc.Should().NotBeNull();
        task.ClaimedBy.Should().Be(ServerTaskLease.ProcessOwner);
        task.LeaseUntil.Should().NotBeNull();
        task.ScheduledFor.Should().BeNull(
            "a claimed task must never be re-matched by the scheduled-dispatch job");
    }

    [Fact]
    public async Task Cancelled_row_cannot_be_claimed()
    {
        var id = await SeedQueuedDeploymentAsync();

        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, DeploymentStatus.Cancelled));

        (await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.NotQueued,
                "cancel-before-dispatch must win over any wake-up");
    }

    [Fact]
    public async Task Renew_extends_a_running_lease_and_refuses_terminal_rows()
    {
        var id = await SeedQueuedDeploymentAsync();

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);

        var before = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == id).Select(t => t.LeaseUntil).FirstAsync();

        (await ServerTaskLease.TryRenewAsync(db, id, TimeProvider.System)).Should().BeTrue();

        var after = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == id).Select(t => t.LeaseUntil).FirstAsync();
        after.Should().BeOnOrAfter(before!.Value, "renewal pushes the lease forward");

        // Terminal (e.g. the reconciler failed it as orphaned) — renewal must refuse.
        await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, DeploymentStatus.Failed));
        (await ServerTaskLease.TryRenewAsync(db, id, TimeProvider.System))
            .Should().BeFalse("a non-Running row must not be re-leased");
    }

    // D1 Phase 3: the Release_clears_the_lease_fields test went with
    // ServerTaskLease.ReleaseAsync — the mid-flight release belonged to the
    // deleted runbook hand-off model; terminal writes clear the lease inline.

    // ── F1: (project, environment, tenant) serialization ─────────────────────

    [Fact]
    public async Task Second_deployment_of_same_project_env_is_blocked_until_first_terminal()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var first  = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        var second = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, first, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed, "the first deployment of the key wins");
        (await ServerTaskLease.TryClaimAsync(db, second, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.SerializationBlocked,
                "a second deployment of the same (project, env, tenant) must wait");

        // The refused task stays Queued (the minutely re-signal retries it).
        (await StatusOf(db, second)).Should().Be(DeploymentStatus.Queued);

        // Once the first is terminal, the key frees and the second can claim.
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == first)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, DeploymentStatus.Succeeded));
        (await ServerTaskLease.TryClaimAsync(db, second, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "with the running deployment terminal the key is free");
    }

    [Fact]
    public async Task PendingOfflineResult_peer_blocks_a_same_key_claim_until_terminal()
    {
        // A same-key offline-drop deployment parked at PendingOfflineResult is
        // claimed-but-NOT-terminal — it still holds the (project,env,tenant) slot,
        // so a new deployment of the key must wait (not just for Running peers).
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var parked = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        var next   = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, parked, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        // Move the claimed deployment to the parked offline state (non-terminal).
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == parked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, DeploymentStatus.PendingOfflineResult));

        (await ServerTaskLease.TryClaimAsync(db, next, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.SerializationBlocked,
                "a parked offline-drop deployment (PendingOfflineResult) is non-terminal and " +
                "still holds the key");
        (await StatusOf(db, next)).Should().Be(DeploymentStatus.Queued);

        // Only once the parked deployment reaches a TERMINAL state does the key free.
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == parked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, DeploymentStatus.Succeeded));
        (await ServerTaskLease.TryClaimAsync(db, next, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
    }

    [Fact]
    public async Task Different_tenants_of_same_project_env_claim_in_parallel()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var t1 = await harness.SeedTenantAsync("tenant-a");
        var t2 = await harness.SeedTenantAsync("tenant-b");
        var d1 = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets, tenantId: t1.Id);
        var d2 = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets, tenantId: t2.Id);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, d1, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, d2, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "different tenants of the same project+env are independent keys");
    }

    [Fact]
    public async Task Null_tenant_is_its_own_serialization_key()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var tenant = await harness.SeedTenantAsync("tenant-a");
        var tenanted    = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets, tenantId: tenant.Id);
        var untenantedA = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        var untenantedB = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, tenanted, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, untenantedA, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "a tenanted run does not block an untenanted one — NULL tenant is its own key");
        (await ServerTaskLease.TryClaimAsync(db, untenantedB, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.SerializationBlocked,
                "untenanted deployments serialize among themselves");
    }

    [Fact]
    public async Task Concurrent_claims_of_same_key_let_exactly_one_win()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var a = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        var b = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);

        // Two claimants on SEPARATE connections, racing. The (project,env,tenant)
        // advisory xact lock must let exactly one through — without it, write-skew
        // under READ COMMITTED would let both see "no peer" and both win.
        await using var dbA = postgres.CreateContext();
        await using var dbB = postgres.CreateContext();
        var results = await Task.WhenAll(
            ServerTaskLease.TryClaimAsync(dbA, a, TimeProvider.System),
            ServerTaskLease.TryClaimAsync(dbB, b, TimeProvider.System));

        results.Count(r => r == ServerTaskClaimResult.Claimed).Should().Be(1,
            "the advisory lock serializes the check+claim so only one same-key deployment runs");
        results.Count(r => r == ServerTaskClaimResult.SerializationBlocked).Should().Be(1);
    }

    [Fact]
    public async Task RunbookRuns_are_exempt_from_deployment_serialization()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var deployment = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        var run        = await harness.SeedRunbookRunAsync(key.ProjectId, key.EnvId);

        await using var db = postgres.CreateContext();
        // A Running deployment of the key does NOT block a runbook run of the
        // same project+env — runbook runs are exempt (they take the plain claim).
        (await ServerTaskLease.TryClaimAsync(db, deployment, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await ServerTaskLease.TryClaimAsync(db, run, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "runbook runs are exempt from (project, env, tenant) serialization");

        // And symmetrically: a Running runbook run is not a deployment peer, so it
        // does not block a fresh deployment of the same key. (Retire the earlier
        // deployment first so only the runbook run is Running.)
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == deployment)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, DeploymentStatus.Succeeded));
        var deployment2 = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        (await ServerTaskLease.TryClaimAsync(db, deployment2, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "a Running runbook run is not a deployment peer");
    }

    [Fact]
    public async Task Oldest_queued_deployment_of_a_key_claims_first_fifo()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var d1 = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        var d2 = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        var d3 = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);

        await using var db = postgres.CreateContext();
        var baseUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SetCreatedUtc(db, d1, baseUtc);
        await SetCreatedUtc(db, d2, baseUtc.AddMinutes(1));
        await SetCreatedUtc(db, d3, baseUtc.AddMinutes(2));

        // No peer is running, yet the younger two defer to the oldest (FIFO).
        (await ServerTaskLease.TryClaimAsync(db, d3, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.SerializationBlocked, "d1 and d2 are queued earlier");
        (await ServerTaskLease.TryClaimAsync(db, d2, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.SerializationBlocked, "d1 is queued earlier");
        (await ServerTaskLease.TryClaimAsync(db, d1, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed, "the oldest queued deployment claims first");

        // d1 terminal → d2 is now the front; d3 still defers to d2.
        await SetStatus(db, d1, DeploymentStatus.Succeeded);
        (await ServerTaskLease.TryClaimAsync(db, d3, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.SerializationBlocked, "d2 is still queued earlier");
        (await ServerTaskLease.TryClaimAsync(db, d2, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);

        // d2 terminal → d3 is the front.
        await SetStatus(db, d2, DeploymentStatus.Succeeded);
        (await ServerTaskLease.TryClaimAsync(db, d3, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
    }

    [Fact]
    public async Task Future_scheduled_older_sibling_does_not_block_a_due_deployment()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var older = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);
        var due   = await harness.CreateDeploymentAsync(key.ReleaseId, key.EnvId, key.Targets);

        await using var db = postgres.CreateContext();
        // `older` is queued earlier BUT scheduled for the future → not yet due, so
        // it must not block the ready `due` one (no starvation by a future-dated
        // sibling — the FIFO gate only counts already-due Queued peers).
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == older)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CreatedUtc, DateTimeOffset.UtcNow.AddMinutes(-10))
                .SetProperty(t => t.ScheduledFor, DateTimeOffset.UtcNow.AddHours(1)));

        (await ServerTaskLease.TryClaimAsync(db, due, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "a future-scheduled older sibling is not due and must not block a ready deployment");
    }

    [Fact]
    public async Task Claim_runs_through_the_retrying_execution_strategy()
    {
        // The web host enables NpgsqlRetryingExecutionStrategy; the deployment
        // claim opens a user-initiated transaction, which is only legal when driven
        // THROUGH the execution strategy. This proves the wrapper is correctly
        // placed — a bare BeginTransactionAsync under retry throws "does not support
        // user-initiated transactions".
        var id = await SeedQueuedDeploymentAsync();

        await using var db = postgres.CreateRetryingContext();
        (await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed);
        (await StatusOf(db, id)).Should().Be(DeploymentStatus.Running);
    }

    // ── Maintenance gate ───────────────────────────────────────────────────
    //
    // Octopus's maintenance mode blocks non-admin CHANGES but does NOT stop the
    // task queue: already-queued and scheduled deployments still start and run to
    // completion during the window. These tests pin that KrakenDeploy does NOT
    // share that hole — the gate sits on the claim, the single choke point every
    // wake-up source funnels through, so the queue genuinely stops.

    [Fact]
    public async Task Maintenance_mode_blocks_the_claim_and_leaves_the_task_queued()
    {
        var id = await SeedQueuedDeploymentAsync();

        await using var db = postgres.CreateContext();
        await WithMaintenanceAsync("Upgrading to v1.2", async () =>
        {
            (await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System))
                .Should().Be(ServerTaskClaimResult.MaintenanceBlocked,
                    "no new task may start while the instance is in maintenance");

            (await StatusOf(db, id)).Should().Be(DeploymentStatus.Queued,
                "a refused claim must leave the row Queued for the post-window re-signal");
        });

        // Window closed → the very same row claims normally, no operator action and
        // no extra poller needed (the minutely stale-Queued re-signal drives it).
        (await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System))
            .Should().Be(ServerTaskClaimResult.Claimed,
                "disabling maintenance must let the queue drain again");
    }

    [Fact]
    public async Task Maintenance_mode_blocks_an_already_due_scheduled_deployment()
    {
        // The exact Octopus hole: a deployment scheduled for a time that ARRIVES
        // during the maintenance window. The minutely dispatch job still signals it
        // (its orphan-reconciliation arm must keep running, so the job itself is
        // deliberately not paused) — the claim is what refuses to start it.
        var id = await SeedQueuedDeploymentAsync(scheduledFor: DateTimeOffset.UtcNow.AddMinutes(-5));

        await using var db = postgres.CreateContext();
        await WithMaintenanceAsync("Migrating the schema", async () =>
        {
            (await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System))
                .Should().Be(ServerTaskClaimResult.MaintenanceBlocked,
                    "a scheduled deployment whose time arrives mid-window must not fire");

            var task = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(t => t.Id == id);
            task.Status.Should().Be(DeploymentStatus.Queued);
            task.ScheduledFor.Should().NotBeNull(
                "a refused claim must not clear ScheduledFor — only a winning claim does");
            task.StartedUtc.Should().BeNull();
        });
    }

    [Fact]
    public async Task Maintenance_mode_blocks_runbook_runs_too()
    {
        // RunbookRun is exempt from F1 serialization (operational tooling), but NOT
        // from the maintenance gate: a runbook run mid-migration races the schema
        // exactly as hard as a deployment does.
        await using var harness = new OrchestratorTestHarness(postgres);
        var key = await SeedSharedKeyAsync(harness);
        var run = await harness.SeedRunbookRunAsync(key.ProjectId, key.EnvId);

        await using var db = postgres.CreateContext();
        await WithMaintenanceAsync("Patching the host", async () =>
        {
            (await ServerTaskLease.TryClaimAsync(db, run, TimeProvider.System))
                .Should().Be(ServerTaskClaimResult.MaintenanceBlocked,
                    "the maintenance gate is kind-agnostic — it sits ahead of the kind branch");
        });
    }

    [Fact]
    public async Task Child_task_still_claims_during_maintenance_so_its_parent_cannot_strand()
    {
        // A child spawned by an Octopus.DeployRelease step is the continuation of an
        // already-claimed parent, not new work. Blocking it would hang the parent's
        // WaitForChildAsync forever — and the parent keeps renewing its lease, so the
        // reconciler would never reap it either. Same exemption shape as the E3
        // NodeTaskGate bypass.
        var parentId = await SeedQueuedDeploymentAsync();
        var childId  = await SeedQueuedDeploymentAsync();

        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == childId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ParentTaskId, parentId));

        await WithMaintenanceAsync("Upgrading", async () =>
        {
            (await ServerTaskLease.TryClaimAsync(db, childId, TimeProvider.System))
                .Should().Be(ServerTaskClaimResult.Claimed,
                    "a child must claim during maintenance or its parent strands");

            (await ServerTaskLease.TryClaimAsync(db, parentId, TimeProvider.System))
                .Should().Be(ServerTaskClaimResult.MaintenanceBlocked,
                    "the exemption is for children only — a top-level task is still gated");
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="body"/> with instance-wide maintenance mode ON,
    /// then always turns it back off — the class shares one cloned database, so a
    /// leaked flag would block every later test's claim. Goes through the real
    /// <see cref="MaintenanceModeService"/> (not a hand-written settings row) so the
    /// test exercises the same write path the operator's toggle uses.</summary>
    private async Task WithMaintenanceAsync(string reason, Func<Task> body)
    {
        var maintenance = new MaintenanceModeService(
            new SettingsService(postgres.ScopeFactory, TimeProvider.System), TimeProvider.System);
        await maintenance.EnableAsync(reason, userId: null);
        try
        {
            await body();
        }
        finally
        {
            await maintenance.DisableAsync();
        }
    }

    private static async Task SetCreatedUtc(KrakenDbContext db, Guid id, DateTimeOffset createdUtc)
        => await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedUtc, createdUtc));

    private static async Task SetStatus(KrakenDbContext db, Guid id, DeploymentStatus status)
        => await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status));

    private async Task<Guid> SeedQueuedDeploymentAsync(DateTimeOffset? scheduledFor = null)
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"lp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"le-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("lease-t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        var id = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        if (scheduledFor is not null)
        {
            await using var db = postgres.CreateContext();
            await db.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ScheduledFor, scheduledFor));
        }

        return id;
    }

    /// <summary>Seeds ONE project + environment + release + target so several
    /// deployments can be created that share the same (project, env) key —
    /// unlike <see cref="SeedQueuedDeploymentAsync"/>, which mints a fresh
    /// project/env each call. Unique Guid-based names avoid slug collisions
    /// between tests sharing the class's cloned database.</summary>
    private static async Task<(Guid ProjectId, Guid ReleaseId, Guid EnvId, List<DeploymentTarget> Targets)>
        SeedSharedKeyAsync(OrchestratorTestHarness harness)
    {
        var project = await harness.SeedProjectAsync($"lp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"le-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync($"ft-{Guid.NewGuid():N}"[..12]);
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        return (project.Id, release.Id, env.Id, targets);
    }

    private static async Task<DeploymentStatus> StatusOf(KrakenDbContext db, Guid id)
        => await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == id)
            .Select(t => t.Status)
            .FirstAsync();
}
