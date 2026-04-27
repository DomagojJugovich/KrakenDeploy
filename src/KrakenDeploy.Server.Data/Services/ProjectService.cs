using KrakenDeploy.Server.Core.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

public class ProjectService(KrakenDbContext db)
{
    public Task<List<Project>> GetAllAsync(CancellationToken ct = default)
        => db.Projects.OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<Project> CreateAsync(
        string name, string slug, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);

        if (await db.Projects.AnyAsync(p => p.Slug == slug, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var project = new Project { Name = name, Slug = slug, Description = description };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return project;
    }

    public async Task<Project?> UpdateAsync(
        Guid id, string name, string slug, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);

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

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
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
