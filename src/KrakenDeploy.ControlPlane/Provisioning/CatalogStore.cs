using KrakenDeploy.ControlPlane.Catalog;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.ControlPlane.Provisioning;

/// <inheritdoc />
public sealed class CatalogStore(IDbContextFactory<CatalogDbContext> catalogFactory) : ICatalogStore
{
    public async Task<bool> SubdomainExistsAsync(string subdomain, CancellationToken ct = default)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.BusinessAccounts.AnyAsync(a => a.Subdomain == subdomain, ct).ConfigureAwait(false);
    }

    public async Task<BusinessAccount?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.BusinessAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Subdomain == subdomain, ct)
            .ConfigureAwait(false);
    }

    public async Task<Shard> SelectShardAsync(CancellationToken ct = default)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Fewest-accounts-first among Online shards that still have capacity.
        var candidate = await db.Shards
            .Where(s => s.Status == ShardStatus.Online && s.Accounts.Count < s.Capacity)
            .OrderBy(s => s.Accounts.Count)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return candidate ?? throw new InvalidOperationException(
            "No online shard with spare capacity is available for placement.");
    }

    public async Task<BusinessAccount> AddAsync(BusinessAccount account, CancellationToken ct = default)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.BusinessAccounts.Add(account);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return account;
    }

    public async Task SetStatusAsync(Guid accountId, AccountStatus status, CancellationToken ct = default)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var account = await db.BusinessAccounts.FindAsync([accountId], ct).ConfigureAwait(false);
        if (account is null)
        {
            return;
        }

        account.Status = status;
        account.ModifiedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var account = await db.BusinessAccounts.FindAsync([accountId], ct).ConfigureAwait(false);
        if (account is null)
        {
            return;
        }

        db.BusinessAccounts.Remove(account);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BusinessAccount>> ListAsync(
        AccountStatus? status = null, CancellationToken ct = default)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.BusinessAccounts.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            query = query.Where(a => a.Status == s);
        }

        return await query.OrderBy(a => a.Subdomain).ToListAsync(ct).ConfigureAwait(false);
    }
}
