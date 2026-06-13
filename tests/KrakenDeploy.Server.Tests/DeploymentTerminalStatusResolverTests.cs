using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Locks the mode-aware rolling-deployment terminal-status decision. In Atomic
/// mode a Required-step drop is a hard Failed (consistency required); in
/// BestEffort mode a partial drop / soft failure with survivors completing is
/// SucceededWithWarnings (partial progress is the intended outcome).
/// </summary>
public sealed class DeploymentTerminalStatusResolverTests
{
    private static DeploymentStatus Resolve(
        DeploymentFailureMode mode, bool hasFailed, bool requiredDropped,
        int droppedCount, int softFailedCount)
        => DeploymentTerminalStatusResolver.Resolve(
            mode, hasFailed, requiredDropped, droppedCount, softFailedCount);

    [Theory]
    [InlineData(DeploymentFailureMode.BestEffort)]
    [InlineData(DeploymentFailureMode.Atomic)]
    public void Clean_run_is_Succeeded(DeploymentFailureMode mode)
        => Resolve(mode, hasFailed: false, requiredDropped: false,
                   droppedCount: 0, softFailedCount: 0)
            .Should().Be(DeploymentStatus.Succeeded);

    [Theory]
    [InlineData(DeploymentFailureMode.BestEffort)]
    [InlineData(DeploymentFailureMode.Atomic)]
    public void Soft_failure_is_SucceededWithWarnings(DeploymentFailureMode mode)
        => Resolve(mode, hasFailed: false, requiredDropped: false,
                   droppedCount: 0, softFailedCount: 1)
            .Should().Be(DeploymentStatus.SucceededWithWarnings);

    [Fact]
    public void BestEffort_required_drop_with_survivors_is_SucceededWithWarnings()
        => Resolve(DeploymentFailureMode.BestEffort, hasFailed: false,
                   requiredDropped: true, droppedCount: 1, softFailedCount: 0)
            .Should().Be(DeploymentStatus.SucceededWithWarnings,
                "BestEffort treats a partial required drop as partial success");

    [Fact]
    public void Atomic_required_drop_is_Failed_even_with_survivors()
        => Resolve(DeploymentFailureMode.Atomic, hasFailed: true,
                   requiredDropped: true, droppedCount: 1, softFailedCount: 0)
            .Should().Be(DeploymentStatus.Failed,
                "Atomic treats any Required-step failure as a hard failure");

    [Fact]
    public void Atomic_agent_offline_only_drop_is_SucceededWithWarnings()
        // No REQUIRED step failed (offline drop only) → not a hard failure.
        => Resolve(DeploymentFailureMode.Atomic, hasFailed: true,
                   requiredDropped: false, droppedCount: 1, softFailedCount: 0)
            .Should().Be(DeploymentStatus.SucceededWithWarnings);
}
