using KrakenDeploy.Server.Core.Domain.Channels;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages <see cref="Channel"/>s for projects.
/// Ensures exactly one default channel exists per project.
/// </summary>
public class ChannelService(KrakenDbContext db)
{
    public Task<List<Channel>> GetForProjectAsync(Guid projectId, CancellationToken ct = default)
        => db.Channels
            .Where(c => c.ProjectId == projectId)
            .Include(c => c.Lifecycle)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public Task<Channel?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Channels.Include(c => c.Lifecycle).FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>
    /// Returns the default channel for a project, creating one if none exists.
    /// </summary>
    public async Task<Channel> GetOrCreateDefaultAsync(Guid projectId, CancellationToken ct = default)
    {
        var existing = await db.Channels
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.IsDefault, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
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
