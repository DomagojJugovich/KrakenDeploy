using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B1/T0-1 — the dispatch reconciler (boot + minutely sweep). A server crash
/// used to strand Queued rows forever (their wake-up lived only in the
/// in-process channel) and leave mid-run tasks stuck at Running with no owner.
/// D1: BOTH kinds share the unified orchestrator, so the reconciler re-signals
/// stale Queued tasks (both kinds, to the ONE task channel) and fails Running
/// tasks (both kinds) whose lease expired OR was never stamped — while a LIVE
/// lease (a draining blue-green slot, a long step) is never touched. (D1
/// Phase 3 removed the transition-era arm-4 ceiling and its null-lease
/// runbook-run exemption: every live orchestration holds a lease.)
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DispatchReconcileTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Stale_queued_deployment_is_resignalled()
    {
        var id = await SeedDeploymentAsync();
        await AgeAsync(id, createdAgo: TimeSpan.FromMinutes(10));
        var (job, tasks, _) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        tasks.Reader.TryRead(out var item).Should().BeTrue(
            "a Queued task older than the grace lost its wake-up in a restart and must be re-signalled");
        item.Id.Should().Be(id);
    }

    [Fact]
    public async Task Fresh_queued_deployment_is_left_alone()
    {
        var id = await SeedDeploymentAsync(); // CreatedUtc = now, inside the grace window
        var (job, tasks, _) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        // Other tests in this class DB may legitimately be re-signalled; the
        // assertion is that THIS fresh row is not among them.
        var signalled = new List<Guid>();
        while (tasks.Reader.TryRead(out var item))
        {
            signalled.Add(item.Id);
        }
        signalled.Should().NotContain(id,
            "a just-created Queued row's original wake-up is still in flight");
    }

    [Fact]
    public async Task Expired_lease_running_deployment_is_failed_with_interrupted_audit()
    {
        var id = await SeedDeploymentAsync();
        await SetRunningAsync(id, leaseUntil: DateTimeOffset.UtcNow.AddMinutes(-1), claimedBy: "kraken:dead-node");
        var (job, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == id);
        task.Status.Should().Be(DeploymentStatus.Failed,
            "an expired lease means the orchestrating process died; the run is unresumable");
        task.CompletedUtc.Should().NotBeNull();
        task.ClaimedBy.Should().BeNull();
        task.LeaseUntil.Should().BeNull();

        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.DeploymentInterrupted && e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Null_lease_running_deployment_is_failed_too()
    {
        // Rows from before the lease feature (or a claim that predates an upgrade).
        // A null-lease Running DEPLOYMENT is a genuine pre-B1 orphan and is reaped
        // (the kind-branched predicate keeps the deployment "null OR expired" arm).
        var id = await SeedDeploymentAsync();
        await SetRunningAsync(id, leaseUntil: null, claimedBy: null);
        var (job, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Failed);
        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.DeploymentInterrupted && e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Live_lease_running_deployment_is_never_touched()
    {
        // The blue-green guarantee: a draining slot keeps renewing its lease;
        // the freshly booted slot's reconciler must leave its runs alone.
        var id = await SeedDeploymentAsync();
        await SetRunningAsync(id, leaseUntil: DateTimeOffset.UtcNow.AddMinutes(4), claimedBy: "kraken:draining-slot");
        var (job, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Running, "a live lease means a live owner — hands off");
        audit.Entries.Should().NotContain(e => e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task PendingOfflineResult_is_never_reconciled()
    {
        var id = await SeedDeploymentAsync();
        await using (var db = postgres.CreateContext())
        {
            await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.PendingOfflineResult)
                    .SetProperty(t => t.CreatedUtc, DateTimeOffset.UtcNow.AddDays(-3)));
        }
        var (job, tasks, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        var signalled = new List<Guid>();
        while (tasks.Reader.TryRead(out var item))
        {
            signalled.Add(item.Id);
        }
        signalled.Should().NotContain(id,
            "a task parked awaiting an out-of-band offline result is neither stranded nor orphaned");
        audit.Entries.Should().NotContain(e => e.SubjectId == id.ToString());
        await using var check = postgres.CreateContext();
        (await check.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.PendingOfflineResult);
    }

    // ── Runbook runs through the unified reconciler (D1) ────────────────────

    [Fact]
    public async Task Runbook_run_with_expired_lease_is_failed_with_interrupted_audit()
    {
        // D1: a runbook run now holds a live lease for the whole orchestration, so
        // an EXPIRED (non-null) lease means the orchestrating process died — the
        // SAME arm-3 reconcile that covers deployments fails it, kind-branching the
        // audit to RunbookRun.Interrupted.
        var id = await SeedRunbookRunAsync();
        await SetRunbookRunningAsync(id,
            leaseUntil: DateTimeOffset.UtcNow.AddMinutes(-1),
            claimedBy: "kraken:dead-node",
            startedAgo: TimeSpan.FromMinutes(5));
        var (job, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var run = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == id);
        run.Status.Should().Be(DeploymentStatus.Failed);
        run.CompletedUtc.Should().NotBeNull();
        run.LeaseUntil.Should().BeNull();

        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.RunbookRunInterrupted && e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Null_lease_running_runbook_run_is_failed_too()
    {
        // D1 Phase 3: the transition-era exemption is gone — a null-lease Running
        // runbook run can no longer be a legacy hand-off (that model, and arm 4
        // that drained it, are deleted). Nothing owns such a row, so the SAME
        // "null OR expired" orphan reconcile that covers deployments fails it.
        var id = await SeedRunbookRunAsync();
        await SetRunbookRunningAsync(id,
            leaseUntil: null, claimedBy: null, startedAgo: TimeSpan.FromMinutes(10));
        var (job, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Failed,
                "post-Phase-3 every live orchestration holds a lease — a null-lease " +
                "Running run is ownerless and unresumable");
        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.RunbookRunInterrupted && e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Live_lease_runbook_run_is_never_touched()
    {
        // Mid-orchestration with a healthy renewing lease — hands off,
        // exactly like deployments.
        var id = await SeedRunbookRunAsync();
        await SetRunbookRunningAsync(id,
            leaseUntil: DateTimeOffset.UtcNow.AddMinutes(4),
            claimedBy: "kraken:live-node",
            startedAgo: TimeSpan.FromHours(3)); // old StartedUtc must NOT matter while leased
        var (job, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Running);
        audit.Entries.Should().NotContain(e => e.SubjectId == id.ToString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private (ScheduledDeploymentDispatchJob Job,
             Channel<TenantWorkItem> Tasks,
             TestAuditLog Audit) NewJob()
    {
        // D1: one shared task channel carries both kinds.
        var tasks = Channel.CreateUnbounded<TenantWorkItem>();
        var audit = new TestAuditLog();
        var job = new ScheduledDeploymentDispatchJob(
            postgres, tasks, TimeProvider.System,
            new DisabledAccountContext(), audit,
            NullLogger<ScheduledDeploymentDispatchJob>.Instance);
        return (job, tasks, audit);
    }

    private async Task<Guid> SeedDeploymentAsync()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"re-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("rec-t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        return await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
    }

    private async Task AgeAsync(Guid id, TimeSpan createdAgo)
    {
        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(
                t => t.CreatedUtc, DateTimeOffset.UtcNow - createdAgo));
    }

    /// <summary>Seeds a minimum-viable bare RunbookRun (project + env + runbook +
    /// Queued run, no targets/steps) via the shared harness shell. The reconciler
    /// tests then flip it Running (SetRunbookRunningAsync) and exercise the reap arms.</summary>
    private async Task<Guid> SeedRunbookRunAsync()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rbp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rbe-{Guid.NewGuid():N}"[..16]);
        return await harness.SeedRunbookRunAsync(project.Id, env.Id);
    }

    private async Task SetRunbookRunningAsync(
        Guid id, DateTimeOffset? leaseUntil, string? claimedBy, TimeSpan startedAgo)
    {
        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, DeploymentStatus.Running)
                .SetProperty(t => t.StartedUtc, DateTimeOffset.UtcNow - startedAgo)
                .SetProperty(t => t.LeaseUntil, leaseUntil)
                .SetProperty(t => t.ClaimedBy, claimedBy)
                .SetProperty(t => t.CreatedUtc, DateTimeOffset.UtcNow - startedAgo));
    }

    private async Task SetRunningAsync(Guid id, DateTimeOffset? leaseUntil, string? claimedBy)
    {
        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, DeploymentStatus.Running)
                .SetProperty(t => t.StartedUtc, DateTimeOffset.UtcNow.AddMinutes(-30))
                .SetProperty(t => t.LeaseUntil, leaseUntil)
                .SetProperty(t => t.ClaimedBy, claimedBy)
                .SetProperty(t => t.CreatedUtc, DateTimeOffset.UtcNow.AddMinutes(-40)));
    }
}
