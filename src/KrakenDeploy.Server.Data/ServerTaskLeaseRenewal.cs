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
    // E2 — cancelled when a renewal attempt finds no Running row to renew: the
    // task went terminal or the reconciler failed it as orphaned. The owning
    // orchestration links this into its own cancellation so it tears down
    // cleanly instead of dispatching further waves LEASELESS (a run whose lease
    // the reconciler already reclaimed must not keep pushing work).
    private readonly CancellationTokenSource _leaseLostCts = new();
    private readonly Task _loop;

    /// <summary>
    /// Fires when the lease is definitively lost — a renewal attempt matched no
    /// <c>Running</c> row (terminal transition or reconciler orphan-fail). Does
    /// NOT fire on transient DB errors: those are retried and the lease survives
    /// up to <c>(LeaseDuration / RenewInterval) - 1</c> consecutive misses. The
    /// worker links this token into the orchestration's cancellation.
    /// </summary>
    public CancellationToken LeaseLost => _leaseLostCts.Token;

    /// <param name="renewInterval">How often to renew; defaults to
    /// <see cref="ServerTaskLease.RenewInterval"/>. A short interval is used by
    /// tests so a lease-loss teardown runs in milliseconds, not a minute.</param>
    public ServerTaskLeaseRenewal(
        IServiceScopeFactory scopeFactory,
        Guid taskId,
        TimeProvider time,
        ILogger logger,
        TimeSpan? renewInterval = null)
    {
        _loop = RenewLoopAsync(
            scopeFactory, taskId, time, logger,
            renewInterval is { } ri && ri > TimeSpan.Zero ? ri : ServerTaskLease.RenewInterval,
            _leaseLostCts, _cts.Token);
    }

    private static async Task RenewLoopAsync(
        IServiceScopeFactory scopeFactory,
        Guid taskId,
        TimeProvider time,
        ILogger logger,
        TimeSpan renewInterval,
        CancellationTokenSource leaseLostCts,
        CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(renewInterval, time);
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
                            "a terminal state or was reconciled as orphaned; signalling the " +
                            "orchestration to tear down and stopping renewals.",
                            taskId);
                        // E2: tell the owning orchestration the lease is gone so it
                        // stops dispatching leaseless. Best-effort: if the source was
                        // already disposed (racing DisposeAsync) the signal is moot.
                        try { await leaseLostCts.CancelAsync().ConfigureAwait(false); }
                        catch (ObjectDisposedException) { }
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
        _leaseLostCts.Dispose();
    }
}
