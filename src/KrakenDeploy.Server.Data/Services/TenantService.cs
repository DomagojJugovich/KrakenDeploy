using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD and relationship management for <see cref="Tenant"/>s.
/// Tag sets moved to the Space-level extended-tag-sets model — see
/// <see cref="TagService"/> (docs/extended-tag-sets-plan.md).
/// </summary>
public class TenantService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPermissionEvaluator permissions)
{
    // T1-8: the tenant-id-keyed mutations authorize against the specific tenant,
    // so a grant scoped to Tenant=X can't edit tenant Y. (Create is Space-level —
    // it carries no tenant dimension — and stays gated by the endpoint's coarse
    // TenantCreate policy.) Resolve the tenant's Space filter-free so a
    // foreign-Space id fails closed.
    private async Task EnsureTenantScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, Guid tenantId,
        Permission permission, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var spaceId = await db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => (Guid?)t.SpaceId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, permission,
            new PermissionScope(SpaceId: spaceId, TenantId: tenantId), ct).ConfigureAwait(false);
    }

    // ── Tenant ─────────────────────────────────────────────────────────────────

    public async Task<List<Tenant>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tenants.OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<Tenant?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tenants.FindAsync(new object?[] { id }, ct).AsTask();
    }

    public async Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
    }

    /// <summary>Tenants connected to one project (via the project_tenants M2M),
    /// name-ordered — powers project-scoped tenant pickers (deploy dialog).</summary>
    public async Task<List<Tenant>> GetForProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Projects
            .Where(p => p.Id == projectId)
            .SelectMany(p => p.Tenants)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    /// <summary>Returns the tenant with its connected projects loaded.</summary>
    public async Task<Tenant?> GetWithProjectsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tenants
            .Include(t => t.Projects)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<Tenant> CreateAsync(
        string name,
        string slug,
        string? description,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureTenantScopeAsync(db, caller, id, Permission.TenantEdit, ct).ConfigureAwait(false);

        if (await db.Tenants.AnyAsync(t => t.Slug == slug && t.Id != id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Slug '{slug}' is already taken.");
        }

        var tenant = await db.Tenants.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
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

    public async Task<bool> DeleteAsync(
        Guid id, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureTenantScopeAsync(db, caller, id, Permission.TenantDelete, ct).ConfigureAwait(false);
        var tenant = await db.Tenants.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
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
    public async Task ConnectProjectAsync(
        Guid tenantId, Guid projectId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureTenantScopeAsync(db, caller, tenantId, Permission.TenantEdit, ct).ConfigureAwait(false);

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
    public async Task DisconnectProjectAsync(
        Guid tenantId, Guid projectId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureTenantScopeAsync(db, caller, tenantId, Permission.TenantEdit, ct).ConfigureAwait(false);

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
}
