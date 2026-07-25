using System.Collections.Concurrent;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Result returned by the agent for an in-flight sub-plan.
/// </summary>
public sealed record SubPlanResult(bool Success, string? ErrorMessage);

/// <summary>
/// M14.4 — per-step outcome reported by the agent during a sub-plan's wave.
/// The orchestrator drains the registry's per-step results when the sub-plan
/// completes and uses them to attribute Required failures to the actual
/// failing step (not the conservative whole-wave abort the M14.0..3 code
/// performed).
///
/// <para>
/// <see cref="StepIndex"/> is the plan-level <c>DeploymentStepPlan.Index</c>
/// — stable across the deployment so the worker can look up the
/// <c>StepSnapshot</c> by index without name-collision risk inside ForEach
/// iterations (M15) or duplicate-name authoring.
/// </para>
/// </summary>
public sealed record SubPlanStepResult(
    int StepIndex,
    string StepName,
    bool Success,
    string? ErrorMessage,
    IReadOnlyDictionary<string, string> Outputs,
    /// <summary>
    /// B4 — the subset of <see cref="Outputs"/> keys the agent flagged
    /// sensitive (T0-6). The hub already uses the wire list for at-rest
    /// encryption; threading it through the registry lets the orchestrator's
    /// online output merge extend the NEXT wave's
    /// <c>DeploymentPlan.SensitiveVariableNames</c>, so the agent's log
    /// redactor masks a prior step's sensitive output in later waves too.
    /// Null (legacy callers) means none.
    /// </summary>
    IReadOnlyCollection<string>? SensitiveOutputNames = null);

/// <summary>
/// Shared singleton state coordinating piecewise agent dispatches between
/// <see cref="DeploymentWorker"/> (writer) and <see cref="AgentHub"/> (reader).
/// <para>
/// When the worker dispatches a target-side sub-plan it registers a
/// <see cref="TaskCompletionSource{TResult}"/> here keyed by
/// (deployment id, target id), then awaits the task. While the wave runs the
/// agent calls <see cref="IAgentHubServer.ReportStepCompletedAsync"/> per
/// step — the hub forwards those into <see cref="RecordStepResult"/> here
/// (the hub resolves the target id from its connection's NameIdentifier
/// claim, so no wire-contract change) so the worker can drain them on wave
/// completion. When the agent finishes the sub-plan and calls
/// <c>AgentHub.CompleteDeploymentAsync</c>, the hub resolves the TCS so the
/// worker resumes, drains per-step results, and continues to the next wave.
/// </para>
/// <para>
/// M-RollingDeployments Phase 1b: slots are per-(deployment, target) so a
/// multi-target deployment can have N sub-plans in flight concurrently
/// (one per target inside the same wave). The pre-1b key (deployment-only)
/// is reachable through the (deployment, <see cref="Guid.Empty"/>) tuple —
/// the single-target dispatch path and tests that don't care about the
/// target dimension can pass <see cref="Guid.Empty"/>.
/// </para>
/// </summary>
public interface IPendingSubPlanRegistry
{
    /// <summary>
    /// Register a TCS that <see cref="RouteCompletion"/> will complete when the
    /// agent reports sub-plan completion for this exact dispatch attempt.
    /// Clears any per-step results left over from a previous wave so the new
    /// wave starts with an empty bag.
    /// <paramref name="dispatchId"/> is the attempt's idempotency key
    /// (<see cref="KrakenDeploy.Contracts.DeploymentPlan.DispatchId"/>);
    /// <see cref="Guid.Empty"/> preserves the legacy match-by-(deployment,
    /// target) behaviour for callers without a key.
    /// <para>
    /// F2 — <paramref name="onExecutionStarted"/> fires (at most once, on the
    /// caller-agnostic hub thread) when <see cref="TryMarkExecutionStarted"/>
    /// matches THIS attempt: the orchestrator uses it to re-arm the wave deadline
    /// from gate acquisition. It must be tolerant of running late — the attempt may
    /// already have ended.
    /// </para>
    /// </summary>
    void Register(
        Guid deploymentId, Guid targetId, Guid dispatchId,
        TaskCompletionSource<SubPlanResult> tcs,
        Action? onExecutionStarted = null);

    /// <summary>
    /// F2 — records that the agent reported "this attempt acquired my machine
    /// execution gate and is executing now", and invokes the slot's
    /// <c>onExecutionStarted</c> callback the FIRST time it matches. Returns
    /// <c>true</c> only on that first match.
    /// <para>
    /// Matching is STRICT (unlike <see cref="RouteCompletion"/>): the open slot's
    /// dispatch id must equal <paramref name="dispatchId"/> exactly and
    /// <see cref="Guid.Empty"/> never matches. A report from a superseded /
    /// re-dispatched attempt must not extend the LIVE attempt's deadline, and the
    /// message only exists from agent contract v2 onwards, so every genuine sender
    /// carries a real key.
    /// </para>
    /// </summary>
    bool TryMarkExecutionStarted(Guid deploymentId, Guid targetId, Guid dispatchId);

    /// <summary>
    /// B2 (B6.2 pulled forward) — route an agent completion to the pending
    /// sub-plan state:
    /// <list type="bullet">
    /// <item><see cref="SubPlanCompletionRoute.ResolvedPending"/> — a slot for
    /// this exact dispatch attempt was waiting and has been resolved; the
    /// caller must NOT finalize the deployment (the orchestrator will).</item>
    /// <item><see cref="SubPlanCompletionRoute.StaleOrDuplicate"/> — the
    /// completion belongs to an attempt that was already resolved/cancelled,
    /// or to a different attempt than the one currently awaited; the caller
    /// must swallow it (finalizing would corrupt a mid-flight deployment).</item>
    /// <item><see cref="SubPlanCompletionRoute.NoPendingSubPlan"/> — nothing
    /// here knows the dispatch; the caller falls back to the direct DB
    /// finalize (runbook runs, post-restart lates — both guarded by
    /// <c>IsTerminal</c> downstream).</item>
    /// </list>
    /// An <paramref name="dispatchId"/> of <see cref="Guid.Empty"/> (legacy
    /// agent / offline-era plan) matches whatever slot is open for the
    /// (deployment, target) pair — the pre-B2 behaviour.
    /// </summary>
    SubPlanCompletionRoute RouteCompletion(
        Guid deploymentId, Guid targetId, Guid dispatchId, SubPlanResult result);

    /// <summary>
    /// Forcefully cancel any pending TCS for this (deployment, target) pair
    /// (used on cleanup when the worker bails out of a dispatch loop). The
    /// cancelled attempt's dispatch id is retired, so a late completion for it
    /// routes as <see cref="SubPlanCompletionRoute.StaleOrDuplicate"/> instead
    /// of reaching the DB fallback finalizer.
    /// </summary>
    void Cancel(Guid deploymentId, Guid targetId, string reason);

    /// <summary>
    /// M14.4 — record a per-step outcome from the agent. Called by
    /// <c>AgentHub.ReportStepCompletedAsync</c>. Silently no-ops if no
    /// sub-plan is currently registered for the (deployment, target) pair
    /// (e.g. the wave already timed out and was cancelled — late reports are
    /// dropped) or if <paramref name="dispatchId"/> does not match the
    /// registered attempt (stale report from a previous wave attempt).
    /// <see cref="Guid.Empty"/> matches any open slot (legacy behaviour).
    /// </summary>
    void RecordStepResult(Guid deploymentId, Guid targetId, Guid dispatchId, SubPlanStepResult result);

    /// <summary>
    /// M14.4 — drain the per-step outcomes accumulated for the currently
    /// in-flight or just-resolved sub-plan, in arrival order. The orchestrator
    /// calls this after the wave's <see cref="TryResolve"/> fires (success
    /// or failure) to apply per-step Required attribution + emit collision
    /// audits. Returns an empty list when no per-step reports landed.
    /// </summary>
    IReadOnlyList<SubPlanStepResult> DrainStepResults(Guid deploymentId, Guid targetId);

    /// <summary>
    /// M-RollingDeployments Phase 1b — hub-side lookup: which (deployment,
    /// target) slots are awaiting a TCS resolution that this connection's
    /// target id could fulfil. The hub uses this when the agent calls
    /// <c>CompleteDeploymentAsync</c> / <c>ReportStepCompletedAsync</c> with a
    /// deployment id but the per-target slot key was registered on the
    /// orchestrator side. Returns the set of target ids the registry has
    /// open slots for, so the hub can route the agent's report to the right
    /// slot by intersecting with the connection's claimed target id.
    /// </summary>
    bool HasSlot(Guid deploymentId, Guid targetId);

    /// <summary>
    /// B6 — <c>true</c> when this dispatch attempt has POSITIVELY ended
    /// (resolved or cancelled) in this process. The hub drops log lines from a
    /// retired attempt so a superseded/timed-out attempt's outbox flush cannot
    /// interleave noise into the current attempt's log.
    /// <see cref="Guid.Empty"/> (legacy/offline plans) is never retired, and an
    /// unknown id (post-restart the set is empty) is NOT retired — only
    /// positive knowledge drops a line.
    /// </summary>
    bool IsRetiredDispatch(Guid dispatchId);
}

/// <summary>How <see cref="IPendingSubPlanRegistry.RouteCompletion"/> classified
/// an agent completion — drives <c>AgentHub.CompleteDeploymentAsync</c>.</summary>
public enum SubPlanCompletionRoute
{
    /// <summary>The awaited attempt's slot was resolved; the orchestrator continues.</summary>
    ResolvedPending,

    /// <summary>Already resolved/cancelled attempt, or a different attempt than
    /// the awaited one — swallow; never finalize from this.</summary>
    StaleOrDuplicate,

    /// <summary>No sub-plan state knows this dispatch — direct DB finalize path
    /// (runbook runs, post-restart lates; IsTerminal-guarded downstream).</summary>
    NoPendingSubPlan,
}

public sealed class PendingSubPlanRegistry : IPendingSubPlanRegistry
{
    /// <summary>
    /// One dispatch attempt's in-flight state. A CLASS, not a record: it carries
    /// mutable state (<see cref="ExecutionStartedMarked"/>) and the
    /// remove-if-unchanged in <see cref="RouteCompletion"/> wants IDENTITY
    /// ("is the same slot object still registered?"), which is exactly what
    /// <c>EqualityComparer&lt;Slot&gt;.Default</c> gives a plain class.
    /// </summary>
    private sealed class Slot(
        Guid dispatchId,
        TaskCompletionSource<SubPlanResult> tcs,
        Action? onExecutionStarted)
    {
        public Guid DispatchId { get; } = dispatchId;
        public TaskCompletionSource<SubPlanResult> Tcs { get; } = tcs;

        /// <summary>F2 — fired once, when this attempt's execution-started report
        /// lands. Must not throw (it runs on the reporting agent's hub call).</summary>
        public Action? OnExecutionStarted { get; } = onExecutionStarted;

        /// <summary>F2 — 0 until the attempt's execution-started report has been
        /// accepted; the at-least-once outbox may deliver it twice.</summary>
        public int ExecutionStartedMarked;
    }

    private readonly ConcurrentDictionary<(Guid DeploymentId, Guid TargetId), Slot> _pending = new();
    // Per-(deployment, target) per-step results bag. Survives until the next
    // Register call clears it, so the orchestrator can drain after RouteCompletion.
    private readonly ConcurrentDictionary<(Guid DeploymentId, Guid TargetId), List<SubPlanStepResult>> _stepResults = new();

    // B2: dispatch ids whose attempt ended (resolved or cancelled). A late or
    // duplicate completion carrying one of these is swallowed. Process-lifetime
    // and bounded: after a server restart the set is empty, but then the TCS is
    // gone too and the dispatch reconciler + IsTerminal guard own the outcome.
    // Sized so one long-running deployment's ids survive heavy system-wide wave
    // churn (~256 KB worst case) — eviction reopens only the IsTerminal-guarded
    // fallback, never a TCS resolve.
    private const int RetiredCapacity = 16_384;
    private readonly ConcurrentDictionary<Guid, byte> _retired = new();
    private readonly ConcurrentQueue<Guid> _retiredOrder = new();

    public void Register(
        Guid deploymentId, Guid targetId, Guid dispatchId,
        TaskCompletionSource<SubPlanResult> tcs,
        Action? onExecutionStarted = null)
    {
        ArgumentNullException.ThrowIfNull(tcs);
        var key = (deploymentId, targetId);
        // Overwrite any stale entry (should not normally happen — caller
        // guarantees only one sub-plan per (deployment, target) at a time).
        _pending[key] = new Slot(dispatchId, tcs, onExecutionStarted);
        // New wave starts with a clean per-step bag.
        _stepResults[key] = [];
    }

    public bool TryMarkExecutionStarted(Guid deploymentId, Guid targetId, Guid dispatchId)
    {
        // Strict match: Guid.Empty is the shared "no key" marker and must never
        // arm anything, and a non-empty id must be THIS attempt's — a superseded
        // attempt's late report cannot extend the live attempt's deadline.
        if (dispatchId == Guid.Empty
            || !_pending.TryGetValue((deploymentId, targetId), out var slot)
            || slot.DispatchId != dispatchId)
        {
            return false;
        }

        // At-least-once delivery: only the first report arms.
        if (Interlocked.Exchange(ref slot.ExecutionStartedMarked, 1) != 0)
        {
            return false;
        }

        // The callback re-arms a CancellationTokenSource timer that the attempt may
        // already have disposed (it ends concurrently with this hub call) — the
        // callback owns that guard; by contract it does not throw. If one ever does,
        // hand the one-shot back so a retry (the agent re-reports on reconnect) can
        // still arm the deadline: swallowing the marker AND the arm would leave the
        // attempt running on the backstop while claiming it had started executing,
        // which is the one state the two-stage deadline must never be in.
        try
        {
            slot.OnExecutionStarted?.Invoke();
        }
        catch
        {
            Interlocked.Exchange(ref slot.ExecutionStartedMarked, 0);
            throw;
        }

        return true;
    }

    public SubPlanCompletionRoute RouteCompletion(
        Guid deploymentId, Guid targetId, Guid dispatchId, SubPlanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var key = (deploymentId, targetId);

        if (_pending.TryGetValue(key, out var slot))
        {
            if (!Matches(slot.DispatchId, dispatchId))
            {
                // A slot is open but for a DIFFERENT attempt — this completion
                // is from a previous (timed-out / re-dispatched) attempt. It
                // must neither resolve the current attempt nor fall through to
                // the DB fallback (the deployment is mid-flight).
                return SubPlanCompletionRoute.StaleOrDuplicate;
            }

            // Remove-if-same-slot: a concurrent Register (next attempt) may
            // have replaced the slot between TryGetValue and here — in that
            // case this completion just became stale.
            if (_pending.TryRemove(new KeyValuePair<(Guid, Guid), Slot>(key, slot)))
            {
                Retire(slot.DispatchId);
                slot.Tcs.TrySetResult(result);
                return SubPlanCompletionRoute.ResolvedPending;
            }
            return SubPlanCompletionRoute.StaleOrDuplicate;
        }

        if (dispatchId != Guid.Empty && _retired.ContainsKey(dispatchId))
        {
            return SubPlanCompletionRoute.StaleOrDuplicate;
        }

        return SubPlanCompletionRoute.NoPendingSubPlan;
    }

    public bool IsRetiredDispatch(Guid dispatchId)
        => dispatchId != Guid.Empty && _retired.ContainsKey(dispatchId);

    public void Cancel(Guid deploymentId, Guid targetId, string reason)
    {
        if (_pending.TryRemove((deploymentId, targetId), out var slot))
        {
            Retire(slot.DispatchId);
            slot.Tcs.TrySetResult(new SubPlanResult(false, reason));
        }
    }

    public void RecordStepResult(Guid deploymentId, Guid targetId, Guid dispatchId, SubPlanStepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var key = (deploymentId, targetId);
        // Append iff a sub-plan is currently in flight FOR THIS ATTEMPT —
        // otherwise we'd leak unbounded state from late reports of cancelled
        // waves, or pollute a retried attempt's bag with a previous attempt's
        // stale reports. The _pending check is the in-flight gate; the
        // _stepResults bag is populated by Register so the absence of
        // _pending means "nobody's listening".
        if (!_pending.TryGetValue(key, out var slot) || !Matches(slot.DispatchId, dispatchId))
        {
            return;
        }
        if (_stepResults.TryGetValue(key, out var list))
        {
            lock (list)
            {
                list.Add(result);
            }
        }
    }

    public IReadOnlyList<SubPlanStepResult> DrainStepResults(Guid deploymentId, Guid targetId)
    {
        if (!_stepResults.TryRemove((deploymentId, targetId), out var list))
        {
            return [];
        }
        lock (list)
        {
            // Defensive copy — list is mutated under lock; caller iterates
            // freely without us holding the lock.
            return list.ToArray();
        }
    }

    public bool HasSlot(Guid deploymentId, Guid targetId)
        => _pending.ContainsKey((deploymentId, targetId));

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>An incoming Guid.Empty (legacy agent / offline-era plan) matches
    /// any open slot — the pre-B2 behaviour. A non-empty id must match exactly.</summary>
    private static bool Matches(Guid slotDispatchId, Guid incomingDispatchId)
        => incomingDispatchId == Guid.Empty || slotDispatchId == incomingDispatchId;

    private void Retire(Guid dispatchId)
    {
        // Guid.Empty is the shared "no key" marker — never retire it, or every
        // legacy completion after the first would be misread as a duplicate.
        if (dispatchId == Guid.Empty)
        {
            return;
        }
        if (_retired.TryAdd(dispatchId, 0))
        {
            _retiredOrder.Enqueue(dispatchId);
            while (_retiredOrder.Count > RetiredCapacity
                   && _retiredOrder.TryDequeue(out var evicted))
            {
                _retired.TryRemove(evicted, out _);
            }
        }
    }
}
