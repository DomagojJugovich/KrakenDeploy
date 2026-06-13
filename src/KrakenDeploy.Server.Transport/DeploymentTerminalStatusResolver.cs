using KrakenDeploy.Server.Core.Domain.Deployments;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Resolves a rolling deployment's terminal status from the per-target outcome.
/// Only used once at least one target SURVIVED — the all-targets-dropped case
/// fails the deployment earlier.
/// <para>
/// A Required-step failure on ANY target is a hard failure (<see cref="DeploymentStatus.Failed"/>),
/// not a warning: survivors completing does not make the deployment a success,
/// and labelling it <see cref="DeploymentStatus.SucceededWithWarnings"/> would
/// mask the failure behind a yellow badge. Softer partial-success conditions —
/// a non-required step failure, or a target dropped only because its agent went
/// offline — terminate as <see cref="DeploymentStatus.SucceededWithWarnings"/>.
/// </para>
/// </summary>
public static class DeploymentTerminalStatusResolver
{
    /// <param name="hasFailed">A non-required step failed, or a target dropped.</param>
    /// <param name="requiredStepDropped">A target dropped because a <em>Required</em> step failed.</param>
    /// <param name="droppedTargetCount">How many targets dropped out (any reason).</param>
    public static DeploymentStatus Resolve(
        bool hasFailed, bool requiredStepDropped, int droppedTargetCount)
    {
        if (requiredStepDropped)
        {
            return DeploymentStatus.Failed;
        }

        return hasFailed || droppedTargetCount > 0
            ? DeploymentStatus.SucceededWithWarnings
            : DeploymentStatus.Succeeded;
    }
}
