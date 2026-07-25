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
        // later. The global space filter scopes this to the current Space.
        // ProjectGroupId is now required, so a missing default group is a
        // Space-setup invariant violation, not a silently-null project.
        var defaultGroupId = await db.ProjectGroups
            .Where(g => g.IsDefault)
            .Select(g => (Guid?)g.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No Default Project Group exists in this Space; cannot create a project.");

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

    /// <summary>A single project group by id, or null if absent / foreign-Space.</summary>
    public async Task<ProjectGroup?> GetGroupAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ProjectGroups.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
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
    /// Renames / re-describes a project group (WP5 item 3). The default group may be
    /// renamed but its <see cref="ProjectGroup.IsDefault"/> flag is preserved. Returns
    /// <c>null</c> if the group does not exist (or is outside the caller's Space).
    /// </summary>
    public async Task<ProjectGroup?> UpdateGroupAsync(
        Guid id, string name, string slug, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.ProjectGroups.AnyAsync(g => g.Slug == slug && g.Id != id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var group = await db.ProjectGroups.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        group.Name = name;
        group.Slug = slug;
        group.Description = description;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return group;
    }

    /// <summary>
    /// Deletes a project group (WP5 item 3). Refuses the bootstrap default group
    /// (<see cref="ProjectGroup.IsDefault"/>) and any group that still holds projects
    /// — <c>projects.project_group_id</c> is a required RESTRICT FK, so members are
    /// never silently orphaned or reassigned; move them out first. Returns <c>null</c>
    /// if the group does not exist (or is outside the caller's Space).
    /// </summary>
    public async Task<bool> DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var group = await db.ProjectGroups.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (group is null)
        {
            return false;
        }

        if (group.IsDefault)
        {
            throw new InvalidOperationException(
                "The Default Project Group cannot be deleted.");
        }

        var projectCount = await db.Projects
            .CountAsync(p => p.ProjectGroupId == id, ct)
            .ConfigureAwait(false);
        if (projectCount > 0)
        {
            throw new InvalidOperationException(
                $"Project group '{group.Name}' still contains {projectCount} project(s). " +
                "Move them to another group before deleting it.");
        }

        db.ProjectGroups.Remove(group);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Updates a project's lifecycle and project-group references. Pass
    /// <c>null</c> for <paramref name="lifecycleId"/> to clear the (optional)
    /// lifecycle. <paramref name="projectGroupId"/> is required: <c>null</c>
    /// means "move back to the Space's Default Project Group" (the group can
    /// never be cleared — every project belongs to exactly one group).
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

        // Resolve "no group" to the Space's Default Project Group so the
        // required FK is always satisfied.
        var resolvedGroupId = projectGroupId
            ?? await db.ProjectGroups
                .Where(g => g.IsDefault)
                .Select(g => (Guid?)g.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No Default Project Group exists in this Space; cannot clear the project's group.");

        project.LifecycleId = lifecycleId;
        project.ProjectGroupId = resolvedGroupId;
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
