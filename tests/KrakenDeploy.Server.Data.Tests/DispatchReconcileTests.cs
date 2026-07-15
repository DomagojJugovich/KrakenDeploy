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
/// in-process channel) and leave mid-run deployments stuck at Running with no
/// owner. The reconciler re-signals stale Queued tasks (both kinds, to the
/// right channel) and fails Running DEPLOYMENTS whose lease expired — while a
/// LIVE lease (a draining blue-green slot, a long step) is never touched, and
/// runbook runs / PendingOfflineResult rows are never reconciled at all.
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
        var (job, deployments, _, _) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        deployments.Reader.TryRead(out var item).Should().BeTrue(
            "a Queued task older than the grace lost its wake-up in a restart and must be re-signalled");
        item.Id.Should().Be(id);
    }

    [Fact]
    public async Task Fresh_queued_deployment_is_left_alone()
    {
        var id = await SeedDeploymentAsync(); // CreatedUtc = now, inside the grace window
        var (job, deployments, _, _) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        // Other tests in this class DB may legitimately be re-signalled; the
        // assertion is that THIS fresh row is not among them.
        var signalled = new List<Guid>();
        while (deployments.Reader.TryRead(out var item))
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
        var (job, _, _, audit) = NewJob();

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
        var id = await SeedDeploymentAsync();
        await SetRunningAsync(id, leaseUntil: null, claimedBy: null);
        var (job, _, _, audit) = NewJob();

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
        var (job, _, _, audit) = NewJob();

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
        var (job, deployments, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        var signalled = new List<Guid>();
        while (deployments.Reader.TryRead(out var item))
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

    // ── Helpers ────────────────────────────────────────────────────────────

    private (ScheduledDeploymentDispatchJob Job,
             Channel<TenantWorkItem> Deployments,
             RunbookRunChannel Runbooks,
             TestAuditLog Audit) NewJob()
    {
        var deployments = Channel.CreateUnbounded<TenantWorkItem>();
        var runbooks = new RunbookRunChannel();
        var audit = new TestAuditLog();
        var job = new ScheduledDeploymentDispatchJob(
            postgres, deployments, runbooks, TimeProvider.System,
            new DisabledAccountContext(), audit,
            NullLogger<ScheduledDeploymentDispatchJob>.Instance);
        return (job, deployments, runbooks, audit);
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
