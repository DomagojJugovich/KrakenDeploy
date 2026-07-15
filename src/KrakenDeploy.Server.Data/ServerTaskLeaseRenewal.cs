using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// B1 — keeps a claimed task's lease alive for the duration of one in-flight
/// dispatch. Created right after a successful <see cref="ServerTaskLease.TryClaimAsync"/>;
/// disposal stops the renewal. Each tick opens its own DI scope (the dispatch's
/// main <c>KrakenDbContext</c> is busy orchestrating and EF contexts are not
/// concurrency-safe); the ambient account context flows into that scope via
/// <c>AsyncLocal</c>, so renewals hit the right tenant database.
/// <para>
/// A failed renewal is logged but never interrupts the dispatch: either the
/// task reached a terminal state moments ago (benign), or the process stalled
/// past the whole lease and the reconciler failed the task as orphaned — in
/// which case the terminal-state guards on the final writes keep this stale
/// dispatch from overwriting that verdict.
/// </para>
/// </summary>
public sealed class ServerTaskLeaseRenewal : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public ServerTaskLeaseRenewal(
        IServiceScopeFactory scopeFactory,
        Guid taskId,
        TimeProvider time,
        ILogger logger)
    {
        _loop = RenewLoopAsync(scopeFactory, taskId, time, logger, _cts.Token);
    }

    private static async Task RenewLoopAsync(
        IServiceScopeFactory scopeFactory,
        Guid taskId,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(ServerTaskLease.RenewInterval, time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
                    if (!await ServerTaskLease.TryRenewAsync(db, taskId, time, ct).ConfigureAwait(false))
                    {
                        logger.LogWarning(
                            "Lease renewal for task {TaskId} matched no Running row — it reached " +
                            "a terminal state or was reconciled as orphaned; renewals stop.",
                            taskId);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Transient DB error — keep trying; the lease survives
                    // (LeaseDuration / RenewInterval) - 1 consecutive misses.
                    logger.LogWarning(ex,
                        "Lease renewal for task {TaskId} failed; retrying next tick.", taskId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop via DisposeAsync.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Loop observed the cancel between ticks.
        }
        _cts.Dispose();
    }
}
