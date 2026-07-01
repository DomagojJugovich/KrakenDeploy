using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.ControlPlane.Provisioning;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Data.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Hangfire;

/// <summary>
/// Multi-account scheduled-backup orchestration. Each account runs its OWN backup of its
/// OWN tenant database on its OWN cron via a per-account Hangfire recurring job
/// (<c>kraken.backup:{accountId}</c>). This type is both the job body (resolve the account,
/// run the backup under <c>WithAccount</c>) and the startup reconciler (register one
/// per-account job for each active account whose <c>BackupSettings</c> enable a schedule).
/// Only used when <c>MultiAccount:Enabled</c>; single-instance keeps the single
/// <c>kraken.backup</c> job that runs <see cref="BackupJob"/> directly.
/// <para>
/// Injects only singleton-safe services (<see cref="IServiceScopeFactory"/> + logger) and
/// resolves scoped services from child scopes, so it carries no captive dependency.
/// </para>
/// </summary>
public sealed class AccountBackupRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<AccountBackupRunner> logger)
{
    /// <summary>
    /// Hangfire job body for <c>kraken.backup:{accountId}</c>: resolve the account and run
    /// its backup under <c>WithAccount</c> so <c>BackupService</c>/<c>BackupEngine</c> bind
    /// to the tenant DB. Reuses <see cref="BackupJob.ExecuteAsync"/> so the maintenance-pause
    /// guard and history-row write are identical to single-instance.
    /// </summary>
    public async Task RunForAccountAsync(Guid accountId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var account = await scope.ServiceProvider
            .GetRequiredService<IAccountResolver>()
            .ResolveByIdAsync(accountId, ct)
            .ConfigureAwait(false);
        if (account is null)
        {
            logger.LogWarning(
                "Scheduled backup skipped: account {AccountId} is not active / not found.", accountId);
            return;
        }

        using (scope.ServiceProvider.GetRequiredService<IAccountContext>().WithAccount(account))
        {
            var job = ActivatorUtilities.GetServiceOrCreateInstance<BackupJob>(scope.ServiceProvider);
            await job.ExecuteAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Startup reconcile: align each active account's per-account backup recurring job with
    /// its persisted <c>BackupSettings</c> (the settings page does the same per account on
    /// save). Mirrors the single-instance startup <c>ApplyAsync</c>, fanned out per account.
    /// </summary>
    public async Task ReconcileSchedulesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogStore>();
        var resolver = scope.ServiceProvider.GetRequiredService<IAccountResolver>();
        var accounts = await catalog.ListAsync(AccountStatus.Active, ct).ConfigureAwait(false);

        foreach (var account in accounts)
        {
            try
            {
                var resolved = await resolver.ResolveByIdAsync(account.Id, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    continue;
                }

                await using var accountScope = scopeFactory.CreateAsyncScope();
                using (accountScope.ServiceProvider.GetRequiredService<IAccountContext>().WithAccount(resolved))
                {
                    await accountScope.ServiceProvider
                        .GetRequiredService<BackupScheduler>()
                        .ApplyAsync(ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to reconcile backup schedule for account {AccountId}.", account.Id);
            }
        }
    }
}
