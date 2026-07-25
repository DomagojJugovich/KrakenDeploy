namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// B7/F2 — this machine's single execution slot (Octopus tentacle-mutex parity):
/// ONE unit of work executes at a time on this agent, FIFO for async waiters, so
/// concurrent deployments, runbook runs and ad-hoc scripts hitting the same box
/// serialize instead of interleaving file / IIS / service mutations.
/// <para>
/// A process-wide SINGLETON, extracted from <see cref="DeploymentExecutor"/> by
/// F2 so the ad-hoc path can share the very same slot — before F2 ad-hoc scripts
/// bypassed the gate entirely and could run a diagnostic script straight into a
/// deployment's file operations.
/// </para>
/// <para>
/// Deliberately mirrors <c>NodeTaskGate</c> (the server's task cap): acquisition
/// hands back an idempotent <see cref="Releaser"/> rather than requiring the
/// caller to pair an <c>Exit()</c> with its own "did I take it?" flag. Callers own
/// only the POLICY — whether the target's <c>AllowParallelTaskExecution</c>
/// bypasses the gate, how long a bounded wait lasts, what to log while queued and
/// what to report on expiry — because the deployment and ad-hoc paths escalate
/// differently.
/// </para>
/// </summary>
public sealed class MachineExecutionGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Whether the slot is currently held. Observability + test
    /// assertions; do NOT gate acquisition on it (that would race).</summary>
    public bool IsHeld => _gate.CurrentCount == 0;

    /// <summary>
    /// Non-blocking probe + take. Returns the slot's <see cref="Releaser"/>, or
    /// <c>null</c> when someone else holds it — the caller should announce the wait
    /// before blocking. Completes synchronously by construction
    /// (<c>WaitAsync(TimeSpan.Zero)</c> never parks).
    /// </summary>
    public async Task<Releaser?> TryAcquireNowAsync(CancellationToken ct)
        => await _gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)
            ? new Releaser(_gate)
            : null;

    /// <summary>
    /// Bounded wait. <c>null</c> on expiry (the slot was NOT taken); throws
    /// <see cref="OperationCanceledException"/> if <paramref name="ct"/> fires
    /// while queued — the caller's work was cancelled with nothing executed.
    /// </summary>
    public async Task<Releaser?> AcquireAsync(TimeSpan timeout, CancellationToken ct)
        => await _gate.WaitAsync(timeout, ct).ConfigureAwait(false)
            ? new Releaser(_gate)
            : null;

    /// <summary>Unbounded FIFO wait; observes <paramref name="ct"/>.</summary>
    public async Task<Releaser> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    /// <summary>App-lifetime singleton; the DI container disposes it at shutdown
    /// (releases the semaphore's wait handle).</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Idempotent releaser — disposing hands the machine to the next queued
    /// waiter exactly once. Safe to dispose a <c>null</c> lease (a caller that
    /// bypassed the gate holds none), which is what lets both call sites use a
    /// plain <c>using</c> instead of tracking a "did I take it?" flag.
    /// </summary>
    public sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                // Disposed-guard: host shutdown can dispose the gate while an
                // in-flight run is still unwinding toward this release.
                try { gate.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
    }
}
