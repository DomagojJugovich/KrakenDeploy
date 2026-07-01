using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Applies pending EF migrations to every active tenant database (§12). Enumerates
/// accounts from the catalog, migrates each against its own connection, and reports
/// per-account success/failure so a database that fails (drift) surfaces loudly
/// rather than diverging silently. EF's <c>MigrateAsync</c> applies only pending
/// migrations, so this is idempotent.
/// </summary>
public sealed class FleetMigrationOrchestrator(
    ICatalogStore catalog,
    ISecretStore secrets,
    TenantInitializer initializer,
    ILogger<FleetMigrationOrchestrator> logger)
{
    public async Task<FleetMigrationReport> MigrateAllAsync(CancellationToken ct = default)
    {
        var accounts = await catalog.ListAsync(AccountStatus.Active, ct).ConfigureAwait(false);
        var migrated = new List<string>();
        var failures = new List<FleetMigrationFailure>();

        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var conn = await secrets.ResolveAsync(account.ConnSecretRef, ct).ConfigureAwait(false);
                var resolved = new ResolvedAccount(account.Id, account.Subdomain, account.ConnSecretRef, conn);
                await initializer.MigrateAsync(resolved, ct).ConfigureAwait(false);
                migrated.Add(account.Subdomain);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fleet migration failed for account {Subdomain}.", account.Subdomain);
                failures.Add(new FleetMigrationFailure(account.Subdomain, ex.Message));
            }
        }

        var report = new FleetMigrationReport(migrated, failures);
        if (failures.Count > 0)
        {
            logger.LogError(
                "Fleet migration finished: {Migrated} migrated, {Failed} FAILED — drift must be resolved.",
                migrated.Count, failures.Count);
        }
        else
        {
            logger.LogInformation("Fleet migration finished: {Migrated} account(s) up to date.", migrated.Count);
        }

        return report;
    }
}

public sealed record FleetMigrationFailure(string Subdomain, string Error);

public sealed record FleetMigrationReport(
    IReadOnlyList<string> Migrated,
    IReadOnlyList<FleetMigrationFailure> Failures)
{
    public bool AllSucceeded => Failures.Count == 0;
}
