using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD and relationship management for <see cref="Tenant"/>s, <see cref="TagSet"/>s,
/// and <see cref="TenantTag"/>s.
/// </summary>
public class TenantService(KrakenDbContext db)
{
    // ── Tenant ─────────────────────────────────────────────────────────────────

    public Task<List<Tenant>> GetAllAsync(CancellationToken ct = default)
        => db.Tenants.OrderBy(t => t.Name).ToListAsync(ct);

    public Task<Tenant?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Tenants.FindAsync(new object?[] { id }, ct).AsTask();

    public Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);

    /// <summary>Returns the tenant with its TagSets and Tags loaded.</summary>
    public Task<Tenant?> GetWithTagsAsync(Guid id, CancellationToken ct = default)
        => db.Tenants
            .Include(t => t.TagSets)
                .ThenInclude(ts => ts.Tags)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>Returns the tenant with its connected projects loaded.</summary>
    public Task<Tenant?> GetWithProjectsAsync(Guid id, CancellationToken ct = default)
        => db.Tenants
            .Include(t => t.Projects)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<Tenant> CreateAsync(
        string name,
        string slug,
        string? description,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var tenant = new Tenant { Name = name, Slug = slug, Description = description };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tenant;
    }

    public async Task<Tenant?> UpdateAsync(
        Guid id,
        string name,
        string slug,
        string? description,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        if (await db.Tenants.AnyAsync(t => t.Slug == slug && t.Id != id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var tenant = await GetAsync(id, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return null;
        }

        tenant.Name = name;
        tenant.Slug = slug;
        tenant.Description = description;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tenant;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tenant = await GetAsync(id, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return false;
        }

        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Project ↔ Tenant connections ───────────────────────────────────────────

    /// <summary>Connects a project to a tenant (idempotent).</summary>
    public async Task ConnectProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants
            .Include(t => t.Projects)
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found.");

        if (tenant.Projects.Any(p => p.Id == projectId))
        {
            return; // already connected
        }

        var project = await db.Projects.FindAsync(new object?[] { projectId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        tenant.Projects.Add(project);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Disconnects a project from a tenant (idempotent).</summary>
    public async Task DisconnectProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants
            .Include(t => t.Projects)
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            .ConfigureAwait(false);

        if (tenant is null)
        {
            return;
        }

        var project = tenant.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project is not null)
        {
            tenant.Projects.Remove(project);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    // ── TagSet ─────────────────────────────────────────────────────────────────

    public Task<List<TagSet>> GetTagSetsAsync(Guid tenantId, CancellationToken ct = default)
        => db.TagSets
            .Where(ts => ts.TenantId == tenantId)
            .OrderBy(ts => ts.SortOrder).ThenBy(ts => ts.Name)
            .Include(ts => ts.Tags)
            .ToListAsync(ct);

    public Task<TagSet?> GetTagSetAsync(Guid id, CancellationToken ct = default)
        => db.TagSets.Include(ts => ts.Tags).FirstOrDefaultAsync(ts => ts.Id == id, ct);

    public async Task<TagSet> CreateTagSetAsync(
        Guid tenantId,
        string name,
        string? description,
        int sortOrder,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (!tenantExists)
        {
            throw new InvalidOperationException($"Tenant {tenantId} not found.");
        }

        if (await db.TagSets.AnyAsync(ts => ts.TenantId == tenantId && ts.Name == name, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Tag set '{name}' already exists for this tenant.");
        }

        var tagSet = new TagSet
        {
            TenantId = tenantId,
            Name = name,
            Description = description,
            SortOrder = sortOrder,
        };

        db.TagSets.Add(tagSet);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tagSet;
    }

    public async Task<TagSet?> UpdateTagSetAsync(
        Guid id,
        string name,
        string? description,
        int sortOrder,
        CancellationToken ct = default)
    {
        var tagSet = await db.TagSets.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (tagSet is null)
        {
            return null;
        }

        tagSet.Name = name;
        tagSet.Description = description;
        tagSet.SortOrder = sortOrder;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tagSet;
    }

    public async Task<bool> DeleteTagSetAsync(Guid id, CancellationToken ct = default)
    {
        var tagSet = await db.TagSets.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (tagSet is null)
        {
            return false;
        }

        db.TagSets.Remove(tagSet);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── TenantTag ──────────────────────────────────────────────────────────────

    public Task<TenantTag?> GetTagAsync(Guid id, CancellationToken ct = default)
        => db.TenantTags.FindAsync(new object?[] { id }, ct).AsTask();

    public async Task<TenantTag> CreateTagAsync(
        Guid tagSetId,
        string name,
        string? color,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var setExists = await db.TagSets.AnyAsync(ts => ts.Id == tagSetId, ct).ConfigureAwait(false);
        if (!setExists)
        {
            throw new InvalidOperationException($"TagSet {tagSetId} not found.");
        }

        if (await db.TenantTags.AnyAsync(t => t.TagSetId == tagSetId && t.Name == name, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Tag '{name}' already exists in this tag set.");
        }

        var tag = new TenantTag { TagSetId = tagSetId, Name = name, Color = color };
        db.TenantTags.Add(tag);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tag;
    }

    public async Task<TenantTag?> UpdateTagAsync(
        Guid id,
        string name,
        string? color,
        CancellationToken ct = default)
    {
        var tag = await db.TenantTags.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (tag is null)
        {
            return null;
        }

        tag.Name = name;
        tag.Color = color;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tag;
    }

    public async Task<bool> DeleteTagAsync(Guid id, CancellationToken ct = default)
    {
        var tag = await db.TenantTags.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (tag is null)
        {
            return false;
        }

        db.TenantTags.Remove(tag);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Target ↔ TenantTag connections ─────────────────────────────────────────

    /// <summary>Assigns a tag to a target (idempotent).</summary>
    public async Task AddTagToTargetAsync(Guid tagId, Guid targetId, CancellationToken ct = default)
    {
        var tag = await db.TenantTags
            .Include(t => t.Targets)
            .FirstOrDefaultAsync(t => t.Id == tagId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tag {tagId} not found.");

        if (tag.Targets.Any(t => t.Id == targetId))
        {
            return; // already tagged
        }

        var target = await db.DeploymentTargets.FindAsync(new object?[] { targetId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Target {targetId} not found.");

        tag.Targets.Add(target);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes a tag from a target (idempotent).</summary>
    public async Task RemoveTagFromTargetAsync(Guid tagId, Guid targetId, CancellationToken ct = default)
    {
        var tag = await db.TenantTags
            .Include(t => t.Targets)
            .FirstOrDefaultAsync(t => t.Id == tagId, ct)
            .ConfigureAwait(false);

        if (tag is null)
        {
            return;
        }

        var target = tag.Targets.FirstOrDefault(t => t.Id == targetId);
        if (target is not null)
        {
            tag.Targets.Remove(target);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns all tag IDs assigned to a target, grouped by TagSet.
    /// </summary>
    public Task<List<TenantTag>> GetTagsForTargetAsync(Guid targetId, CancellationToken ct = default)
        => db.TenantTags
            .Where(t => t.Targets.Any(tr => tr.Id == targetId))
            .Include(t => t.TagSet)
            .OrderBy(t => t.TagSet.SortOrder)
                .ThenBy(t => t.Name)
            .ToListAsync(ct);
}
