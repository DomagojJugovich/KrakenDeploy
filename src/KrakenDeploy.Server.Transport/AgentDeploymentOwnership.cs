using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Shared agent trust-boundary check: is the connecting agent's authenticated
/// target (its AgentJwt <c>NameIdentifier</c>) a participant in a given
/// deployment? A deployment "belongs to" a target when it is the legacy single
/// <see cref="Deployment.TargetId"/> OR has a row in the
/// <c>deployment_target_assignments</c> join (the multi-target source of truth —
/// every target in a rolling/parallel wave reports against the same deployment
/// id). Queried filter-free: the agent control-plane has no ambient Space and
/// the join carries no global query filter of its own.
/// <para>
/// Used by every agent-facing mutation path that resolves a deployment by a
/// wire-supplied id (<see cref="AgentHub"/> log/complete and the gRPC artifact
/// upload) so the ownership rule lives in exactly one place.
/// </para>
/// </summary>
public static class AgentDeploymentOwnership
{
    /// <summary>Entity overload — the caller already loaded the deployment row,
    /// so the legacy target column is checked in-memory (no extra query).</summary>
    public static async Task<bool> ConnectionOwnsDeploymentAsync(
        KrakenDbContext db, Deployment deployment, Guid targetId)
        => deployment.TargetId == targetId
           || await HasAssignmentAsync(db, deployment.Id, targetId).ConfigureAwait(false);

    /// <summary>Id overload — the caller has only the deployment id (e.g. the
    /// gRPC artifact channel, which never materialises the row).</summary>
    public static async Task<bool> ConnectionOwnsDeploymentAsync(
        KrakenDbContext db, Guid deploymentId, Guid targetId)
        => await db.Deployments.IgnoreQueryFilters()
               .AnyAsync(d => d.Id == deploymentId && d.TargetId == targetId).ConfigureAwait(false)
           || await HasAssignmentAsync(db, deploymentId, targetId).ConfigureAwait(false);

    private static Task<bool> HasAssignmentAsync(
        KrakenDbContext db, Guid deploymentId, Guid targetId)
        => db.DeploymentTargetAssignments.IgnoreQueryFilters()
            .AnyAsync(a => a.DeploymentId == deploymentId && a.TargetId == targetId);
}
