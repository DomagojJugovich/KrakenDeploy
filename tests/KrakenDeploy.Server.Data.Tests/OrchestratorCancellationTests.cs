using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// End-to-end orchestrator coverage for deployment cancellation
/// (<see cref="DeploymentStatus.Cancelled"/>). Two guarantees are asserted
/// against persisted state via the <see cref="OrchestratorTestHarness"/>:
/// <list type="number">
///   <item><b>Cancelling a pending/queued deployment prevents dispatch</b> —
///     the worker's dequeue-skip check bails before transitioning to Running,
///     so no wave is ever sent to an agent.</item>
///   <item><b>Cancelling a running deployment stops at the next wave
///     boundary</b> — the wave already dispatched to an agent runs to
///     completion (the agent protocol has no in-flight abort), but no further
///     wave starts, and the terminal status stays <c>Cancelled</c> (never
///     overwritten by the finaliser).</item>
/// </list>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OrchestratorCancellationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Cancelling_a_queued_deployment_prevents_dispatch()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0",
            StepBuilder.Script("s1"), StepBuilder.Script("s2"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        var agent = harness.ConnectFakeAgent(targets[0]);

        // Operator cancels while the deployment is still Queued (before dispatch).
        await harness.CancelDeploymentAsync(deploymentId);

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Cancelled);
        deployment.StartedUtc.Should().BeNull(
            because: "a cancelled-while-queued deployment must never transition to Running");
        deployment.CompletedUtc.Should().NotBeNull(
            because: "CancelAsync stamps the completion time");

        agent.WaveCount.Should().Be(0,
            because: "the agent must never receive a sub-plan for a cancelled deployment");
        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().BeEmpty(because: "no wave should have been dispatched");
    }

    [Fact]
    public async Task Cancelling_a_running_deployment_stops_at_the_next_wave_boundary_as_Cancelled()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        // Two target-side steps → two sequential waves (default StartTrigger),
        // giving exactly one between-wave boundary for cancellation to land on.
        var release = await harness.SeedReleaseAsync(project.Id, "1.0",
            StepBuilder.Script("wave1"), StepBuilder.Script("wave2"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        var agent = harness.ConnectFakeAgent(targets[0]);
        // Cancel right after the first wave completes on the agent. The
        // orchestrator's next-boundary check must then halt before dispatching
        // wave 2, leaving the terminal Cancelled status in place.
        agent.AfterWaveAsync = async wave =>
        {
            if (wave == 1)
            {
                await harness.CancelDeploymentAsync(deploymentId);
            }
        };

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Cancelled,
            because: "the finaliser must not overwrite the Cancelled status set mid-run");
        deployment.CompletedUtc.Should().NotBeNull();

        agent.WaveCount.Should().Be(1,
            because: "wave 1 ran; wave 2 must never be dispatched after cancellation");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle(
            because: "only the first wave's step executed before cancellation");
        outcomes[0].StepName.Should().Be("wave1");
    }

    [Fact]
    public async Task Cancelling_during_the_final_wave_is_not_overwritten_by_the_finaliser()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        // Single wave: the cancel lands after the only wave completes, so the
        // worker runs straight into finalisation. DeploymentTerminalStatusResolver
        // would compute Succeeded — the finaliser's guard must let Cancelled win.
        var release = await harness.SeedReleaseAsync(project.Id, "1.0",
            StepBuilder.Script("only-wave"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.AfterWaveAsync = async _ => await harness.CancelDeploymentAsync(deploymentId);

        await harness.RunDeploymentAsync(deploymentId);

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Cancelled,
            because: "the terminal-status finaliser must not overwrite a cancellation " +
                     "that landed while the final wave was completing");
    }
}
