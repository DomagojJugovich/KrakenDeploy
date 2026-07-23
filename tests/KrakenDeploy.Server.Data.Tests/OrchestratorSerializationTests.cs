using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// F1 (project, environment, tenant) deployment serialization — the orchestrator
/// side. The claim-time semantics (blocked / different-tenant / null-tenant /
/// concurrent-race / runbook-exempt) are pinned in <see cref="ServerTaskLeaseTests"/>;
/// this covers the worker's PRE-GATE behaviour: a serialization-blocked deployment
/// stays <c>Queued</c> WITHOUT taking a <see cref="NodeTaskGate"/> slot, so a
/// deployment of a different key still runs on the node's capacity.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OrchestratorSerializationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Blocked_deployment_consumes_no_gate_slot_so_other_keys_still_run()
    {
        // Capacity 1: were a serialization-blocked deployment to hold the only
        // slot (e.g. by acquiring it and waiting for the running peer), the
        // unrelated deployment could never start. F1 evaluates the serialization
        // predicate BEFORE gate acquisition and returns without a slot, so the
        // other key proceeds on the free slot.
        var engine = new EngineOptions { MaxConcurrentTasks = 1 };
        await using var harness = new OrchestratorTestHarness(postgres, engine);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var env = await harness.SeedEnvironmentAsync($"ser-env-{tag}");

        // Key A — two deployments of the same (project, env). One is already
        // Running (seeded directly), so the second is serialization-blocked.
        var projectA = await harness.SeedProjectAsync($"ser-a-{tag}");
        var releaseA = await harness.SeedReleaseAsync(projectA.Id, "1.0", StepBuilder.Script("a-step"));
        var targetA = (await harness.SeedTargetsAsync($"ser-ta-{tag}"))[0];
        var running = await harness.CreateDeploymentAsync(releaseA.Id, env.Id, [targetA]);
        await MarkRunningAsync(running);
        var blocked = await harness.CreateDeploymentAsync(releaseA.Id, env.Id, [targetA]);

        // Key B — an unrelated deployment (different project) with a live agent
        // that resolves its target wave successfully.
        var projectB = await harness.SeedProjectAsync($"ser-b-{tag}");
        var releaseB = await harness.SeedReleaseAsync(projectB.Id, "1.0", StepBuilder.Script("b-step"));
        var targetB = (await harness.SeedTargetsAsync($"ser-tb-{tag}"))[0];
        harness.ConnectFakeAgent(targetB);
        var other = await harness.CreateDeploymentAsync(releaseB.Id, env.Id, [targetB]);

        await harness.StartWorkerAsync();
        await harness.EnqueueAsync(blocked);
        await harness.EnqueueAsync(other);

        // The unrelated deployment completes despite the single slot — proof the
        // blocked one never tied it up.
        var otherResult = await harness.WaitForTerminalAsync(other, TimeSpan.FromSeconds(30));
        otherResult.Status.Should().Be(DeploymentStatus.Succeeded,
            because: "a serialization-blocked deployment must not consume the node's only gate slot");

        // The blocked deployment is still Queued — the running peer never finished,
        // and the harness runs no reconciler, so nothing re-signals it here.
        await using var db = postgres.CreateContext();
        var blockedStatus = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == blocked).Select(t => t.Status).FirstAsync();
        blockedStatus.Should().Be(DeploymentStatus.Queued,
            because: "it stays Queued for the minutely re-signal while the peer runs");
    }

    private async Task MarkRunningAsync(Guid id)
    {
        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, DeploymentStatus.Running));
    }
}
