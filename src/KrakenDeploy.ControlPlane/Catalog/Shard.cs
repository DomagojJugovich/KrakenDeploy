namespace KrakenDeploy.ControlPlane.Catalog;

/// <summary>
/// A PostgreSQL server/instance that hosts one or more tenant databases.
/// Control-plane row; see <c>docs/saas-multi-account-architecture.md</c> §8.
/// </summary>
public class Shard
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Operator-facing name for the shard.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Reference into the secret store for the shard's admin/connection secret
    /// (used to <c>CREATE DATABASE</c> during provisioning). Never the raw secret.
    /// </summary>
    public required string HostSecretRef { get; set; }

    /// <summary>Soft cap on the number of tenant accounts placed on this shard.</summary>
    public int Capacity { get; set; }

    public ShardStatus Status { get; set; } = ShardStatus.Online;

    public ICollection<BusinessAccount> Accounts { get; } = new List<BusinessAccount>();
}

/// <summary>Lifecycle state for a <see cref="Shard"/>.</summary>
public enum ShardStatus
{
    /// <summary>Accepting new accounts and serving traffic.</summary>
    Online = 0,

    /// <summary>Serving existing accounts but not accepting new placements.</summary>
    Draining = 1,

    /// <summary>Out of service.</summary>
    Offline = 2,
}
