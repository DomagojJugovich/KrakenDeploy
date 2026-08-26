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
    /// <param name="interventionRejected">
    /// WP3 — a manual-intervention gate was rejected (or timed out). Unconditionally
    /// <see cref="DeploymentStatus.Failed"/>, in EVERY failure mode: a human said no,
    /// so however cleanly the cleanup steps ran afterwards, the task did not do what
    /// it was asked to. This cannot be folded into <paramref name="hasFailed"/> —
    /// that resolves to <see cref="DeploymentStatus.SucceededWithWarnings"/>, exactly
    /// the wrong verdict for a refused change. It is a separate input rather than an
    /// early <c>FailAsync</c> at the gate because the run must CONTINUE past the gate
    /// to execute its <c>Failure</c>/<c>Always</c> cleanup steps.
    /// </param>
    /// <param name="resumeInvalidated">
    /// WP3-c — the pause checkpoint no longer matched the rebuilt wave partition
    /// (wave count changed, or a wave's execution side flipped) and the run failed
    /// THROUGH the wave loop so <c>Failure</c>/<c>Always</c> cleanup could execute.
    /// Unconditionally <see cref="DeploymentStatus.Failed"/> for the same reason as
    /// <paramref name="interventionRejected"/>: however cleanly the cleanup ran, the
    /// approved work was not done.
    /// </param>
    public static DeploymentStatus Resolve(
        DeploymentFailureMode mode,
        bool hasFailed,
        bool requiredStepDropped,
        int droppedTargetCount,
        int softFailedCount,
        bool interventionRejected = false,
        bool resumeInvalidated = false)
    {
        // WP3: a rejected approval gate is a hard failure regardless of mode.
        // WP3-c: so is an invalidated resume checkpoint.
        if (interventionRejected || resumeInvalidated)
        {
            return DeploymentStatus.Failed;
        }

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
