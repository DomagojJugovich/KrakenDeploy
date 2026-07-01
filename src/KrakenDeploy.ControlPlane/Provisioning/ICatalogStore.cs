using KrakenDeploy.ControlPlane.Catalog;

namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Catalog write/lookup surface used by the provisioner and fleet operations.
/// Wraps the control-plane <see cref="CatalogDbContext"/>.
/// </summary>
public interface ICatalogStore
{
    Task<bool> SubdomainExistsAsync(string subdomain, CancellationToken ct = default);

    Task<BusinessAccount?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default);

    /// <summary>Picks an <see cref="ShardStatus.Online"/> shard with remaining capacity.</summary>
    Task<Shard> SelectShardAsync(CancellationToken ct = default);

    Task<BusinessAccount> AddAsync(BusinessAccount account, CancellationToken ct = default);

    Task SetStatusAsync(Guid accountId, AccountStatus status, CancellationToken ct = default);

    Task RemoveAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>All accounts in a given status (e.g. Active) — used by the fleet migrator.</summary>
    Task<IReadOnlyList<BusinessAccount>> ListAsync(AccountStatus? status = null, CancellationToken ct = default);
}
