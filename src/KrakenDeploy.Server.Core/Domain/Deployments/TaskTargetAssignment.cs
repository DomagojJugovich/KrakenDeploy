using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// Many-to-many join between a <see cref="ServerTask"/> and the
/// <see cref="DeploymentTarget"/>(s) it dispatches to (formerly
/// <c>DeploymentTargetAssignment</c>) — the SINGLE authority for a task's target
/// set. Exactly one row for classic single-target execution; N rows for
/// rolling/parallel fan-out. Shared by deployments and runbook runs.
///
/// <para>
/// <strong>Scope:</strong> Space scope inherits through <see cref="TaskId"/> — the
/// task row carries <see cref="ServerTask.SpaceId"/>, so the join carries no
/// <c>SpaceId</c> of its own (a stamped <c>space_id</c> + composite Space FKs land
/// in the later composite-FK hardening step).
/// </para>
/// </summary>
public class TaskTargetAssignment
{
    public Guid TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    public Guid TargetId { get; set; }
    public DeploymentTarget? Target { get; set; }

    /// <summary>When the target was assigned. Carries assignment ORDER as well as
    /// time: creation stamps strictly increasing values so "first-assigned" (the
    /// canonical target for server-wave machine variables) survives the round-trip.</summary>
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
}
