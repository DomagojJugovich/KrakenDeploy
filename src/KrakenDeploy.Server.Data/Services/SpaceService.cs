using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD for <see cref="Space"/> entities. The Space table is platform-level
/// (not <see cref="ISpaceScoped"/>) so the global query filter doesn't apply
/// here — every method returns rows across all Spaces.
/// </summary>
public class SpaceService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>All spaces, ordered with the Default Space first then by name.</summary>
    public async Task<List<Space>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Spaces
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<Space?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Spaces.FindAsync(new object?[] { id }, ct).AsTask();
    }

    public async Task<Space?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Spaces.FirstOrDefaultAsync(s => s.Slug == slug, ct);
    }

    /// <summary>Returns the bootstrap Default Space, creating it if missing.</summary>
    public async Task<Space> EnsureDefaultAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.Spaces
            .FirstOrDefaultAsync(s => s.Id == WellKnown.DefaultSpaceId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new Space
            {
                Id          = WellKnown.DefaultSpaceId,
                Slug        = WellKnown.DefaultSpaceSlug,
                Name        = WellKnown.DefaultSpaceName,
                Description = "Auto-created Default Space.",
                IsDefault   = true,
                Status      = SpaceStatus.Active,
            };

            db.Spaces.Add(existing);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Default Project Group inside the Default Space.
        await EnsureDefaultProjectGroupAsync(existing.Id, ct).ConfigureAwait(false);

        return existing;
    }

    /// <summary>
    /// Returns the Default Project Group for the given Space, creating it if
    /// missing. Every Space gets one auto-created — new Projects land there
    /// unless explicitly moved.
    /// </summary>
    public async Task<ProjectGroup> EnsureDefaultProjectGroupAsync(
        Guid spaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // IgnoreQueryFilters because the active Space might not match the
        // Space we're seeding (e.g. when creating a brand-new Space that
        // isn't yet the active one).
        var existing = await db.ProjectGroups
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.SpaceId == spaceId && g.IsDefault, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var group = new ProjectGroup
        {
            SpaceId     = spaceId, // explicit so SpaceScopingInterceptor leaves it
            Slug        = "default",
            Name        = "Default Project Group",
            Description = "Auto-created default group for projects in this Space.",
            IsDefault   = true,
            SortOrder   = 0,
        };

        db.ProjectGroups.Add(group);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return group;
    }

    /// <summary>
    /// Creates a Space, seeds its built-in teams, and (when
    /// <paramref name="creatorUserId"/> is supplied) adds that user to the new
    /// Space's "Space Managers" team. The membership is the anti-lockout step:
    /// after the hard-tenant-boundary fix a non-admin creator would otherwise be
    /// locked out of the Space they just made (system admins keep access via
    /// <see cref="Permission.AdministerSystem"/>). CLI / seed callers pass
    /// <c>null</c>.
    /// </summary>
    public async Task<Space> CreateAsync(
        string slug, string name, string? description,
        Guid? creatorUserId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        ArgumentException.ThrowIfNullOrEmpty(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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

        // Auto-create the Default Project Group inside the new Space.
        await EnsureDefaultProjectGroupAsync(space.Id, ct).ConfigureAwait(false);

        // Auto-seed the per-Space built-in teams (Space Managers, Project
        // Deployers, Project Contributors, Project Viewers, Everyone) so a
        // brand-new Space immediately has the standard team inventory.
        var rbacSeeder = new BuiltInRbacSeeder(dbFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BuiltInRbacSeeder>.Instance);
        await rbacSeeder.SeedSpaceTeamsAsync(space.Id, ct).ConfigureAwait(false);

        // Anti-lockout: make the creator a Space Manager of the new Space.
        if (creatorUserId is { } uid)
        {
            var managersTeamId = BuiltInRbacSeeder.SpaceManagersTeamId(space.Id);
            var alreadyMember = await db.TeamMembers
                .AnyAsync(m => m.TeamId == managersTeamId && m.UserId == uid, ct)
                .ConfigureAwait(false);
            if (!alreadyMember)
            {
                db.TeamMembers.Add(new TeamMember
                {
                    TeamId   = managersTeamId,
                    UserId   = uid,
                    AddedUtc = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        return space;
    }

    public async Task<Space?> UpdateAsync(
        Guid id, string name, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
