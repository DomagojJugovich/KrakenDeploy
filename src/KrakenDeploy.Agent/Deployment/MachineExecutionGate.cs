namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// F5 — this machine's execution gate: a fair asynchronous READER-WRITER lock
/// (Octopus <c>ScriptIsolationMutex</c> parity — Tentacle's isolation primitive is
/// an in-process <c>AsyncReaderWriterLock</c> and its <c>NoIsolation</c> option
/// takes the READ side of that same lock, so "bypass" is a downgrade to SHARED,
/// never an actual bypass).
/// <para>
/// Two modes, one queue:
/// <list type="bullet">
///   <item><see cref="Mode.Exclusive"/> (write) — the default for a dispatched
///         plan. Excludes every other holder, so a deployment's file / IIS /
///         service mutations cannot interleave with anything else on this box.</item>
///   <item><see cref="Mode.Shared"/> (read) — taken when the work declares
///         <c>AllowParallelTaskExecution</c>. Co-runs with other SHARED holders
///         only: consent is MUTUAL (locked decision P2), so one side opting in
///         still serializes against a writer that did not.</item>
/// </list>
/// </para>
/// <para>
/// A process-wide SINGLETON: <see cref="DeploymentExecutor"/>,
/// <see cref="Adhoc.AdhocScriptExecutor"/> and
/// <see cref="Services.AgentUpdateService"/> all take the same gate, which is what
/// makes the self-upgrade wait for EVERY kind of work rather than only for the
/// deployments <see cref="DeploymentExecutor.IsExecuting"/> can see (locked
/// decision P8 — the 2026-07-25 parallel-safety audit CLASH).
/// </para>
/// <para>
/// <b>Fairness / no writer starvation.</b> Acquisition never barges past a queued
/// waiter, even when the gate's current state would allow it. Without that rule a
/// steady stream of ad-hoc readers would keep <see cref="ReaderCount"/> above zero
/// indefinitely and a queued deployment (or the updater) would never be granted.
/// This is why the gate is hand-built rather than composed from
/// <see cref="SemaphoreSlim"/> pairs: <see cref="ReaderWriterLockSlim"/> has no
/// async surface at all, and the usual semaphore recipes either barge or need a
/// second lock to stay consistent.
/// </para>
/// <para>
/// Callers own only the POLICY — which mode their work takes, how long a bounded
/// wait lasts, what to log while queued and what to report on expiry — because the
/// deployment, ad-hoc and self-upgrade paths escalate differently. Acquisition
/// hands back an idempotent <see cref="Releaser"/> so a caller never has to pair an
/// <c>Exit()</c> with its own "did I take it?" flag.
/// </para>
/// </summary>
public sealed class MachineExecutionGate : IDisposable
{
    /// <summary>Which side of the gate a unit of work takes.</summary>
    public enum Mode
    {
        /// <summary>READ side: co-runs with other <see cref="Shared"/> holders,
        /// but is excluded by — and excludes — an <see cref="Exclusive"/> holder.</summary>
        Shared,

        /// <summary>WRITE side: excludes every other holder in both directions.</summary>
        Exclusive,
    }

    /// <summary>
    /// Default for <see cref="MaxSharedHolders"/>. Generous on purpose: it is a
    /// backstop against pathological fan-out, not a throughput knob, so it must sit
    /// well above any realistic number of co-running scripts on one box.
    /// </summary>
    internal const int DefaultMaxSharedHolders = 8;

    /// <summary>
    /// How many <see cref="Mode.Shared"/> holders may execute at once. The pre-F5
    /// primitive was a <c>SemaphoreSlim(1, 1)</c>, i.e. a hard cap of ONE unit of work
    /// per machine; reader-writer semantics removed that bound entirely, and nothing
    /// else replaces it — the ad-hoc path is shared-always, the agent's SignalR push
    /// handlers are detached with no limiter, and no server-side cap covers ad-hoc
    /// dispatch. Without a bound, N concurrently-approved scripts spawn N PowerShell
    /// processes on one target. Octopus's own reader-writer lock is uncapped, so this
    /// is a deliberate divergence: a shared holder beyond the cap QUEUES like any other
    /// waiter rather than being refused, so the only visible effect is that a
    /// pathological burst serializes instead of exhausting the box.
    /// </summary>
    /// <remarks>Floored at 1: a zero or negative cap would make every shared
    /// acquisition permanently unsatisfiable, i.e. a config typo would deadlock the
    /// whole ad-hoc path rather than merely narrow it.</remarks>
    public int MaxSharedHolders
    {
        get => _maxSharedHolders;
        init => _maxSharedHolders = Math.Max(1, value);
    }

    private readonly int _maxSharedHolders = DefaultMaxSharedHolders;

    private readonly object _sync = new();

    /// <summary>Waiters in arrival order. The HEAD decides what may be granted:
    /// nothing behind it is allowed to overtake it (see the fairness note on the
    /// class).</summary>
    private readonly LinkedList<Waiter> _queue = new();

    private int _readers;
    private bool _writer;
    private bool _disposed;

    /// <summary>Whether ANY holder occupies the gate — a writer or at least one
    /// reader. Observability + test assertions; do NOT gate acquisition on it
    /// (that would race).</summary>
    public bool IsHeld
    {
        get { lock (_sync) { return _writer || _readers > 0; } }
    }

    /// <summary>How many <see cref="Mode.Shared"/> holders are executing right
    /// now. Zero while a writer holds the gate.</summary>
    public int ReaderCount
    {
        get { lock (_sync) { return _readers; } }
    }

    /// <summary>Whether an <see cref="Mode.Exclusive"/> holder occupies the gate.</summary>
    public bool IsWriteHeld
    {
        get { lock (_sync) { return _writer; } }
    }

    /// <summary>Waiters queued but not yet granted. Observability only.</summary>
    public int QueuedCount
    {
        get { lock (_sync) { return _queue.Count; } }
    }

    /// <summary>
    /// Non-blocking probe + take. Returns the lease, or <c>null</c> when the gate
    /// cannot be granted right now — either because an incompatible holder occupies
    /// it OR because somebody is already queued (taking it ahead of them would be
    /// the barge this gate exists to prevent). The caller should announce the wait
    /// before blocking. Completes synchronously by construction: the
    /// <see cref="TimeSpan.Zero"/> path never enqueues and never parks.
    /// </summary>
    public Task<Releaser?> TryAcquireNowAsync(Mode mode, CancellationToken ct)
        => AcquireCoreAsync(mode, TimeSpan.Zero, ct);

    /// <summary>
    /// Bounded wait. <c>null</c> on expiry (the gate was NOT taken); throws
    /// <see cref="OperationCanceledException"/> if <paramref name="ct"/> fires
    /// while queued — the caller's work was cancelled with nothing executed and
    /// nothing to release. Throws <see cref="ObjectDisposedException"/> if the gate
    /// is disposed while queued (host shutdown racing DI disposal).
    /// <para>
    /// <see cref="TimeSpan.Zero"/> behaves as <see cref="TryAcquireNowAsync"/>;
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits indefinitely.
    /// </para>
    /// </summary>
    public Task<Releaser?> AcquireAsync(Mode mode, TimeSpan timeout, CancellationToken ct)
        => AcquireCoreAsync(mode, timeout, ct);

    /// <summary>Unbounded fair wait; observes <paramref name="ct"/>.</summary>
    public async Task<Releaser> AcquireAsync(Mode mode, CancellationToken ct)
        => await AcquireCoreAsync(mode, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException(
               "MachineExecutionGate: an unbounded acquisition returned without a lease.");

    /// <summary>
    /// Longest bounded wait <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// accepts (<see cref="int.MaxValue"/> ms ≈ 24.8 days). A caller asking for more is
    /// CLAMPED rather than faulted: at that scale a bounded wait and an unbounded one
    /// are indistinguishable operationally, whereas throwing would fail the acquisition
    /// — and therefore the deployment or the self-upgrade — over a config typo. Mirrors
    /// the F2-followup 5 decision to cap every <c>CancelAfter</c> arm at the timer limit
    /// instead of letting it throw at dispatch.
    /// </summary>
    private static readonly TimeSpan MaxBoundedWait =
        TimeSpan.FromMilliseconds(int.MaxValue);

    private async Task<Releaser?> AcquireCoreAsync(
        Mode mode, TimeSpan timeout, CancellationToken ct)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout,
                "A negative gate timeout is only meaningful as Timeout.InfiniteTimeSpan.");
        }
        if (timeout > MaxBoundedWait)
        {
            timeout = MaxBoundedWait;
        }

        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Fairness first, compatibility second: an empty queue is a precondition
            // for taking the gate directly. See the class-level fairness note.
            if (_queue.Count == 0 && CanGrantNoLock(mode))
            {
                TakeNoLock(mode);
                return new Releaser(this, mode);
            }

            if (timeout == TimeSpan.Zero)
            {
                return null; // probe only — never enqueue
            }

            waiter = new Waiter(mode);
            waiter.Node = _queue.AddLast(waiter);
        }

        bool granted;
        try
        {
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                expiry.CancelAfter(timeout);
            }

            // The callback only COMPLETES the waiter; leaving the queue happens below
            // under _sync, where it is ordered against a racing grant. Disposing the
            // registration before awaiting the lock keeps the callback out of it.
            using var registration = expiry.Token.Register(
                static state => ((Waiter)state!).Tcs.TrySetResult(false), waiter);

            granted = await waiter.Tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            // Defence in depth, deliberately unreachable today. A waiter left in the
            // queue with an uncompleted TCS is FATAL: DrainNoLock would later dequeue
            // it, take the slot on its behalf, succeed at TrySetResult(true) because
            // nobody had completed it, then find no continuation to hand the lease to.
            // The gate would be held by NOBODY, forever — an Exclusive phantom wedges
            // every later deployment and ad-hoc script behind the unbounded wait, a
            // Shared one makes Exclusive permanently ungrantable, and neither survives
            // without restarting the agent.
            // Nothing between the enqueue and the await currently throws: the timeout is
            // validated and clamped BEFORE the enqueue, and a token whose
            // CancellationTokenSource has been disposed is verified (.NET 10) NOT to
            // throw from CreateLinkedTokenSource, CancelAfter or Register — it simply
            // never fires. So this catch exists for the next edit, not for today: any
            // statement added in this window inherits an unrecoverable failure mode,
            // and that is too sharp an edge to leave unguarded.
            LeaveQueue(waiter);
            throw;
        }

        if (granted)
        {
            // DrainNoLock already mutated the gate's state on our behalf, so we hold
            // the lease regardless of what fired afterwards. If the CALLER's own token
            // cancelled we must not execute: release at once — handing the gate to the
            // next waiter — and surface the cancellation holding nothing.
            var lease = new Releaser(this, mode);
            if (ct.IsCancellationRequested)
            {
                lease.Dispose();
                ct.ThrowIfCancellationRequested();
            }
            return lease;
        }

        // We gave up (expiry, caller cancel, or disposal).
        LeaveQueue(waiter);

        // Cancellation first: it is the more specific reason, and a caller that
        // cancelled wants OperationCanceledException rather than a null "expired".
        ct.ThrowIfCancellationRequested();

        // Disposal is only the reason when the waiter was still QUEUED at the moment
        // Dispose() ran — Dispose() nulls Node for everything it strands, so a waiter
        // whose bounded wait had ALREADY expired keeps Node null for its own reason and
        // must still report expiry. Distinguishing them matters: relabelling a genuine
        // expiry as a disposal skips the callers' escalation paths (the wedged-gate
        // report in DeploymentExecutor, SwapGate.Busy in AgentUpdateService).
        ObjectDisposedException.ThrowIf(waiter.StrandedByDispose, this);
        return null; // the bounded wait expired
    }

    /// <summary>
    /// Takes <paramref name="waiter"/> out of the queue after it has given up, and
    /// re-drains: removing a HEAD writer can unblock readers that were correctly queued
    /// behind it, and making them wait for the current holder to release instead would
    /// be a needless stall. A racing grant either has not happened — we remove
    /// ourselves — or lost the TCS race and already rolled itself back in
    /// <see cref="DrainNoLock"/>, in which case <c>Node</c> is already null.
    /// </summary>
    private void LeaveQueue(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node is { } node)
            {
                _queue.Remove(node);
                waiter.Node = null;
                if (!_disposed)
                {
                    DrainNoLock();
                }
            }
        }
    }

    // ── State machine (all NoLock members require _sync) ──────────────────────

    private bool CanGrantNoLock(Mode mode)
        => mode == Mode.Shared
            ? !_writer && _readers < MaxSharedHolders
            : !_writer && _readers == 0;

    private void TakeNoLock(Mode mode)
    {
        if (mode == Mode.Shared)
        {
            _readers++;
        }
        else
        {
            _writer = true;
        }
    }

    /// <summary>
    /// Gives the mode's slot back. THROWS on an unheld release: a corrupted counter
    /// would silently make <see cref="Mode.Exclusive"/> ungrantable forever (readers
    /// stuck below zero), which is far worse than a loud failure. Unreachable via
    /// <see cref="Releaser"/> — its <see cref="Interlocked"/> guard makes a
    /// double-dispose a no-op — so this only fires on a genuine ownership bug.
    /// </summary>
    private void ReleaseNoLock(Mode mode)
    {
        if (mode == Mode.Shared)
        {
            if (_readers == 0)
            {
                throw new InvalidOperationException(
                    "MachineExecutionGate: Shared release with no reader held.");
            }
            _readers--;
        }
        else
        {
            if (!_writer)
            {
                throw new InvalidOperationException(
                    "MachineExecutionGate: Exclusive release with no writer held.");
            }
            _writer = false;
        }
    }

    /// <summary>
    /// Hands the gate to as many head-of-queue waiters as its state allows. Stops at
    /// the first waiter that cannot be granted — never looks past it, which is the
    /// fairness rule that keeps a writer from being starved by later readers.
    /// </summary>
    private void DrainNoLock()
    {
        while (_queue.First is { } node)
        {
            var waiter = node.Value;
            if (!CanGrantNoLock(waiter.Mode))
            {
                return;
            }

            _queue.Remove(node);
            waiter.Node = null;
            TakeNoLock(waiter.Mode);

            if (!waiter.Tcs.TrySetResult(true))
            {
                // The waiter expired or cancelled between our decision and this
                // completion. Undo the grant — otherwise the gate stays "held" by
                // nobody — and keep draining.
                ReleaseNoLock(waiter.Mode);
            }
        }
    }

    private void Release(Mode mode)
    {
        lock (_sync)
        {
            // After disposal the accounting no longer matters (nothing can be granted
            // again) and nobody is left to hand the slot to, so returning early keeps
            // the documented "releasing after disposal is a no-op" contract literally
            // true — including for a lease whose slot Dispose has conceptually
            // abandoned, which would otherwise trip ReleaseNoLock's invariant throw.
            if (_disposed)
            {
                return;
            }
            ReleaseNoLock(mode);
            DrainNoLock();
        }
    }

    /// <summary>
    /// App-lifetime singleton; the DI container disposes it at shutdown. Every
    /// queued waiter is completed so it unwinds with an
    /// <see cref="ObjectDisposedException"/> rather than parking forever — the
    /// failure mode the old <see cref="SemaphoreSlim"/> implementation had (its
    /// <c>Dispose</c> does not signal pending waiters), which callers had to work
    /// around by linking the host-shutdown token into every wait. They still do, so
    /// the normal shutdown path unwinds cleanly; this is the backstop for the racy
    /// one. Releasing a lease AFTER disposal is a genuine no-op (see
    /// <see cref="Release"/>), so an in-flight run unwinding into its <c>finally</c>
    /// never faults on a gate the host has already torn down.
    /// </summary>
    public void Dispose()
    {
        List<Waiter>? stranded = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_queue.Count > 0)
            {
                stranded = [.. _queue];
                _queue.Clear();
                foreach (var waiter in stranded)
                {
                    waiter.Node = null;
                    waiter.StrandedByDispose = true;
                }
            }
        }

        if (stranded is not null)
        {
            foreach (var waiter in stranded)
            {
                waiter.Tcs.TrySetResult(false);
            }
        }
    }

    private sealed class Waiter(Mode mode)
    {
        public Mode Mode { get; } = mode;

        /// <summary><c>true</c> = granted (the gate's state is ALREADY mutated on
        /// this waiter's behalf); <c>false</c> = the waiter gave up.
        /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> is
        /// load-bearing: <see cref="DrainNoLock"/> completes this while holding
        /// <c>_sync</c>, and an inline continuation could re-enter the lock.</summary>
        public TaskCompletionSource<bool> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>This waiter's queue node while queued; <c>null</c> once
        /// dequeued. Read and written under <c>_sync</c> only.</summary>
        public LinkedListNode<Waiter>? Node { get; set; }

        /// <summary>Set by <see cref="Dispose"/> when it strands this waiter, so the
        /// waiter reports disposal rather than expiry. Written and read under
        /// <c>_sync</c> only — a plain <c>_disposed</c> check could not tell the two
        /// apart, because a waiter whose bounded wait expired FIRST also unwinds after
        /// disposal has been observed.</summary>
        public bool StrandedByDispose { get; set; }
    }

    /// <summary>
    /// Idempotent lease — disposing gives the mode's slot back exactly once and
    /// hands the gate to whatever queued waiters that unblocks. Safe to dispose a
    /// <c>null</c> lease, which is what lets call sites use a plain <c>using</c>
    /// instead of tracking a "did I take it?" flag.
    /// </summary>
    public sealed class Releaser : IDisposable
    {
        private readonly MachineExecutionGate _gate;
        private int _released;

        internal Releaser(MachineExecutionGate gate, Mode mode)
        {
            _gate = gate;
            Mode = mode;
        }

        /// <summary>Which side of the gate this lease holds.</summary>
        public Mode Mode { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return; // already released — idempotent by contract
            }

            try
            {
                _gate.Release(Mode);
            }
            catch
            {
                // Restore the flag so the slot can still be handed back by a later
                // Dispose. Marking the lease consumed BEFORE the release succeeded
                // would make a throwing release permanent: the slot is never returned
                // and the idempotency guard turns every retry into a no-op, so the gate
                // silently loses a reader slot (or stays write-held) for the life of
                // the process.
                Interlocked.Exchange(ref _released, 0);
                throw;
            }
        }
    }
}
