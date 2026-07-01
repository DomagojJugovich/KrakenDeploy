using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.ControlPlane.Provisioning;

/// <inheritdoc />
public sealed class AccountProvisioner(
    ICatalogStore catalog,
    IDatabaseProvisioner database,
    IDnsProvisioner dns,
    ISecretStore secrets,
    TenantInitializer initializer,
    ILogger<AccountProvisioner> logger) : IAccountProvisioner
{
    public async Task<ProvisioningResult> ProvisionAsync(NewAccountRequest req, CancellationToken ct = default)
    {
        // 1. Validate the subdomain (format + reserved + uniqueness).
        var (subdomain, error) = SubdomainPolicy.Normalize(req.Subdomain);
        if (subdomain is null)
        {
            return ProvisioningResult.Fail(error!);
        }

        if (await catalog.SubdomainExistsAsync(subdomain, ct).ConfigureAwait(false))
        {
            return ProvisioningResult.Fail($"Subdomain '{subdomain}' is already taken.");
        }

        // 2. Select a shard with capacity.
        Shard shard;
        try
        {
            shard = await catalog.SelectShardAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ProvisioningResult.Fail(ex.Message);
        }

        var adminConn = await secrets.ResolveAsync(shard.HostSecretRef, ct).ConfigureAwait(false);
        var dbName = DatabaseName(subdomain);
        var accountId = Guid.CreateVersion7();
        var connSecretRef = $"acct/{accountId:N}";
        var tenantConn = database.BuildTenantConnectionString(adminConn, dbName);
        var resolved = new ResolvedAccount(accountId, subdomain, connSecretRef, tenantConn);

        var dbCreated = false;
        var secretStored = false;
        var catalogRegistered = false;

        try
        {
            // 3. Create the tenant database + store its connection secret.
            await database.CreateDatabaseAsync(adminConn, dbName, ct).ConfigureAwait(false);
            dbCreated = true;

            await secrets.StoreAsync(connSecretRef, tenantConn, ct).ConfigureAwait(false);
            secretStored = true;

            // 4. Register the catalog row (status = Provisioning).
            await catalog.AddAsync(new BusinessAccount
            {
                Id = accountId,
                Subdomain = subdomain,
                DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? subdomain : req.DisplayName,
                Status = AccountStatus.Provisioning,
                Tier = AccountTier.Shared,
                ShardId = shard.Id,
                ConnSecretRef = connSecretRef,
                CreatedUtc = DateTimeOffset.UtcNow,
                ModifiedUtc = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            catalogRegistered = true;

            // 5. Migrate schema, seed defaults, create the first admin.
            await initializer.MigrateAsync(resolved, ct).ConfigureAwait(false);
            await initializer.SeedAsync(resolved, req, ct).ConfigureAwait(false);

            // 6. DNS (no-op under wildcard).
            await dns.ConfigureAsync(subdomain, ct).ConfigureAwait(false);

            // 7. Flip to Active — the subdomain now serves the app.
            await catalog.SetStatusAsync(accountId, AccountStatus.Active, ct).ConfigureAwait(false);

            logger.LogInformation("Provisioned account {Subdomain} ({AccountId}).", subdomain, accountId);
            return ProvisioningResult.Ok(accountId, subdomain);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Provisioning '{Subdomain}' failed; compensating in reverse.", subdomain);
            await CompensateAsync(accountId, connSecretRef, adminConn, dbName, subdomain,
                catalogRegistered, secretStored, dbCreated).ConfigureAwait(false);
            return ProvisioningResult.Fail(ex.Message);
        }
    }

    private async Task CompensateAsync(
        Guid accountId, string connSecretRef, string adminConn, string dbName, string subdomain,
        bool catalogRegistered, bool secretStored, bool dbCreated)
    {
        // Best-effort teardown; each step is independent and logged.
        if (catalogRegistered)
        {
            await SafeAsync(() => catalog.RemoveAsync(accountId), "remove catalog row").ConfigureAwait(false);
        }

        if (secretStored)
        {
            await SafeAsync(() => secrets.RemoveAsync(connSecretRef), "revoke connection secret").ConfigureAwait(false);
        }

        if (dbCreated)
        {
            await SafeAsync(() => database.DropDatabaseAsync(adminConn, dbName), "drop tenant database").ConfigureAwait(false);
        }

        await SafeAsync(() => dns.RemoveAsync(subdomain), "remove DNS record").ConfigureAwait(false);
    }

    private async Task SafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Compensation step '{What}' failed.", what);
        }
    }

    private static string DatabaseName(string subdomain) =>
        "kraken_acct_" + subdomain.Replace('-', '_');
}
