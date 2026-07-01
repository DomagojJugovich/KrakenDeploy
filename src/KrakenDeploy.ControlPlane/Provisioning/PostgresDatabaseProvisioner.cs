using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace KrakenDeploy.ControlPlane.Provisioning;

/// <inheritdoc />
public sealed partial class PostgresDatabaseProvisioner(
    ILogger<PostgresDatabaseProvisioner> logger) : IDatabaseProvisioner
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex IdentifierRegex();

    public async Task<bool> DatabaseExistsAsync(
        string adminConnectionString, string databaseName, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn);
        cmd.Parameters.AddWithValue("n", databaseName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    public async Task CreateDatabaseAsync(
        string adminConnectionString, string databaseName, CancellationToken ct = default)
    {
        GuardIdentifier(databaseName);

        if (await DatabaseExistsAsync(adminConnectionString, databaseName, ct).ConfigureAwait(false))
        {
            logger.LogInformation("Tenant database {Database} already exists; skipping create.", databaseName);
            return;
        }

        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        // Identifier is validated by GuardIdentifier; quote it. CREATE DATABASE
        // cannot be parameterized or wrapped in a transaction.
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Created tenant database {Database}.", databaseName);
    }

    public async Task DropDatabaseAsync(
        string adminConnectionString, string databaseName, CancellationToken ct = default)
    {
        GuardIdentifier(databaseName);

        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        // WITH (FORCE) terminates remaining connections (Postgres 13+) so a
        // compensating drop succeeds even if a tenant connection lingers.
        await using var cmd = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Dropped tenant database {Database}.", databaseName);
    }

    public string BuildTenantConnectionString(string adminConnectionString, string databaseName)
    {
        GuardIdentifier(databaseName);
        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
        };
        return builder.ConnectionString;
    }

    private static void GuardIdentifier(string databaseName)
    {
        if (!IdentifierRegex().IsMatch(databaseName))
        {
            throw new ArgumentException(
                $"Refusing to use unsafe database identifier '{databaseName}'.", nameof(databaseName));
        }
    }
}
