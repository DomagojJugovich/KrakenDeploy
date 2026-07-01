using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.ControlPlane.Accounts;

/// <summary>
/// Catalog-backed <see cref="IAccountResolver"/>. Extracts the subdomain from the
/// request host, looks up the (cached) active account in the control-plane catalog,
/// resolves its tenant connection string from the secret store, and returns a
/// <see cref="ResolvedAccount"/>. Returns <c>null</c> for apex / control-plane
/// hosts and for subdomains that do not map to an <see cref="AccountStatus.Active"/>
/// account (the middleware decides pass-through vs. fail-closed).
/// </summary>
public sealed class CatalogAccountResolver(
    IDbContextFactory<CatalogDbContext> catalogFactory,
    ISecretStore secretStore,
    IMemoryCache cache,
    IOptions<MultiAccountOptions> options,
    ILogger<CatalogAccountResolver> logger) : IAccountResolver
{
    public async Task<ResolvedAccount?> ResolveAsync(string host, CancellationToken ct = default)
    {
        var subdomain = HostParser.ExtractSubdomain(host, options.Value.BaseDomain);
        if (subdomain is null)
        {
            return null; // apex / control-plane host — not a tenant request
        }

        if (cache.TryGetValue(CacheKey(subdomain), out ResolvedAccount? cached))
        {
            return cached;
        }

        await using var catalog = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var account = await catalog.BusinessAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Subdomain == subdomain, ct)
            .ConfigureAwait(false);

        if (account is null || account.Status != AccountStatus.Active)
        {
            // Don't cache misses: a Provisioning account flips to Active shortly, and
            // an unknown subdomain is cheap to re-check (and shouldn't be memoized).
            return null;
        }

        var connectionString = await secretStore
            .ResolveAsync(account.ConnSecretRef, ct)
            .ConfigureAwait(false);

        var resolved = new ResolvedAccount(
            account.Id, account.Subdomain, account.ConnSecretRef, connectionString);

        cache.Set(
            CacheKey(subdomain),
            resolved,
            TimeSpan.FromSeconds(Math.Max(1, options.Value.CacheSeconds)));

        logger.LogDebug("Resolved subdomain {Subdomain} to account {AccountId}", subdomain, account.Id);
        return resolved;
    }

    public async Task<ResolvedAccount?> ResolveByIdAsync(Guid accountId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(IdCacheKey(accountId), out ResolvedAccount? cached))
        {
            return cached;
        }

        await using var catalog = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var account = await catalog.BusinessAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId, ct)
            .ConfigureAwait(false);

        if (account is null || account.Status != AccountStatus.Active)
        {
            return null;
        }

        var connectionString = await secretStore
            .ResolveAsync(account.ConnSecretRef, ct)
            .ConfigureAwait(false);

        var resolved = new ResolvedAccount(
            account.Id, account.Subdomain, account.ConnSecretRef, connectionString);

        cache.Set(
            IdCacheKey(accountId),
            resolved,
            TimeSpan.FromSeconds(Math.Max(1, options.Value.CacheSeconds)));

        return resolved;
    }

    /// <summary>
    /// Evicts a cached subdomain → account mapping. Call on account status / connection
    /// change so the next request re-reads the catalog (explicit invalidation).
    /// </summary>
    public void Invalidate(string subdomain) => cache.Remove(CacheKey(subdomain));

    private static string CacheKey(string subdomain) => $"catalog:account:{subdomain}";

    private static string IdCacheKey(Guid accountId) => $"catalog:account-id:{accountId}";
}
