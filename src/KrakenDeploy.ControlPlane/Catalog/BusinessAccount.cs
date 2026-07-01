namespace KrakenDeploy.ControlPlane.Catalog;

/// <summary>
/// A business account = one fully isolated KrakenDeploy instance (its own users,
/// Spaces, data) that mimics a standalone install. Identified by a subdomain and
/// mapped to a tenant database via a connection-secret reference.
/// <para>
/// This is a <em>control-plane</em> row holding routing metadata only — no customer
/// PII. The resolved connection string is the cross-customer isolation boundary.
/// See <c>docs/saas-multi-account-architecture.md</c> §6–§8.
/// </para>
/// </summary>
public class BusinessAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Normalized, lower-case, validated subdomain label (unique).</summary>
    public required string Subdomain { get; set; }

    /// <summary>Display name for the account (operator-facing).</summary>
    public required string DisplayName { get; set; }

    /// <summary>Provisioning / lifecycle state.</summary>
    public AccountStatus Status { get; set; } = AccountStatus.Provisioning;

    /// <summary>Density / isolation tier (default <see cref="AccountTier.Shared"/>).</summary>
    public AccountTier Tier { get; set; } = AccountTier.Shared;

    /// <summary>Shard hosting this account's tenant database.</summary>
    public Guid ShardId { get; set; }

    public Shard? Shard { get; set; }

    /// <summary>
    /// Reference into the secret store for this account's tenant DB connection
    /// string — NOT the raw string (keeps secrets out of the catalog and keeps
    /// data-residency clean).
    /// </summary>
    public required string ConnSecretRef { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }
}

/// <summary>Provisioning / lifecycle state of a <see cref="BusinessAccount"/>.</summary>
public enum AccountStatus
{
    /// <summary>Provisioning workflow in progress; the subdomain does not yet serve the app.</summary>
    Provisioning = 0,

    /// <summary>Normal operation; the subdomain serves the app.</summary>
    Active = 1,

    /// <summary>Read-only / sign-in disabled, data retained.</summary>
    Suspended = 2,

    /// <summary>Deprovisioning workflow in progress (database drop pending).</summary>
    Deprovisioning = 3,

    /// <summary>
    /// Breaking-change straddle in progress (per-account quiesce; slot-tier path, §13).
    /// Reserved — not used by the Phase 1/2 resolution + provisioning slice.
    /// </summary>
    Upgrading = 4,
}

/// <summary>
/// Density / isolation tier, chosen per account via the catalog (§6). The default
/// (<see cref="Shared"/>) is database-per-account on a shared Postgres server; the
/// connection string is the boundary, so no row-level discriminator exists.
/// </summary>
public enum AccountTier
{
    /// <summary>Many tenant databases on a shared Postgres server. Default.</summary>
    Shared = 0,

    /// <summary>A dedicated database on a dedicated server.</summary>
    DedicatedDb = 1,

    /// <summary>A dedicated server/instance for one account.</summary>
    DedicatedServer = 2,

    /// <summary>A fully isolated deployment (app + DB + infra).</summary>
    DedicatedDeployment = 3,
}
