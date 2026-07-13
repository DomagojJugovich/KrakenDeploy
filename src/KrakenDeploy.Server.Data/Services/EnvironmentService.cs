using KrakenDeploy.Server.Core.Domain.Environments;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

public class EnvironmentService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <param name="includeArchived">
    /// <c>false</c> (default) hides archived environments — the safe default for
    /// pickers (you can't scope new work to a retired environment). Pass
    /// <c>true</c> for admin/management and for historical filter/display lists
    /// that must still resolve archived ids.
    /// </param>
    public async Task<List<DeploymentEnvironment>> GetAllOrderedAsync(
        bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Environments.AsQueryable();
        if (!includeArchived)
        {
            query = query.Where(e => !e.Archived);
        }
        return await query.OrderBy(e => e.SortOrder).ThenBy(e => e.Name).ToListAsync(ct);
    }

    public async Task<bool> SetArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var env = await db.Environments.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (env is null)
        {
            return false;
        }
        env.Archived = archived;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<DeploymentEnvironment> CreateAsync(
        string name, string slug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.Environments.AnyAsync(e => e.Slug == slug, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var maxOrder = await db.Environments
            .MaxAsync(e => (int?)e.SortOrder, ct).ConfigureAwait(false) ?? -1;

        var env = new DeploymentEnvironment { Name = name, Slug = slug, SortOrder = maxOrder + 1 };
        db.Environments.Add(env);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return env;
    }

    public async Task<DeploymentEnvironment?> UpdateAsync(
        Guid id, string name, string slug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.Environments.AnyAsync(e => e.Slug == slug && e.Id != id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var env = await db.Environments.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (env is null)
        {
            return null;
        }

        env.Name = name;
        env.Slug = slug;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return env;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var env = await db.Environments.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (env is null)
        {
            return false;
        }

        // Friendly guard in front of the RESTRICT FK (server_tasks.environment_id):
        // an environment with execution history can't be hard-deleted. Point the
        // caller at the archive escape hatch instead of a raw DbUpdateException.
        var hasHistory = await db.ServerTasks.IgnoreQueryFilters()
            .AnyAsync(t => t.EnvironmentId == id, ct).ConfigureAwait(false);
        if (hasHistory)
        {
            throw new InvalidOperationException(
                "This environment has deployment history and can't be deleted. Archive it instead.");
        }

        db.Environments.Remove(env);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task MoveUpAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var all = await db.Environments
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        var idx = all.FindIndex(e => e.Id == id);
        if (idx <= 0)
        {
            return;
        }

        (all[idx].SortOrder, all[idx - 1].SortOrder) = (all[idx - 1].SortOrder, all[idx].SortOrder);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MoveDownAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var all = await db.Environments
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        var idx = all.FindIndex(e => e.Id == id);
        if (idx < 0 || idx >= all.Count - 1)
        {
            return;
        }

        (all[idx].SortOrder, all[idx + 1].SortOrder) = (all[idx + 1].SortOrder, all[idx].SortOrder);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
            else if ((ch is ' ' or '-' or '_') && !lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().TrimEnd('-');
        return slug.Length > 64 ? slug[..64] : slug;
    }
}
