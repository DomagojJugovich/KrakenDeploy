using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Locks the rolling-deployment terminal-status decision (CAT 4). The key
/// property: a Required-step failure that dropped a target is a hard failure
/// (Failed), never masked behind SucceededWithWarnings — even when other targets
/// survived. Softer partial-success (non-required failure, agent-offline drop)
/// stays SucceededWithWarnings.
/// </summary>
public sealed class DeploymentTerminalStatusResolverTests
{
    [Fact]
    public void Clean_run_is_Succeeded()
        => DeploymentTerminalStatusResolver.Resolve(
            hasFailed: false, requiredStepDropped: false, droppedTargetCount: 0)
            .Should().Be(DeploymentStatus.Succeeded);

    [Fact]
    public void Non_required_failure_is_SucceededWithWarnings()
        => DeploymentTerminalStatusResolver.Resolve(
            hasFailed: true, requiredStepDropped: false, droppedTargetCount: 0)
            .Should().Be(DeploymentStatus.SucceededWithWarnings);

    [Fact]
    public void Agent_offline_only_drop_is_SucceededWithWarnings()
        => DeploymentTerminalStatusResolver.Resolve(
            hasFailed: true, requiredStepDropped: false, droppedTargetCount: 2)
            .Should().Be(DeploymentStatus.SucceededWithWarnings);

    [Fact]
    public void Required_step_drop_is_Failed_even_with_survivors()
        => DeploymentTerminalStatusResolver.Resolve(
            hasFailed: true, requiredStepDropped: true, droppedTargetCount: 1)
            .Should().Be(DeploymentStatus.Failed,
                "a Required-step failure on any target is a hard failure, not a warning");

    [Fact]
    public void Required_step_drop_outranks_a_large_survivor_count()
        => DeploymentTerminalStatusResolver.Resolve(
            hasFailed: true, requiredStepDropped: true, droppedTargetCount: 9)
            .Should().Be(DeploymentStatus.Failed);
}
