using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// E2 (execution-engine audit 2026-07-16) — the orchestrator's ownership
/// predicate must be re-checked between ROLLING BATCHES (not only at dequeue and
/// wave boundaries), and a lost dispatch lease must tear the orchestration down
/// instead of letting it run leaseless.
/// <list type="number">
///   <item><b>Cancel mid-rolling stops before the next batch</b> — pre-E2 a
///     zombie orchestration kept dispatching batch after batch after the row was
///     flipped; the between-batch ownership check now halts it.</item>
///   <item><b>A simulated lease loss cancels the orchestration</b> — the lease
///     renewal signals a CTS the worker links into its cancellation, so a run
///     parked on an agent that never reports tears down when the reconciler
///     reclaims its lease, rather than hanging on the wave deadline.</item>
/// </list>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OrchestratorOwnershipTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Cancelling_mid_rolling_stops_before_the_next_batch()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rc-{Guid.NewGuid():N}"[..14]);
        var env = await harness.SeedEnvironmentAsync($"re-{Guid.NewGuid():N}"[..14]);
        var targets = await harness.SeedTargetsAsync("rt1", "rt2", "rt3");

        // MaxParallelism=1 over 3 targets → 3 sequential batches of one target,
        // giving two between-batch boundaries for the cancel to land on.
        var group = StepBuilder.StepGroup("rolling-group", maxParallelism: 1);
        var child = StepBuilder.Script("deploy").InGroup(group.Id);
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", group, child);
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        var a1 = harness.ConnectFakeAgent(targets[0]);
        var a2 = harness.ConnectFakeAgent(targets[1]);
        var a3 = harness.ConnectFakeAgent(targets[2]);

        // The first batch's target completes, then the operator cancels. The
        // between-batch ownership check must halt before dispatching batch 2/3.
        a1.AfterWaveAsync = async _ => await harness.CancelDeploymentAsync(deploymentId);

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Cancelled,
            because: "the finaliser must not overwrite the Cancelled status set mid-run");

        a1.WaveCount.Should().Be(1, because: "batch 1 (rt1) ran before the cancel");
        a2.WaveCount.Should().Be(0,
            because: "the between-batch ownership check must stop batch 2 (rt2) after the cancel");
        a3.WaveCount.Should().Be(0,
            because: "batch 3 (rt3) must never dispatch either");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Where(o => o.Outcome == StepOutcomeKind.Succeeded)
            .Select(o => o.TargetId)
            .Should().BeEquivalentTo([(Guid?)targets[0].Id],
                because: "only rt1's batch executed before the cancel stopped the roll");
    }

    [Fact]
    public async Task A_lost_lease_tears_the_orchestration_down_without_finalising()
    {
        // A 50ms renewal interval + a disabled disconnect monitor + the default
        // (1 h) wave deadline: the ONLY thing that can free a wave parked on an
        // agent that never reports is the lease-loss teardown. We park the wave,
        // simulate the reconciler failing the run (flip Running → Failed), and the
        // next renewal tick finds no Running row → fires LeaseLost → the wave await
        // cancels → the orchestration tears down.
        var engine = new EngineOptions
        {
            AgentDisconnectWaveGrace = TimeSpan.Zero,          // disable the disconnect monitor
            MaxTargetWaveDuration    = TimeSpan.FromHours(1),  // the deadline must NOT be what frees it
        };
        await using var harness = new OrchestratorTestHarness(
            postgres, engine, leaseRenewInterval: TimeSpan.FromMilliseconds(50));

        var project = await harness.SeedProjectAsync($"ll-{Guid.NewGuid():N}"[..14]);
        var env = await harness.SeedEnvironmentAsync($"le-{Guid.NewGuid():N}"[..14]);
        var targets = await harness.SeedTargetsAsync("lt1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("hang"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.NeverReport = true;   // wave dispatched, but the agent never completes it

        // Drive the orchestration on a background task — it will park on the wave.
        var run = Task.Run(() => harness.RunDeploymentAsync(deploymentId));

        // Wait until the wave has actually been dispatched (claim succeeded, plan
        // delivered) before simulating the reconciler.
        var dispatched = await WaitUntilAsync(
            () => agent.ReceivedPlans.Count >= 1, TimeSpan.FromSeconds(10));
        dispatched.Should().BeTrue("the wave must be in flight before the lease is lost");

        // Reconciler simulation: the lease expired and the run was failed as
        // orphaned. TryRenewAsync now matches no Running row → LeaseLost fires.
        await using (var db = harness.CreateContext())
        {
            await db.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == deploymentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.Failed)
                    .SetProperty(t => t.CompletedUtc, DateTimeOffset.UtcNow)
                    .SetProperty(t => t.ClaimedBy, (string?)null)
                    .SetProperty(t => t.LeaseUntil, (DateTimeOffset?)null));
        }

        // The orchestration must UNWIND promptly (well under the 1 h wave deadline).
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15)));
        completed.Should().BeSameAs(run,
            "a lost lease must tear the orchestration down, not leave it hanging on the wave deadline");
        await run;   // observe any exception (there must be none — the teardown is clean)

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed,
            because: "the reconciler's verdict stands — the torn-down orchestration must not overwrite it");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return predicate();
    }
}
