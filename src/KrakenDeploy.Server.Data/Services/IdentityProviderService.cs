using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD for <see cref="IdentityProvider"/> rows. Client secrets are encrypted
/// at rest via <see cref="IEncryptionService"/>. A null/empty secret on
/// <see cref="UpdateAsync"/> means "keep the existing secret unchanged".
/// </summary>
public class IdentityProviderService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IEncryptionService encryption,
    IOidcSchemeCacheInvalidator schemeCache)
{
    public async Task<List<IdentityProvider>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IdentityProviders
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<IdentityProvider?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<IdentityProvider> CreateAsync(
        string name, IdentityProviderType type,
        string? authority, string? clientId, string? clientSecret,
        string scopes, string groupClaimName,
        bool autoProvision, bool isEnabled,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var idp = new IdentityProvider
        {
            Name               = name.Trim(),
            Type               = type,
            Authority          = authority?.Trim(),
            ClientId           = clientId?.Trim(),
            ClientSecretEncrypted = string.IsNullOrWhiteSpace(clientSecret)
                ? null
                : encryption.Encrypt(clientSecret),
            Scopes             = scopes,
            GroupClaimName     = groupClaimName,
            AutoProvisionUsers = autoProvision,
            IsEnabled          = isEnabled,
        };

        db.IdentityProviders.Add(idp);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        schemeCache.Invalidate(idp.Id);
        return idp;
    }

    /// <summary>
    /// Updates an identity provider. A null or whitespace
    /// <paramref name="clientSecret"/> keeps the existing encrypted value.
    /// </summary>
    public async Task<IdentityProvider?> UpdateAsync(
        Guid id, string name, IdentityProviderType type,
        string? authority, string? clientId, string? clientSecret,
        string scopes, string groupClaimName,
        bool autoProvision, bool isEnabled,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var idp = await db.IdentityProviders
            .FindAsync(new object?[] { id }, ct)
            .ConfigureAwait(false);

        if (idp is null)
        {
            return null;
        }

        idp.Name               = name.Trim();
        idp.Type               = type;
        idp.Authority          = authority?.Trim();
        idp.ClientId           = clientId?.Trim();
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            idp.ClientSecretEncrypted = encryption.Encrypt(clientSecret);
        }
        idp.Scopes             = scopes;
        idp.GroupClaimName     = groupClaimName;
        idp.AutoProvisionUsers = autoProvision;
        idp.IsEnabled          = isEnabled;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        schemeCache.Invalidate(idp.Id);
        return idp;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var idp = await db.IdentityProviders
            .FindAsync(new object?[] { id }, ct)
            .ConfigureAwait(false);

        if (idp is null)
        {
            return false;
        }

        db.IdentityProviders.Remove(idp);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        schemeCache.Invalidate(id);
        return true;
    }
}
