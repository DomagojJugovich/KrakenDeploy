using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Shared agent trust-boundary check: is the connecting agent's authenticated
/// target (its AgentJwt <c>NameIdentifier</c>) a participant in a given
/// deployment? A deployment "belongs to" a target iff it has a row in the
/// <c>deployment_target_assignments</c> join — the single authority for the
/// target set (every target in a rolling/parallel wave reports against the
/// same deployment id). Queried filter-free: the agent control-plane has no
/// ambient Space and the join carries no global query filter of its own.
/// <para>
/// Used by every agent-facing mutation path that resolves a deployment by a
/// wire-supplied id (<see cref="AgentHub"/> log/complete and the gRPC artifact
/// upload) so the ownership rule lives in exactly one place.
/// </para>
/// </summary>
public static class AgentDeploymentOwnership
{
    /// <summary>Entity overload — kept so call sites read naturally when the
    /// row is already loaded; ownership still resolves via the join.</summary>
    public static Task<bool> ConnectionOwnsDeploymentAsync(
        KrakenDbContext db, Deployment deployment, Guid targetId)
        => HasAssignmentAsync(db, deployment.Id, targetId);

    /// <summary>Id overload — the caller has only the deployment id (e.g. the
    /// gRPC artifact channel, which never materialises the row).</summary>
    public static Task<bool> ConnectionOwnsDeploymentAsync(
        KrakenDbContext db, Guid deploymentId, Guid targetId)
        => HasAssignmentAsync(db, deploymentId, targetId);

    private static Task<bool> HasAssignmentAsync(
        KrakenDbContext db, Guid deploymentId, Guid targetId)
        => db.DeploymentTargetAssignments.IgnoreQueryFilters()
            .AnyAsync(a => a.DeploymentId == deploymentId && a.TargetId == targetId);
}
