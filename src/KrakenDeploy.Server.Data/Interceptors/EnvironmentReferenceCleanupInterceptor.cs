using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Freezes;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KrakenDeploy.Server.Data.Interceptors;

/// <summary>
/// Referential cleanup for environment ids buried in jsonb columns that cannot
/// carry a real FK: <c>lifecycles.phases</c> (per-phase
/// <c>EnvironmentIds</c>/<c>OptionalEnvironmentIds</c>),
/// <c>deployment_freezes.environment_ids</c> and
/// <c>event_subscriptions.environment_ids</c>. When a
/// <see cref="DeploymentEnvironment"/> is hard-deleted, this strips its id out
/// of those documents in the SAME save (and transaction), so no lifecycle gate
/// or freeze/subscription filter is left pointing at a ghost environment.
/// <para>
/// Mirrors <see cref="TagApplicationCleanupInterceptor"/>: registered as a
/// singleton BEFORE <c>AuditLogInterceptor</c> so its edits are audited.
/// Queries run <c>IgnoreQueryFilters</c> because <c>event_subscriptions</c>
/// isn't <see cref="Core.Domain.Common.ISpaceScoped"/> (system-wide rows) and
/// cross-space lifecycle/freeze rows must still be swept.
/// </para>
/// <para>
/// Fires on hard-delete only. Archiving an environment (a soft flag) leaves
/// these references intact by design — an archived environment keeps resolving
/// for historical rows.
/// </para>
/// </summary>
public sealed class EnvironmentReferenceCleanupInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ScrubDeletedEnvironmentIds(eventData.Context, async: false)
            .GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await ScrubDeletedEnvironmentIds(eventData.Context, async: true, cancellationToken)
            .ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ScrubDeletedEnvironmentIds(
        DbContext? context, bool async, CancellationToken ct = default)
    {
        if (context is null)
        {
            return;
        }

        // Collect environments being deleted in this save. Almost every save
        // has none — bail immediately in that case.
        var deletedIds = context.ChangeTracker.Entries<DeploymentEnvironment>()
            .Where(e => e.State == EntityState.Deleted)
            .Select(e => e.Entity.Id)
            .ToHashSet();
        if (deletedIds.Count == 0)
        {
            return;
        }

        // These tables are low-cardinality and the jsonb id arrays are opaque
        // to LINQ-to-SQL (value-converted), so load all rows and filter in
        // memory. Env deletes are rare, so the scan cost is acceptable.
        var lifecycles = async
            ? await context.Set<Lifecycle>().IgnoreQueryFilters().ToListAsync(ct).ConfigureAwait(false)
            : context.Set<Lifecycle>().IgnoreQueryFilters().ToList();
        foreach (var lifecycle in lifecycles)
        {
            var changed = false;
            foreach (var phase in lifecycle.Phases)
            {
                changed |= RemoveIds(phase.EnvironmentIds, deletedIds, out var req);
                if (req is not null) { phase.EnvironmentIds = req; }
                changed |= RemoveIds(phase.OptionalEnvironmentIds, deletedIds, out var opt);
                if (opt is not null) { phase.OptionalEnvironmentIds = opt; }
            }
            if (changed)
            {
                // Reassign the top-level list so EF flags the jsonb column
                // modified regardless of value-comparer behaviour.
                lifecycle.Phases = [.. lifecycle.Phases];
            }
        }

        var freezes = async
            ? await context.Set<DeploymentFreeze>().IgnoreQueryFilters().ToListAsync(ct).ConfigureAwait(false)
            : context.Set<DeploymentFreeze>().IgnoreQueryFilters().ToList();
        foreach (var freeze in freezes)
        {
            if (RemoveIds(freeze.EnvironmentIds, deletedIds, out var cleaned) && cleaned is not null)
            {
                freeze.EnvironmentIds = cleaned;
            }
        }

        var subscriptions = async
            ? await context.Set<EventSubscription>().IgnoreQueryFilters().ToListAsync(ct).ConfigureAwait(false)
            : context.Set<EventSubscription>().IgnoreQueryFilters().ToList();
        foreach (var subscription in subscriptions)
        {
            if (RemoveIds(subscription.EnvironmentIds, deletedIds, out var cleaned) && cleaned is not null)
            {
                subscription.EnvironmentIds = cleaned;
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> and yields a NEW list without the removed ids when
    /// <paramref name="source"/> contained any of them; otherwise <c>false</c>
    /// and <c>null</c> (leave the property untouched).
    /// </summary>
    private static bool RemoveIds(List<Guid> source, HashSet<Guid> removed, out List<Guid>? cleaned)
    {
        if (!source.Any(removed.Contains))
        {
            cleaned = null;
            return false;
        }
        cleaned = source.Where(id => !removed.Contains(id)).ToList();
        return true;
    }
}
