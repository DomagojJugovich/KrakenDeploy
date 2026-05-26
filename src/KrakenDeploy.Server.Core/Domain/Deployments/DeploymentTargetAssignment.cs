using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// M-RollingDeployments groundwork — many-to-many join between a
/// <see cref="Deployment"/> and the <see cref="DeploymentTarget"/>(s)
/// it dispatches to. Pre-this-milestone <see cref="Deployment.TargetId"/>
/// held the single target each deployment ran against; the join lifts
/// that restriction so a deployment can fan out across N targets
/// (parallel-on-all-at-once when the orchestrator rewrite lands; rate-
/// limited via <c>Octopus.Action.MaxParallelism</c> on a
/// <c>Kraken.StepGroup</c> in the rolling-window phase).
///
/// <para>
/// Named <c>DeploymentTargetAssignment</c> to avoid colliding with
/// <see cref="DeploymentTarget"/> (the deployable machine entity, which
/// owns the <c>deployment_targets</c> table). The DB table for the
/// assignment join is <c>deployment_target_assignments</c>.
/// </para>
///
/// <para>
/// <strong>Migration:</strong> the upgrade migration backfills this
/// table from every existing <see cref="Deployment.TargetId"/> so old
/// rows continue to work as single-target deployments through the join.
/// The legacy <see cref="Deployment.TargetId"/> column is kept for
/// reads during the transition — code paths that haven't yet been
/// upgraded to read the join see the same value they always did.
/// </para>
///
/// <para>
/// <strong>Scope:</strong> Space scope inherits through
/// <see cref="DeploymentId"/> — the Deployment row carries
/// <see cref="Deployment.SpaceId"/>, so the join doesn't need its own.
/// (Same pattern as <see cref="DeploymentOutputVariable"/> and
/// <see cref="DeploymentStepOutcome"/>.)
/// </para>
/// </summary>
public class DeploymentTargetAssignment
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;

    public Guid TargetId { get; set; }
    public DeploymentTarget? Target { get; set; }

    /// <summary>When the target was assigned to the deployment.
    /// Useful for audit / forensic review when an operator changes
    /// the target set mid-flight (post-rolling-rewrite — pre-rewrite
    /// the join is immutable after creation).</summary>
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
}
