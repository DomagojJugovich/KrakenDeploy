using FluentAssertions;
using KrakenDeploy.Platform.Releases;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// BG1 item 10 — the deployment worker's drain claim gate (grill B1): a slot
/// whose blue-green release is Draining must stop CLAIMING new work, or a
/// cookie-pinned user creating work there keeps refilling the drain gauge (the
/// create-time enqueue wakes THAT process) and new deployments execute on OLD
/// code post-flip. These tests pin the three behaviours the gate must have:
/// refuse a NEW top-level claim, leave it Queued for the Active release to pick
/// up, and still let a child of an already-claimed parent claim locally.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DrainClaimGateTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private sealed class FakeSlotDrainGuard : ISlotDrainGuard
    {
        public bool Draining { get; set; }

        public Task<bool> IsOwnReleaseDrainingAsync(CancellationToken ct = default)
            => Task.FromResult(Draining);
    }

    [Fact]
    public async Task Draining_slot_refuses_a_new_claim_and_leaves_it_queued()
    {
        var guard = new FakeSlotDrainGuard { Draining = true };
        await using var draining = new OrchestratorTestHarness(postgres, slotDrainGuard: guard);
        var id = await SeedQueuedDeploymentAsync(draining);

        await draining.RunDeploymentAsync(id);

        await using var db = postgres.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == id);
        task.Status.Should().Be(DeploymentStatus.Queued,
            "a draining slot must refuse the claim and leave the row for the Active release");
        task.StartedUtc.Should().BeNull("the refused task never started here");
    }

    [Fact]
    public async Task Active_release_claims_the_task_a_draining_slot_refused()
    {
        var guard = new FakeSlotDrainGuard { Draining = true };
        await using var draining = new OrchestratorTestHarness(postgres, slotDrainGuard: guard);
        var id = await SeedQueuedDeploymentAsync(draining);

        // The draining slot's create-time wake-up fires first and is refused…
        await draining.RunDeploymentAsync(id);
        await using (var check = postgres.CreateContext())
        {
            (await StatusOf(check, id)).Should().Be(DeploymentStatus.Queued);
        }

        // …and the ACTIVE release's re-signal (a worker whose guard says "not
        // draining" — the OnPrem-shaped default harness models it) claims it.
        await using var active = new OrchestratorTestHarness(postgres);
        await active.RunDeploymentAsync(id);

        await using var db = postgres.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == id);
        task.Status.Should().NotBe(DeploymentStatus.Queued,
            "the active release must claim the task the draining slot refused");
        task.StartedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Child_of_a_claimed_parent_still_claims_on_the_draining_slot()
    {
        // Children of a parent already running on the draining slot MUST claim
        // there — the parent's WaitForChildAsync would otherwise strand behind a
        // child that can never run (the same exemption the maintenance gate and
        // the E3 NodeTaskGate bypass make; IsContinuationOfClaimedParent).
        var guard = new FakeSlotDrainGuard { Draining = true };
        await using var draining = new OrchestratorTestHarness(postgres, slotDrainGuard: guard);

        var parentId = await SeedQueuedDeploymentAsync(draining);
        var childId = await SeedQueuedDeploymentAsync(draining);

        await using (var setup = postgres.CreateContext())
        {
            // The parent is in-flight on THIS slot (claimed before the drain began).
            await ServerTaskLease.TryClaimAsync(setup, parentId, TimeProvider.System);
            await setup.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == childId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ParentTaskId, parentId));
        }

        await draining.RunDeploymentAsync(childId);

        await using var db = postgres.CreateContext();
        var child = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == childId);
        child.Status.Should().NotBe(DeploymentStatus.Queued,
            "a continuation of a claimed parent is exempt from the drain gate");
        child.StartedUtc.Should().NotBeNull();
    }

    // ── Helpers (mirror ServerTaskLeaseTests) ─────────────────────────────────

    private static async Task<Guid> SeedQueuedDeploymentAsync(OrchestratorTestHarness harness)
    {
        var project = await harness.SeedProjectAsync($"dg-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"dg-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync($"dg-{Guid.NewGuid():N}"[..12]);
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        return await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
    }

    private static async Task<DeploymentStatus> StatusOf(KrakenDbContext db, Guid id)
        => await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == id)
            .Select(t => t.Status)
            .FirstAsync();
}
