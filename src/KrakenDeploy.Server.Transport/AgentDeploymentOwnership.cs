using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Shared agent trust-boundary check: is the connecting agent's authenticated
/// target (its AgentJwt <c>NameIdentifier</c>) a participant in a given
/// <see cref="ServerTask"/> (deployment OR runbook run)? A task "belongs to" a
/// target iff it has a row in the <c>task_target_assignments</c> join — the single
/// authority for the target set (every target in a rolling/parallel wave reports
/// against the same task id, and runbook runs now use the same join). Queried
/// filter-free: the agent control-plane has no ambient Space and the join carries
/// no global query filter of its own.
/// <para>
/// Used by every agent-facing mutation path that resolves a task by a wire-supplied
/// id (<see cref="AgentHub"/> log/complete/step and the gRPC artifact upload) so the
/// ownership rule lives in exactly one place.
/// </para>
/// </summary>
public static class AgentDeploymentOwnership
{
    /// <summary>Entity overload — kept so call sites read naturally when the
    /// row is already loaded; ownership still resolves via the join.</summary>
    public static Task<bool> ConnectionOwnsTaskAsync(
        KrakenDbContext db, ServerTask task, Guid targetId)
        => HasAssignmentAsync(db, task.Id, targetId);

    /// <summary>Id overload — the caller has only the task id (e.g. the gRPC
    /// artifact channel, which never materialises the row).</summary>
    public static Task<bool> ConnectionOwnsTaskAsync(
        KrakenDbContext db, Guid taskId, Guid targetId)
        => HasAssignmentAsync(db, taskId, targetId);

    private static Task<bool> HasAssignmentAsync(
        KrakenDbContext db, Guid taskId, Guid targetId)
        => db.TaskTargetAssignments.IgnoreQueryFilters()
            .AnyAsync(a => a.TaskId == taskId && a.TargetId == targetId);
}
