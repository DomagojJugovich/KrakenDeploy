using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
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

        first.Should().BeTrue("the first wake-up claims the row");
        second.Should().BeFalse("a duplicate wake-up must lose the claim and bail");

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
            .Should().BeFalse("cancel-before-dispatch must win over any wake-up");
    }

    [Fact]
    public async Task Renew_extends_a_running_lease_and_refuses_terminal_rows()
    {
        var id = await SeedQueuedDeploymentAsync();

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System)).Should().BeTrue();

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

    [Fact]
    public async Task Release_clears_the_lease_fields()
    {
        var id = await SeedQueuedDeploymentAsync();

        await using var db = postgres.CreateContext();
        (await ServerTaskLease.TryClaimAsync(db, id, TimeProvider.System)).Should().BeTrue();

        await ServerTaskLease.ReleaseAsync(db, id);

        var task = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == id);
        task.ClaimedBy.Should().BeNull();
        task.LeaseUntil.Should().BeNull();
        task.Status.Should().Be(DeploymentStatus.Running, "release never touches status");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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
}
