using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B1/T1-2 — exactly ONE dispatch path per deployment. CreateAsync must
/// normalize a due/past <c>scheduledFor</c> to <c>null</c> (immediate path)
/// instead of persisting it alongside an immediate enqueue — the persisted past
/// value was what let the minutely dispatch job re-enqueue the same deployment
/// during the worker's prep window (double-dispatch). The dispatch job itself is
/// now a pure idempotent wake-up: it never mutates rows (the worker's atomic
/// claim clears <c>ScheduledFor</c>), so a crash mid-job can no longer strand
/// rows as <c>Queued, ScheduledFor=null</c>.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DeploymentSchedulingTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Past_scheduledFor_takes_the_immediate_path_only()
    {
        var (releaseId, envId, targetId) = await SeedGraphAsync();
        var queue = Channel.CreateUnbounded<TenantWorkItem>();
        var svc = NewService(queue);

        var deployment = await svc.CreateAsync(
            releaseId, envId, targetId,
            initiator: TaskInitiator.Scheduled("scheduling-test"),
            caller: CallerAuthorization.System,
            scheduledFor: DateTimeOffset.UtcNow.AddMinutes(-10));

        deployment.ScheduledFor.Should().BeNull(
            "a due/past schedule is normalized to the immediate path — persisting it " +
            "would let the minutely job re-enqueue the same deployment");
        queue.Reader.TryRead(out var item).Should().BeTrue("the immediate path enqueues once");
        item.Id.Should().Be(deployment.Id);
        queue.Reader.TryRead(out _).Should().BeFalse("exactly one wake-up is written");
    }

    [Fact]
    public async Task Future_scheduledFor_persists_and_does_not_enqueue()
    {
        var (releaseId, envId, targetId) = await SeedGraphAsync();
        var queue = Channel.CreateUnbounded<TenantWorkItem>();
        var svc = NewService(queue);
        var when = DateTimeOffset.UtcNow.AddHours(2);

        var deployment = await svc.CreateAsync(
            releaseId, envId, targetId,
            initiator: TaskInitiator.Scheduled("scheduling-test"),
            caller: CallerAuthorization.System,
            scheduledFor: when);

        deployment.ScheduledFor.Should().Be(when);
        queue.Reader.TryRead(out _).Should().BeFalse(
            "the scheduled-dispatch job is the sole dispatcher for future-dated deployments");
    }

    [Fact]
    public async Task Dispatch_job_is_a_pure_wakeup_and_the_claim_ends_the_signalling()
    {
        var (releaseId, envId, targetId) = await SeedGraphAsync();
        var queue = Channel.CreateUnbounded<TenantWorkItem>();
        var svc = NewService(queue);

        // A future-dated deployment whose time then "arrives".
        var deployment = await svc.CreateAsync(
            releaseId, envId, targetId,
            initiator: TaskInitiator.Scheduled("scheduling-test"),
            caller: CallerAuthorization.System,
            scheduledFor: DateTimeOffset.UtcNow.AddHours(1));
        await using (var db = postgres.CreateContext())
        {
            await db.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == deployment.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    t => t.ScheduledFor, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        var job = new ScheduledDeploymentDispatchJob(
            postgres, queue, TimeProvider.System,
            new DisabledAccountContext(), new NullAuditLog(),
            NullLogger<ScheduledDeploymentDispatchJob>.Instance);

        // Two overlapping/retried job runs → two wake-ups (at-least-once is fine;
        // the claim de-duplicates), and the job itself mutates NOTHING — a crash
        // between its query and the channel writes can strand nothing.
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);
        queue.Reader.TryRead(out _).Should().BeTrue();
        queue.Reader.TryRead(out _).Should().BeTrue("the job never claims; every run re-signals");
        await using (var db = postgres.CreateContext())
        {
            (await db.ServerTasks.IgnoreQueryFilters()
                    .Where(t => t.Id == deployment.Id)
                    .Select(t => t.ScheduledFor)
                    .FirstAsync())
                .Should().NotBeNull("the job is read-only; only the claim clears the schedule");
        }

        // The worker's claim ends the signalling: ScheduledFor cleared, and the
        // next job run no longer matches the row.
        await using (var db = postgres.CreateContext())
        {
            (await ServerTaskLease.TryClaimAsync(db, deployment.Id, TimeProvider.System))
                .Should().BeTrue();
        }
        await job.ExecuteAsync(CancellationToken.None);
        queue.Reader.TryRead(out _).Should().BeFalse("a claimed (Running) row is never re-signalled");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private DeploymentService NewService(Channel<TenantWorkItem> queue) =>
        new(postgres, queue, TimeProvider.System,
            new DisabledAccountContext(), new PermissionEvaluator(postgres, TimeProvider.System));

    private async Task<(Guid ReleaseId, Guid EnvId, Guid TargetId)> SeedGraphAsync()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"se-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("sched-t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        return (release.Id, env.Id, targets[0].Id);
    }
}
