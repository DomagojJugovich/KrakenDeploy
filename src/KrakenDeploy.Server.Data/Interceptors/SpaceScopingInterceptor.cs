using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KrakenDeploy.Server.Data.Interceptors;

/// <summary>
/// EF Core <see cref="SaveChangesInterceptor"/> that auto-stamps
/// <see cref="ISpaceScoped.SpaceId"/> on inserts when the caller hasn't set it.
/// Resolves the active Space from <see cref="ISpaceContext"/> and writes it
/// into every <see cref="EntityState.Added"/> entity that implements
/// <see cref="ISpaceScoped"/> with an empty <c>SpaceId</c>.
/// <para>
/// Caller-set values are preserved — system code can stamp a specific Space
/// (e.g. seeding bootstrap data, cross-Space admin operations) and the
/// interceptor leaves it alone.
/// </para>
/// <para>
/// Modifications to <c>SpaceId</c> on existing rows are <em>blocked</em> —
/// the interceptor throws <see cref="InvalidOperationException"/>. Once an
/// entity is stamped with a Space it cannot move; that's a destructive
/// operation users would have to do explicitly via raw SQL.
/// </para>
/// </summary>
public sealed class SpaceScopingInterceptor(ISpaceContext spaceContext) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        StampSpaceIds(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StampSpaceIds(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampSpaceIds(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<ISpaceScoped>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.SpaceId == Guid.Empty)
                    {
                        entry.Entity.SpaceId = spaceContext.CurrentSpaceId;
                    }
                    break;

                case EntityState.Modified:
                    var spaceProp = entry.Property(nameof(ISpaceScoped.SpaceId));
                    if (spaceProp.IsModified &&
                        (Guid)spaceProp.OriginalValue! != (Guid)spaceProp.CurrentValue!)
                    {
                        throw new InvalidOperationException(
                            $"Cannot move {entry.Metadata.ClrType.Name} " +
                            $"{entry.Property("Id").CurrentValue} between Spaces. " +
                            $"SpaceId is immutable once assigned.");
                    }
                    break;
            }
        }
    }
}
