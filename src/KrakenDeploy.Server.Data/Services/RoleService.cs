using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD for custom <see cref="Role"/>s. Built-in roles (<see cref="Role.IsBuiltIn"/>
/// = true) are managed by <see cref="BuiltInRbacSeeder"/> and are read-only here —
/// <see cref="UpdateAsync"/> and <see cref="DeleteAsync"/> silently no-op on them.
/// </summary>
public class RoleService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    public async Task<List<Role>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Roles
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task<Role?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<Role> CreateAsync(
        string name, string? description, List<Permission> permissions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.Roles.AnyAsync(r => r.Name == name, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"A role named '{name}' already exists.");
        }

        var role = new Role
        {
            Name               = name.Trim(),
            Description        = description?.Trim(),
            GrantedPermissions = permissions,
            IsBuiltIn          = false,
            IsSystemOnly       = false,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return role;
    }

    /// <summary>
    /// Updates a custom role's name, description, and permission set.
    /// Returns null when the role is not found or is built-in (immutable).
    /// </summary>
    public async Task<Role?> UpdateAsync(
        Guid id, string name, string? description, List<Permission> permissions,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var role = await db.Roles.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (role is null || role.IsBuiltIn)
        {
            return null;
        }

        if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"A role named '{name}' already exists.");
        }

        role.Name               = name.Trim();
        role.Description        = description?.Trim();
        role.GrantedPermissions = permissions;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return role;
    }

    /// <summary>
    /// Deletes a custom role. Returns false when not found or built-in.
    /// Does NOT check for orphaned role assignments — callers should warn
    /// the user if assignments reference this role before calling.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var role = await db.Roles.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (role is null || role.IsBuiltIn)
        {
            return false;
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
