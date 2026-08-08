using Microsoft.Extensions.Logging;

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
    /// <summary>
    /// Optional sink for the ONE thing the gate cannot report through its return values or
    /// its exceptions: a holder-accounting failure while unwinding another exception. Set by
    /// <c>MachineExecutionGateRegistration</c>, which already resolves this logger.
    /// <para>
    /// The fault used to be stuffed into <c>Exception.Data</c> on the exception being
    /// rethrown, on the theory that every caller logs what it receives. It does — but
    /// <c>{Exception}</c> renders <c>ToString()</c>, which does NOT include <c>Data</c>, so
    /// no configured sink could ever show it and ~20 lines of machinery surfaced nothing.
    /// A slot leaked into a gate that then blocks every deployment and ad-hoc script on the
    /// box is exactly the thing that must not be silent.
    /// </para>
    /// </summary>
    public ILogger? Logger { get; init; }

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
    /// <remarks>
    /// CLAMPED to <c>[1, <see cref="MaxAllowedSharedHolders"/>]</c> rather than trusted.
    /// A zero or negative cap would make every shared acquisition permanently
    /// unsatisfiable — a config typo deadlocking the whole ad-hoc path rather than
    /// narrowing it. An absurdly LARGE one is the mirror-image mistake and was the
    /// dangerous direction: it silently reinstates the unbounded fan-out this cap exists
    /// to prevent, so `9999` would put 9999 PowerShell processes on one target while a
    /// too-long check interval merely refuses to boot. Both ends therefore clamp here
    /// unconditionally, and <see cref="MachineExecutionGateRegistration"/> logs a warning
    /// when the configured value had to be clamped — silently substituting a different cap
    /// than the operator asked for is a capacity plan wrong by a factor of three with
    /// nothing in any log to say so.
    /// </remarks>
    public int MaxSharedHolders
    {
        get => _maxSharedHolders;
        init => _maxSharedHolders = Math.Clamp(value, 1, MaxAllowedSharedHolders);
    }

    /// <summary>
    /// Hard ceiling on co-running shared work. Well above any realistic number of
    /// simultaneous scripts on one box, and far below a fork bomb.
    /// </summary>
    internal const int MaxAllowedSharedHolders = 64;

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
    /// Whether this gate would admit two holders in <paramref name="first"/> and
    /// <paramref name="second"/> mode AT THE SAME TIME, on an otherwise empty gate.
    /// A pure predicate over the admission rule — it reads no live state.
    /// <para>
    /// Exists so callers that must reason about co-running (notably
    /// <see cref="DeploymentExecutor"/>'s refusal to run two attempts of ONE task) ask the
    /// gate instead of restating its rule. Mode compatibility is no longer the whole rule:
    /// <see cref="MaxSharedHolders"/> can exclude two SHARED holders as well, so a caller
    /// that hardcoded "both shared means both admitted" would be wrong on any box
    /// configured with a cap of 1.
    /// </para>
    /// </summary>
    public bool WouldAdmitConcurrently(Mode first, Mode second)
        => first == Mode.Shared
           && second == Mode.Shared
           && MaxSharedHolders >= 2;

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

            // The callback stamps the REASON before completing the waiter, so the
            // continuation below always observes a settled reason (see Waiter.Reason);
            // leaving the queue happens under _sync, ordered against a racing grant.
            using var registration = expiry.Token.Register(
                static state =>
                {
                    var w = (Waiter)state!;
                    w.ClaimGiveUpReason(GiveUpReason.Expired);
                    w.Tcs.TrySetResult(false);
                },
                waiter);

            granted = await waiter.Tcs.Task.ConfigureAwait(false);
        }
        catch (Exception unwinding)
        {
            // A waiter left in the queue with an uncompleted TCS is FATAL: DrainNoLock
            // would later dequeue it, take the slot on its behalf, succeed at
            // TrySetResult(true) because nobody had completed it, then find no
            // continuation to hand the lease to. The gate would be held by NOBODY,
            // forever — an Exclusive phantom wedges every later deployment and ad-hoc
            // script behind the unbounded wait, a Shared one makes Exclusive permanently
            // ungrantable, and neither survives without restarting the agent.
            // Dequeueing is NOT sufficient: by the time we get here the waiter may
            // ALREADY have been granted, in which case its Node is null and the slot is
            // taken on our behalf. So claim it and release what we own.
            //
            // The original exception is rethrown unchanged — callers dispatch on its type —
            // with any holder-accounting failure recorded alongside it, because every
            // caller logs the exception it receives and the gate has no logger of its own.
            if (AbandonAfterThrow(waiter, mode) is { } releaseFault)
            {
                // Logged, not attached to `unwinding.Data`: no configured sink renders Data.
                // The original exception is still rethrown unchanged — callers dispatch on its
                // type — so this is the only place the accounting failure can surface.
                //
                // Wrapped in its own try because we are INSIDE a catch, one statement before
                // `throw;`. Microsoft.Extensions.Logging collects provider failures and rethrows
                // them as AggregateException, so a faulting sink here would REPLACE the exception
                // the caller dispatches on — turning a clean ObjectDisposedException ("abandon
                // quietly") into a hard wave failure. That is the same hazard AbandonAfterThrow
                // just below was widened to Exception to avoid; leaving it open in the caller
                // would have undone that.
                try
                {
                    Logger?.LogError(releaseFault,
                        "Machine execution gate could not release a {Mode} lease while unwinding " +
                        "{OriginalException}. A holder slot may have leaked, which would block " +
                        "every deployment and ad-hoc script on this machine until the agent is " +
                        "restarted.", mode, unwinding.GetType().Name);
                }
                catch
                {
                    // Nothing useful is left to do: the sink is broken and the original
                    // exception is the one that must reach the caller intact.
                }
            }
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

        // Whoever stamped the reason FIRST owns it, and always stamped it before
        // completing the TCS — so this read is settled and truthful even when disposal
        // and expiry race. It must be: relabelling a genuine expiry as a disposal skips
        // the callers' escalation paths (the wedged-gate report in DeploymentExecutor,
        // SwapGate.Busy — and hence the swap-deferred report — in AgentUpdateService),
        // and relabelling a disposal as an expiry reports a wedged agent during a clean
        // shutdown.
        ObjectDisposedException.ThrowIf(waiter.Reason == GiveUpReason.Disposed, this);
        return null; // the bounded wait expired
    }

    /// <summary>
    /// Unwinds a waiter when something between its enqueue and its await threw. Claims
    /// the waiter first: if we win the race the gate never granted it and dequeueing is
    /// enough, but if we LOSE to a grant we now own a lease no <see cref="Releaser"/>
    /// will ever dispose, and must give the slot back by hand. Losing to the expiry
    /// callback or to <see cref="Dispose"/> means nothing is held, so the queue exit is
    /// all that is left to do.
    /// <para>
    /// Returns the holder-accounting failure if giving the slot back threw, so the caller
    /// can attach it to the exception it is already unwinding. It must NOT be thrown from
    /// here: that would replace the original, and callers dispatch on the original's type
    /// (<see cref="ObjectDisposedException"/> means "abandon quietly", anything else is
    /// reported to the server as a hard task failure).
    /// </para>
    /// </summary>
    private Exception? AbandonAfterThrow(Waiter waiter, Mode mode)
    {
        waiter.ClaimGiveUpReason(GiveUpReason.Abandoned);
        if (waiter.Tcs.TrySetResult(false))
        {
            LeaveQueue(waiter);
            return null;
        }

        // Someone else completed it. Only a GRANT leaves state to undo.
        //
        // Read through Task.Result, not IsCompletedSuccessfully: TrySetResult reserves
        // completion before it publishes the result, so a status check can observe
        // "not completed" on a task whose grant is already committed — and the else branch
        // would then leak the slot forever, which is the exact failure this method exists
        // to prevent. Result blocks through that window by contract instead of relying on
        // TrySetResult's internal spin-until-published, and it cannot deadlock here: the
        // task IS completed (our TrySetResult lost), and no lock is held on this path.
        if (!waiter.Tcs.Task.Result)
        {
            LeaveQueue(waiter);
            return null;
        }

        try
        {
            Release(mode);
            return null;
        }
        catch (Exception ex)
        {
            // Catch EVERYTHING. This runs while another exception is unwinding, and the
            // original is the one callers dispatch on — so any throw that escapes here
            // REPLACES it, turning (say) a cancellation into an accounting error. The narrower
            // InvalidOperationException filter left every other exception type able to do
            // exactly that.
            return ex;
        }
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

    /// <summary>
    /// Gives the mode's slot back and hands the gate on.
    /// </summary>
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
                    // Stamp BEFORE completing (below), and first-writer-wins, so a
                    // waiter whose bounded wait had already lapsed keeps "Expired" and
                    // is not relabelled a disposal.
                    waiter.ClaimGiveUpReason(GiveUpReason.Disposed);
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

    /// <summary>
    /// <c>internal</c>, not <c>private</c>, purely so the first-writer-wins reason stamp
    /// can be asserted DIRECTLY. It is the one invariant here that survived an inverted
    /// implementation with the whole suite green: the interleaving that discriminates it
    /// end-to-end (expiry-completed but still queued when <see cref="Dispose"/> runs) is
    /// not reachable deterministically, because the continuation is scheduled by the very
    /// completion that would have to be paused. Widening visibility costs nothing —
    /// no mutable hook, no behaviour change, and the assembly already grants
    /// <c>InternalsVisibleTo</c> to its test project.
    /// </summary>
    internal sealed class Waiter(Mode mode)
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

        private int _reason = (int)GiveUpReason.None;

        /// <summary>
        /// Why this waiter stopped waiting, or <see cref="GiveUpReason.None"/> while it
        /// is still waiting or was granted.
        /// </summary>
        public GiveUpReason Reason => (GiveUpReason)Volatile.Read(ref _reason);

        /// <summary>
        /// First-writer-wins stamp of the give-up reason. EVERY path that completes this
        /// waiter with <c>false</c> must call this BEFORE <c>Tcs.TrySetResult(false)</c>,
        /// which is what makes <see cref="Reason"/> settled by the time the awaiting
        /// continuation runs — the continuation is only scheduled by that completion.
        /// <para>
        /// First-writer-wins is also the correct SEMANTIC, not just a tie-break: if the
        /// bounded wait lapsed before disposal reached the waiter, the wait genuinely
        /// expired on its own terms and must be reported as expiry even though disposal
        /// happens to win the TCS race. An earlier cut inferred the reason from a bool
        /// set inside <c>Dispose</c>, which mislabelled exactly that case, because the
        /// expiry callback deliberately leaves the waiter queued for the continuation to
        /// dequeue.
        /// </para>
        /// </summary>
        public void ClaimGiveUpReason(GiveUpReason reason)
            => Interlocked.CompareExchange(
                ref _reason, (int)reason, (int)GiveUpReason.None);
    }

    /// <summary>Why a waiter stopped waiting without being granted.</summary>
    internal enum GiveUpReason
    {
        /// <summary>Still waiting, or granted.</summary>
        None = 0,

        /// <summary>The bounded wait lapsed, or the caller's token fired.</summary>
        Expired,

        /// <summary>The gate was disposed while this waiter was queued.</summary>
        Disposed,

        /// <summary>Something threw between the enqueue and the await.</summary>
        Abandoned,
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

        /// <summary>
        /// Returns the slot exactly once. A failure to do so is DELIBERATELY permanent:
        /// the lease stays consumed and the exception propagates.
        /// <para>
        /// There was a re-arm here ("so a later Dispose can retry"), and it was wrong in
        /// both halves. Unreachable, first: <see cref="MachineExecutionGate.Release"/>
        /// validates before it mutates and its hand-off cannot throw, so the only way to
        /// throw without returning the slot is the unheld-release invariant — and a release
        /// that was never owed does not become owed later. Harmful, second: re-arming let a
        /// LATER dispose of that same stale lease run a release that now succeeds, silently
        /// decrementing a slot belonging to somebody else. A reader under-count admits an
        /// exclusive writer beside a live holder, i.e. the whole-directory swap running
        /// under a live script — the P8 clash the gate exists to prevent. A loud, contained
        /// invariant failure is strictly better than a silent miscount, so the throw is
        /// left to propagate to the caller that owns the bug.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return; // already released — idempotent by contract
            }

            _gate.Release(Mode);
        }
    }
}
