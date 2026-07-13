using KrakenDeploy.Server.Core.Domain.Common;
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
/// <strong>Scope:</strong> Space-scoped. It carries a stamped <see cref="SpaceId"/>
/// and composite FKs <c>(space_id, task_id)</c> / <c>(space_id, target_id)</c> so a
/// task can only be assigned to a target in the same Space.
/// </para>
/// </summary>
public class TaskTargetAssignment : ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    public Guid TargetId { get; set; }
    public DeploymentTarget? Target { get; set; }

    /// <summary>When the target was assigned. Carries assignment ORDER as well as
    /// time: creation stamps strictly increasing values so "first-assigned" (the
    /// canonical target for server-wave machine variables) survives the round-trip.</summary>
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
}
