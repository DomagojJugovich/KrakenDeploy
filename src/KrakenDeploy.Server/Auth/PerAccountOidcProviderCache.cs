using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Caches the set of <b>enabled</b> OIDC provider ids per business account, so the
/// request-time <see cref="PerAccountOidcSchemeProvider"/> can answer "does this
/// account have this provider?" without hitting the tenant database on every request.
/// Loaded under <c>WithAccount</c> from the account's own DB; cached with a short TTL
/// and evicted explicitly when an IdP is edited (see <c>OidcSchemeCacheInvalidator</c>).
/// </summary>
public sealed class PerAccountOidcProviderCache(IServiceScopeFactory scopeFactory, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyCollection<Guid>> GetEnabledProviderIdsAsync(
        Guid accountId, CancellationToken ct = default)
    {
        var ids = await cache.GetOrCreateAsync(Key(accountId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return await LoadAsync(accountId, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return ids ?? [];
    }

    public async Task<bool> IsEnabledAsync(Guid accountId, Guid providerId, CancellationToken ct = default)
        => (await GetEnabledProviderIdsAsync(accountId, ct).ConfigureAwait(false)).Contains(providerId);

    public void Evict(Guid accountId) => cache.Remove(Key(accountId));

    private async Task<IReadOnlyCollection<Guid>> LoadAsync(Guid accountId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IAccountResolver>();
        var account = await resolver.ResolveByIdAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return [];
        }

        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContext>();
        using (accountContext.WithAccount(account))
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KrakenDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.IdentityProviders
                .Where(p => p.IsEnabled
                         && p.Authority != null
                         && p.ClientId != null
                         && p.ClientSecretEncrypted != null)
                .Select(p => p.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
    }

    private static string Key(Guid accountId) => $"oidc-providers:{accountId:N}";
}
