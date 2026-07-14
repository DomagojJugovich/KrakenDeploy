using KrakenDeploy.Server.Core.Domain.Channels;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages <see cref="Channel"/>s for projects.
/// Ensures exactly one default channel exists per project.
/// </summary>
public class ChannelService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    public async Task<List<Channel>> GetForProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Channels
            .Where(c => c.ProjectId == projectId)
            .Include(c => c.Lifecycle)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<Channel?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Channels.Include(c => c.Lifecycle).FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>
    /// Returns the default channel for a project, creating one if none exists.
    /// </summary>
    public async Task<Channel> GetOrCreateDefaultAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.Channels
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.IsDefault, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        // Belt: the project must be in the current Space before we stamp a channel
        // into it (the composite FK (space_id, project_id) is the braces).
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Project {projectId} not found in the current Space.");
        }

        // Auto-create a default channel on first access.
        var channel = new Channel
        {
            ProjectId = projectId,
            Name = "Default",
            IsDefault = true,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return channel;
    }

    /// <summary>
    /// Rejects a malformed version rule up front (invalid NuGet range or regex),
    /// so a bad rule fails at channel save rather than surfacing only later at
    /// release creation. Translates the parser's <see cref="FormatException"/> into
    /// the <see cref="InvalidOperationException"/> the API/UI already map to a
    /// user-facing validation error.
    /// </summary>
    private static void ValidateVersionRule(string? versionRange, string? versionTag)
    {
        try
        {
            ChannelVersionRule.Parse(versionRange, versionTag);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }

    public async Task<Channel> CreateAsync(
        Guid projectId,
        string name,
        bool isDefault,
        Guid? lifecycleId,
        string? versionRange,
        string? versionTag,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateVersionRule(versionRange, versionTag);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Belt: the project must be in the current Space (the composite FK
        // (space_id, project_id) is the braces — this yields a clear error first).
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Project {projectId} not found in the current Space.");
        }

        if (await db.Channels.AnyAsync(c => c.ProjectId == projectId && c.Name == name, ct)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Channel '{name}' already exists for this project.");
        }

        if (lifecycleId.HasValue)
        {
            var lcExists = await db.Lifecycles.AnyAsync(l => l.Id == lifecycleId.Value, ct)
                .ConfigureAwait(false);
            if (!lcExists)
            {
                throw new InvalidOperationException($"Lifecycle {lifecycleId} not found.");
            }
        }

        if (isDefault)
        {
            // Clear IsDefault on any existing default channel for this project.
            await db.Channels
                .Where(c => c.ProjectId == projectId && c.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDefault, false), ct)
                .ConfigureAwait(false);
        }

        var channel = new Channel
        {
            ProjectId = projectId,
            Name = name,
            IsDefault = isDefault,
            LifecycleId = lifecycleId,
            VersionRange = versionRange,
            VersionTag = versionTag,
        };

        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return channel;
    }

    public async Task<Channel?> UpdateAsync(
        Guid id,
        string name,
        bool isDefault,
        Guid? lifecycleId,
        string? versionRange,
        string? versionTag,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateVersionRule(versionRange, versionTag);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var channel = await db.Channels.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (channel is null)
        {
            return null;
        }

        if (await db.Channels.AnyAsync(c => c.ProjectId == channel.ProjectId && c.Name == name && c.Id != id, ct)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Channel '{name}' already exists for this project.");
        }

        if (isDefault && !channel.IsDefault)
        {
            await db.Channels
                .Where(c => c.ProjectId == channel.ProjectId && c.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDefault, false), ct)
                .ConfigureAwait(false);
        }

        channel.Name = name;
        channel.IsDefault = isDefault;
        channel.LifecycleId = lifecycleId;
        channel.VersionRange = versionRange;
        channel.VersionTag = versionTag;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return channel;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var channel = await db.Channels.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (channel is null)
        {
            return false;
        }

        if (channel.IsDefault)
        {
            throw new InvalidOperationException("Cannot delete the default channel. Designate another channel as default first.");
        }

        db.Channels.Remove(channel);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
