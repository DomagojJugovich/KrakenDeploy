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
    public void A_rejected_intervention_is_Failed_in_every_mode(DeploymentFailureMode mode)
        // WP3 — a human refused the change, so however cleanly the cleanup waves ran
        // afterwards the verdict is Failed. hasFailed alone resolves
        // SucceededWithWarnings, which is exactly the wrong verdict for a refusal, so
        // this is a separate input rather than a fold into the existing flag.
        => DeploymentTerminalStatusResolver.Resolve(
                mode, hasFailed: true, requiredStepDropped: false,
                droppedTargetCount: 0, softFailedCount: 0,
                interventionRejected: true)
            .Should().Be(DeploymentStatus.Failed);

    [Theory]
    [InlineData(DeploymentFailureMode.BestEffort)]
    [InlineData(DeploymentFailureMode.Atomic)]
    public void A_rejected_intervention_outranks_an_otherwise_clean_run(DeploymentFailureMode mode)
        // Guards the ordering: the rejection arm must sit ABOVE the "no degradation ->
        // Succeeded" fall-through, or a rejection with no other failure signal would
        // report success.
        => DeploymentTerminalStatusResolver.Resolve(
                mode, hasFailed: false, requiredStepDropped: false,
                droppedTargetCount: 0, softFailedCount: 0,
                interventionRejected: true)
            .Should().Be(DeploymentStatus.Failed);

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
