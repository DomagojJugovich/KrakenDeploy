namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Creates and drops a tenant database on a shard's Postgres server. <c>CREATE</c> /
/// <c>DROP DATABASE</c> can't run in a transaction or via EF, so this talks raw
/// Npgsql against the shard admin connection. All operations are idempotent.
/// </summary>
public interface IDatabaseProvisioner
{
    Task<bool> DatabaseExistsAsync(string adminConnectionString, string databaseName, CancellationToken ct = default);

    Task CreateDatabaseAsync(string adminConnectionString, string databaseName, CancellationToken ct = default);

    Task DropDatabaseAsync(string adminConnectionString, string databaseName, CancellationToken ct = default);

    /// <summary>Derives the tenant connection string (admin connection + target database).</summary>
    string BuildTenantConnectionString(string adminConnectionString, string databaseName);
}
