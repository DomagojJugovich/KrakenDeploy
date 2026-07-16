namespace KrakenDeploy.Server.Transport;

/// <summary>
/// B7 — the node task cap: bounds how many deployment orchestrations run
/// concurrently on this node (<c>Engine:MaxConcurrentTasks</c>, Octopus-parity
/// default 5). Pre-B7 the worker fire-and-forgot every dequeued item, so an
/// enqueue burst ran unbounded concurrent orchestrations — each holding DB
/// contexts, a log sequencer and per-target dispatch state for its whole
/// duration.
/// <para>
/// Excess items wait on the semaphore; async waiters are served FIFO, so
/// queued deployments start in dispatch order. A waiter holds NO other
/// resource while queued — in particular it does not count toward the
/// blue-green in-flight gauge, because a queued-but-unstarted deployment is
/// still <c>Queued</c> in the database and the B1 claim + reconciler hand it
/// to the surviving slot if this node retires first.
/// </para>
/// </summary>
public sealed class NodeTaskGate(int maxConcurrentTasks)
{
    public const int DefaultMaxConcurrentTasks = 5;

    private readonly SemaphoreSlim _slots = new(
        maxConcurrentTasks > 0 ? maxConcurrentTasks : DefaultMaxConcurrentTasks,
        maxConcurrentTasks > 0 ? maxConcurrentTasks : DefaultMaxConcurrentTasks);

    /// <summary>Configured capacity (non-positive input falls back to the default).</summary>
    public int Capacity { get; } =
        maxConcurrentTasks > 0 ? maxConcurrentTasks : DefaultMaxConcurrentTasks;

    /// <summary>Slots currently held — observability + test assertions.</summary>
    public int InUse => Capacity - _slots.CurrentCount;

    public async Task<Releaser> AcquireAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_slots);
    }

    /// <summary>Idempotent releaser — dispose exactly returns the one slot.</summary>
    public sealed class Releaser(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
