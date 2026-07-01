using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Runs a per-tenant recurring job once per <b>active account</b> (§12, P3-2). In
/// multi-account mode the per-tenant jobs (audit retention, scheduled-deployment
/// dispatch, subscription poller, …) cannot run without a resolved account, so
/// Hangfire fires this fan-out instead: it enumerates the catalog and invokes each
/// job's <c>ExecuteAsync(CancellationToken)</c> inside a <c>WithAccount</c> scope, so
/// the job binds to that account's tenant database. A failure for one account is
/// logged and does not stop the others.
/// <para>
/// The job is identified by its assembly-qualified type name (a JSON-serializable
/// Hangfire argument); it is resolved/created via <see cref="ActivatorUtilities"/> and
/// invoked through its uniform <c>ExecuteAsync(CancellationToken)</c> method, so no
/// shared interface or per-job wiring is required.
/// </para>
/// </summary>
public sealed class PerAccountRecurringJobRunner(
    ICatalogStore catalog,
    ISecretStore secrets,
    IServiceScopeFactory scopeFactory,
    ILogger<PerAccountRecurringJobRunner> logger)
{
    public async Task RunForAllAccountsAsync(string jobTypeName, CancellationToken ct)
    {
        var jobType = Type.GetType(jobTypeName)
            ?? throw new InvalidOperationException(
                $"Recurring job type '{jobTypeName}' could not be resolved.");
        var executeAsync = jobType.GetMethod("ExecuteAsync", [typeof(CancellationToken)])
            ?? throw new InvalidOperationException(
                $"{jobType.Name} has no ExecuteAsync(CancellationToken) method.");

        var accounts = await catalog.ListAsync(AccountStatus.Active, ct).ConfigureAwait(false);

        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var connectionString = await secrets.ResolveAsync(account.ConnSecretRef, ct)
                    .ConfigureAwait(false);
                var resolved = new ResolvedAccount(
                    account.Id, account.Subdomain, account.ConnSecretRef, connectionString);

                await using var scope = scopeFactory.CreateAsyncScope();
                var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContext>();
                using (accountContext.WithAccount(resolved))
                {
                    var job = ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, jobType);
                    await ((Task)executeAsync.Invoke(job, [ct])!).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex, "Recurring job {Job} failed for account {Subdomain}.",
                    jobType.Name, account.Subdomain);
            }
        }
    }
}
