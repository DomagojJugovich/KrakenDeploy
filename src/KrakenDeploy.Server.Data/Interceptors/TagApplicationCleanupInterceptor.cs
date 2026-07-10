using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Tags;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KrakenDeploy.Server.Data.Interceptors;

/// <summary>
/// Referential cleanup for the polymorphic <see cref="TagApplication"/> table.
/// <c>tag_applications.entity_id</c> points at five different tables, so it
/// cannot carry a real FK — this interceptor deletes an entity's tag
/// applications in the SAME save (and therefore the same transaction) that
/// deletes the entity, keeping the table orphan-free without touching the
/// five entity services.
/// <para>
/// Registered as a singleton next to <see cref="AuditLogInterceptor"/>.
/// Queries run <c>IgnoreQueryFilters</c> — the deleting scope's active Space
/// must not hide applications (SpaceId mismatch would silently orphan them).
/// </para>
/// </summary>
public sealed class TagApplicationCleanupInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        RemoveApplicationsOfDeletedEntities(eventData.Context, async: false)
            .GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RemoveApplicationsOfDeletedEntities(eventData.Context, async: true, cancellationToken)
            .ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RemoveApplicationsOfDeletedEntities(
        DbContext? context, bool async, CancellationToken ct = default)
    {
        if (context is null)
        {
            return;
        }

        // Collect deleted taggable entities, grouped by kind. Almost every save
        // has none — the grouping short-circuits to a no-op.
        Dictionary<TaggableEntityKind, List<Guid>>? deletedByKind = null;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Deleted)
            {
                continue;
            }

            TaggableEntityKind kind;
            Guid id;
            switch (entry.Entity)
            {
                case Tenant t:                kind = TaggableEntityKind.Tenant;           id = t.Id; break;
                case Project p:               kind = TaggableEntityKind.Project;          id = p.Id; break;
                case DeploymentEnvironment e: kind = TaggableEntityKind.Environment;      id = e.Id; break;
                case Runbook r:               kind = TaggableEntityKind.Runbook;          id = r.Id; break;
                case DeploymentTarget dt:     kind = TaggableEntityKind.DeploymentTarget; id = dt.Id; break;
                default: continue;
            }

            deletedByKind ??= [];
            if (!deletedByKind.TryGetValue(kind, out var ids))
            {
                deletedByKind[kind] = ids = [];
            }
            ids.Add(id);
        }

        if (deletedByKind is null)
        {
            return;
        }

        // DB-level ON DELETE CASCADE removes a project's runbooks WITHOUT them
        // entering the change tracker, so their (polymorphic, FK-less)
        // tag_applications would orphan. Runbook is the only taggable kind that
        // is itself a cascade-dependent of another taggable kind — resolve the
        // affected runbook ids while they still exist (the cascade commits with
        // the project delete) and fold them into the Runbook removal below.
        if (deletedByKind.TryGetValue(TaggableEntityKind.Project, out var deletedProjectIds))
        {
            var runbookQuery = context.Set<Runbook>()
                .IgnoreQueryFilters()
                .Where(r => deletedProjectIds.Contains(r.ProjectId))
                .Select(r => r.Id);
            var cascadedRunbookIds = async
                ? await runbookQuery.ToListAsync(ct).ConfigureAwait(false)
                : runbookQuery.ToList();

            if (cascadedRunbookIds.Count > 0)
            {
                if (!deletedByKind.TryGetValue(TaggableEntityKind.Runbook, out var runbookIds))
                {
                    deletedByKind[TaggableEntityKind.Runbook] = runbookIds = [];
                }
                runbookIds.AddRange(cascadedRunbookIds);
            }
        }

        foreach (var (kind, ids) in deletedByKind)
        {
            var query = context.Set<TagApplication>()
                .IgnoreQueryFilters()
                .Where(a => a.EntityKind == kind && ids.Contains(a.EntityId));

            var applications = async
                ? await query.ToListAsync(ct).ConfigureAwait(false)
                : query.ToList();

            if (applications.Count > 0)
            {
                // Marked Deleted in the tracker → removed by the SAME SaveChanges
                // (and audited by AuditLogInterceptor like any other delete).
                context.Set<TagApplication>().RemoveRange(applications);
            }
        }
    }
}
