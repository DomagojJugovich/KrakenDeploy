using KrakenDeploy.Server.Core.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

public class ProjectService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    public async Task<List<Project>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Projects.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<Project?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Projects.FindAsync(new object?[] { id }, ct).AsTask();
    }

    public async Task<Project?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug, ct);
    }

    public async Task<Project> CreateAsync(
        string name, string slug, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.Projects.AnyAsync(p => p.Slug == slug, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        // New projects land in the Space's Default Project Group unless moved
        // later (matches the documented behaviour). The global space filter
        // scopes this to the current Space; null only if no default exists.
        var defaultGroupId = await db.ProjectGroups
            .Where(g => g.IsDefault)
            .Select(g => (Guid?)g.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var project = new Project
        {
            Name = name,
            Slug = slug,
            Description = description,
            ProjectGroupId = defaultGroupId,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return project;
    }

    public async Task<Project?> UpdateAsync(
        Guid id, string name, string slug, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.Projects.AnyAsync(p => p.Slug == slug && p.Id != id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var project = await db.Projects.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        project.Name = name;
        project.Slug = slug;
        project.Description = description;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return project;
    }

    /// <summary>Returns all project groups in the current Space, ordered by SortOrder/Name.</summary>
    public async Task<List<ProjectGroup>> GetProjectGroupsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ProjectGroups
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Creates a new project group at the end of the display order.</summary>
    public async Task<ProjectGroup> CreateGroupAsync(
        string name, string slug, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.ProjectGroups.AnyAsync(g => g.Slug == slug, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var sortOrder = (await db.ProjectGroups
            .MaxAsync(g => (int?)g.SortOrder, ct)
            .ConfigureAwait(false) ?? -1) + 1;

        var group = new ProjectGroup
        {
            Name = name,
            Slug = slug,
            Description = description,
            SortOrder = sortOrder,
        };
        db.ProjectGroups.Add(group);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return group;
    }

    /// <summary>
    /// Updates a project's lifecycle and project-group references. Pass
    /// <c>null</c> for either parameter to clear that reference.
    /// </summary>
    public async Task<Project?> SetLifecycleAndGroupAsync(
        Guid id, Guid? lifecycleId, Guid? projectGroupId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var project = await db.Projects.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }
        project.LifecycleId = lifecycleId;
        project.ProjectGroupId = projectGroupId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return project;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var project = await db.Projects.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (project is null)
        {
            return false;
        }

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public static string Slugify(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new System.Text.StringBuilder(input.Length);
        bool lastDash = false;
        foreach (var ch in input.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                // Any non-alphanumeric character (space, dash, dot, slash, #, etc.)
                // becomes a single separator dash; consecutive separators are collapsed.
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().TrimEnd('-');
        return slug.Length > 64 ? slug[..64] : slug;
    }
}
