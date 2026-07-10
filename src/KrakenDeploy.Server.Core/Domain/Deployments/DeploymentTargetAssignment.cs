using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// Many-to-many join between a <see cref="Deployment"/> and the
/// <see cref="DeploymentTarget"/>(s) it dispatches to — the SINGLE
/// authority for a deployment's target set (the transitional
/// <c>deployments.target_id</c> column was dropped in the 2026-07 schema
/// hardening). Exactly one row for classic single-target deployments;
/// N rows for rolling/parallel fan-out (rate-limited via
/// <c>Octopus.Action.MaxParallelism</c> on a <c>Kraken.StepGroup</c> in
/// the rolling-window phase).
///
/// <para>
/// Named <c>DeploymentTargetAssignment</c> to avoid colliding with
/// <see cref="DeploymentTarget"/> (the deployable machine entity, which
/// owns the <c>deployment_targets</c> table). The DB table for the
/// assignment join is <c>deployment_target_assignments</c>.
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

    /// <summary>When the target was assigned to the deployment. Carries
    /// assignment ORDER as well as time: <c>DeploymentService.CreateAsync</c>
    /// stamps strictly increasing values so "first-assigned" (the canonical
    /// target for server-wave machine variables) survives the round-trip —
    /// see <see cref="DeploymentTargetSetExtensions.ResolvedTargets"/>.</summary>
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
}
