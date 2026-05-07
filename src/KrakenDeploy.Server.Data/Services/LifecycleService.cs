using KrakenDeploy.Server.Core.Domain.Lifecycles;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD for <see cref="Lifecycle"/>s and their JSONB phase lists.
/// </summary>
public class LifecycleService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    public async Task<List<Lifecycle>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Lifecycles.OrderBy(l => l.Name).ToListAsync(ct);
    }

    public async Task<Lifecycle?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Lifecycles.FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<Lifecycle> CreateAsync(
        string name, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.Lifecycles.AnyAsync(l => l.Name == name, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"A lifecycle named '{name}' already exists.");
        }

        var lifecycle = new Lifecycle { Name = name, Description = description };
        db.Lifecycles.Add(lifecycle);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return lifecycle;
    }

    public async Task<Lifecycle?> UpdateAsync(
        Guid id, string name, string? description, List<LifecyclePhase> phases,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.Lifecycles.AnyAsync(l => l.Name == name && l.Id != id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"A lifecycle named '{name}' already exists.");
        }

        var lifecycle = await db.Lifecycles.FirstOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false);
        if (lifecycle is null)
        {
            return null;
        }

        lifecycle.Name = name;
        lifecycle.Description = description;
        // Re-assign sort orders to match the supplied order.
        for (var i = 0; i < phases.Count; i++)
        {
            phases[i].SortOrder = i;
            if (phases[i].Id == Guid.Empty)
            {
                phases[i].Id = Guid.CreateVersion7();
            }
        }
        lifecycle.Phases = phases;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return lifecycle;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var lifecycle = await db.Lifecycles.FirstOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false);
        if (lifecycle is null)
        {
            return false;
        }

        db.Lifecycles.Remove(lifecycle);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
