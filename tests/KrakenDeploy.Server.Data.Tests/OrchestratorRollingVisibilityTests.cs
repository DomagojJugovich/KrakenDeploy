using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// D3 RIDER — rolling-window visibility. When a rolling <c>Kraken.StepGroup</c>
/// is present but its explicit cap cannot be used (malformed / non-positive
/// MaxParallelism from imported or legacy data, or a wave spanning multiple
/// rolling groups), the orchestrator must surface it: a warning in the task log
/// AND a <c>Deployment.RollingBatchingDisabled</c> audit event, rather than
/// silently falling back to the default target-wave cap. The typed int column kills the
/// malformed case at save time going forward; this covers the runtime path for
/// imported/legacy data.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OrchestratorRollingVisibilityTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Malformed_rolling_window_emits_batching_disabled_audit()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rv-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rve-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1", "t2");

        // A rolling group with a NON-POSITIVE window (as imported/legacy data
        // could carry) — the resolver classifies it Malformed and disables
        // batching. Two target-side children so the group actually wraps a wave.
        var group = StepBuilder.StepGroup("Rolling", maxParallelism: 0);
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            group,
            StepBuilder.Script("deploy-a").InGroup(group.Id),
            StepBuilder.Script("deploy-b").InGroup(group.Id));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        foreach (var t in targets) { harness.ConnectFakeAgent(t); }

        await harness.RunDeploymentAsync(deploymentId);

        // The deployment still completes using the Engine default cap, but the
        // unusable explicit rolling cap is audible.
        var dep = await harness.GetServerTaskAsync(deploymentId);
        dep.Status.Should().Be(DeploymentStatus.Succeeded);

        var events = await harness.GetAuditEventTypesAsync(deploymentId);
        events.Should().Contain(AuditEventType.DeploymentRollingBatchingDisabled,
            because: "a rolling group is present but its window is non-positive, " +
                      "so its fallback to default batching must be surfaced");
    }

    [Fact]
    public async Task Valid_rolling_window_does_not_emit_batching_disabled_audit()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rv-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rve-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1", "t2");

        var group = StepBuilder.StepGroup("Rolling", maxParallelism: 1);
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            group,
            StepBuilder.Script("deploy-a").InGroup(group.Id));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        foreach (var t in targets) { harness.ConnectFakeAgent(t); }

        await harness.RunDeploymentAsync(deploymentId);

        var dep = await harness.GetServerTaskAsync(deploymentId);
        dep.Status.Should().Be(DeploymentStatus.Succeeded);

        var events = await harness.GetAuditEventTypesAsync(deploymentId);
        events.Should().NotContain(AuditEventType.DeploymentRollingBatchingDisabled,
            because: "a positive window resolves cleanly — no batching-disabled warning");
    }
}
