using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KrakenDeploy.Server.Data.Interceptors;

/// <summary>
/// Prevents a privilege escalation created by the per-dimension CASCADE FKs on
/// <c>role_assignment_scopes</c>. A RoleAssignment with NO scope rows means
/// "whole Space" (<see cref="RoleAssignmentScopeMatcher"/> treats an empty
/// dimension as match-all). So if a grant is scoped ONLY to, say, project X and
/// X is hard-deleted, the plain FK CASCADE would remove that last scope row and
/// silently WIDEN the grant from "project X only" to "the entire Space".
/// <para>
/// This interceptor closes that hole: when deleting a Project / Environment /
/// Tenant / ProjectGroup empties a RoleAssignment's scope set, the assignment
/// itself is deleted (a grant that applied only to a now-deleted resource is
/// meaningless) instead of being left to widen. Assignments that keep at least
/// one scope row are simply narrowed by the FK CASCADE. Intentionally-unscoped
/// assignments (zero scope rows to begin with) are never touched, since only
/// assignments that HAVE a referencing scope row are considered.
/// </para>
/// <para>
/// Registered before <see cref="AuditLogInterceptor"/> so the assignment
/// deletions are audited. Runs on hard-delete of a scope-referenced entity.
/// </para>
/// </summary>
public sealed class RoleAssignmentScopeCleanupInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        DeleteEmptiedAssignments(eventData.Context, async: false).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await DeleteEmptiedAssignments(eventData.Context, async: true, cancellationToken)
            .ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DeleteEmptiedAssignments(
        DbContext? context, bool async, CancellationToken ct = default)
    {
        if (context is null)
        {
            return;
        }

        // Which scope-dimension entities are being deleted in this save?
        var groups = new HashSet<Guid>();
        var projects = new HashSet<Guid>();
        var environments = new HashSet<Guid>();
        var tenants = new HashSet<Guid>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Deleted)
            {
                continue;
            }
            switch (entry.Entity)
            {
                case ProjectGroup g:            groups.Add(g.Id); break;
                case Project p:                 projects.Add(p.Id); break;
                case DeploymentEnvironment e:   environments.Add(e.Id); break;
                case Tenant t:                  tenants.Add(t.Id); break;
                default: continue;
            }
        }
        if (groups.Count == 0 && projects.Count == 0 && environments.Count == 0 && tenants.Count == 0)
        {
            return;
        }

        // Assignments that have at least one scope row pointing at a deleted
        // entity. role_assignment_scopes is not ISpaceScoped (no query filter),
        // but be explicit for parity with the other cleanup interceptors.
        var affectedQuery = context.Set<RoleAssignmentScope>()
            .IgnoreQueryFilters()
            .Where(s =>
                (s.ProjectGroupId != null && groups.Contains(s.ProjectGroupId.Value))
                || (s.ProjectId != null && projects.Contains(s.ProjectId.Value))
                || (s.EnvironmentId != null && environments.Contains(s.EnvironmentId.Value))
                || (s.TenantId != null && tenants.Contains(s.TenantId.Value)))
            .Select(s => s.RoleAssignmentId)
            .Distinct();
        var affectedAssignmentIds = async
            ? await affectedQuery.ToListAsync(ct).ConfigureAwait(false)
            : affectedQuery.ToList();
        if (affectedAssignmentIds.Count == 0)
        {
            return;
        }

        // For each affected assignment, load its full scope set and decide
        // whether removing the doomed rows would leave it empty. If so, delete
        // the assignment (its remaining scope rows cascade away). Otherwise the
        // per-dimension FK CASCADE narrows it by dropping just the doomed rows.
        var scopeQuery = context.Set<RoleAssignmentScope>()
            .IgnoreQueryFilters()
            .Where(s => affectedAssignmentIds.Contains(s.RoleAssignmentId));
        var scopes = async
            ? await scopeQuery.ToListAsync(ct).ConfigureAwait(false)
            : scopeQuery.ToList();

        var emptiedAssignmentIds = scopes
            .GroupBy(s => s.RoleAssignmentId)
            .Where(g => g.All(IsDoomed))
            .Select(g => g.Key)
            .ToList();
        if (emptiedAssignmentIds.Count == 0)
        {
            return;
        }

        var assignmentsQuery = context.Set<RoleAssignment>()
            .IgnoreQueryFilters()
            .Where(a => emptiedAssignmentIds.Contains(a.Id));
        var assignments = async
            ? await assignmentsQuery.ToListAsync(ct).ConfigureAwait(false)
            : assignmentsQuery.ToList();

        // Marked Deleted in the tracker → removed by the SAME SaveChanges (and
        // audited); the assignment→scope CASCADE clears their scope rows.
        context.Set<RoleAssignment>().RemoveRange(assignments);

        bool IsDoomed(RoleAssignmentScope s) =>
            (s.ProjectGroupId != null && groups.Contains(s.ProjectGroupId.Value))
            || (s.ProjectId != null && projects.Contains(s.ProjectId.Value))
            || (s.EnvironmentId != null && environments.Contains(s.EnvironmentId.Value))
            || (s.TenantId != null && tenants.Contains(s.TenantId.Value));
    }
}
