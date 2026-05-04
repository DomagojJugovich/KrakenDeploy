using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD for <see cref="Space"/> entities. The Space table is platform-level
/// (not <see cref="ISpaceScoped"/>) so the global query filter doesn't apply
/// here — every method returns rows across all Spaces.
/// </summary>
public class SpaceService(KrakenDbContext db)
{
    /// <summary>All spaces, ordered with the Default Space first then by name.</summary>
    public Task<List<Space>> GetAllAsync(CancellationToken ct = default)
        => db.Spaces
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);

    public Task<Space?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Spaces.FindAsync(new object?[] { id }, ct).AsTask();

    public Task<Space?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => db.Spaces.FirstOrDefaultAsync(s => s.Slug == slug, ct);

    /// <summary>Returns the bootstrap Default Space, creating it if missing.</summary>
    public async Task<Space> EnsureDefaultAsync(CancellationToken ct = default)
    {
        var existing = await db.Spaces
            .FirstOrDefaultAsync(s => s.Id == WellKnown.DefaultSpaceId, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var space = new Space
        {
            Id          = WellKnown.DefaultSpaceId,
            Slug        = WellKnown.DefaultSpaceSlug,
            Name        = WellKnown.DefaultSpaceName,
            Description = "Auto-created Default Space.",
            IsDefault   = true,
            Status      = SpaceStatus.Active,
        };

        db.Spaces.Add(space);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return space;
    }

    public async Task<Space> CreateAsync(
        string slug, string name, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (await db.Spaces.AnyAsync(s => s.Slug == slug, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Space slug '{slug}' is already taken.");
        }

        var space = new Space
        {
            Slug        = slug,
            Name        = name,
            Description = description,
            IsDefault   = false,
            Status      = SpaceStatus.Active,
        };

        db.Spaces.Add(space);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return space;
    }

    public async Task<Space?> UpdateAsync(
        Guid id, string name, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var space = await db.Spaces.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (space is null)
        {
            return null;
        }

        space.Name = name;
        space.Description = description;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return space;
    }

    /// <summary>
    /// Marks a Space as <see cref="SpaceStatus.Archived"/>. The Default Space
    /// cannot be archived.
    /// </summary>
    public async Task<bool> ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var space = await db.Spaces.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (space is null)
        {
            return false;
        }

        if (space.IsDefault)
        {
            throw new InvalidOperationException(
                "The Default Space cannot be archived.");
        }

        space.Status = SpaceStatus.Archived;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
