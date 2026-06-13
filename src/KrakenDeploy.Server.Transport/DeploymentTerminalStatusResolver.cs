using KrakenDeploy.Server.Core.Domain.Deployments;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Resolves a rolling deployment's terminal status from the per-target outcome
/// and the deployment's <see cref="DeploymentFailureMode"/>. Only used once at
/// least one target SURVIVED — the all-targets-dropped case fails the deployment
/// earlier.
/// <para>
/// In <see cref="DeploymentFailureMode.Atomic"/>, a Required-step failure on ANY
/// target is a hard <see cref="DeploymentStatus.Failed"/> — survivors completing
/// does not make a consistency-required deployment a success. In
/// <see cref="DeploymentFailureMode.BestEffort"/>, a partial drop or soft failure
/// where other targets completed is <see cref="DeploymentStatus.SucceededWithWarnings"/>
/// (the yellow-badge state) — partial progress is the intended outcome.
/// </para>
/// </summary>
public static class DeploymentTerminalStatusResolver
{
    /// <param name="mode">The deployment's failure-handling mode.</param>
    /// <param name="hasFailed">The deployment-global failing flag (a server-level
    /// non-required failure, or — in Atomic mode — any target failure).</param>
    /// <param name="requiredStepDropped">A target dropped because a <em>Required</em> step failed.</param>
    /// <param name="droppedTargetCount">How many targets dropped out (any reason).</param>
    /// <param name="softFailedCount">How many surviving targets had a non-required failure.</param>
    public static DeploymentStatus Resolve(
        DeploymentFailureMode mode,
        bool hasFailed,
        bool requiredStepDropped,
        int droppedTargetCount,
        int softFailedCount)
    {
        // Atomic: a Required failure anywhere is a hard, masking-free failure.
        if (mode == DeploymentFailureMode.Atomic && requiredStepDropped)
        {
            return DeploymentStatus.Failed;
        }

        // Any degradation that left survivors completing → warnings; else clean.
        return hasFailed || droppedTargetCount > 0 || softFailedCount > 0
            ? DeploymentStatus.SucceededWithWarnings
            : DeploymentStatus.Succeeded;
    }
}
