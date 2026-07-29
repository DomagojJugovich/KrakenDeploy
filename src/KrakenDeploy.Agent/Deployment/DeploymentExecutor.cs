using System.Collections.Concurrent;
using System.Globalization;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Logging;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octostache;

namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// Executes a <see cref="DeploymentPlan"/> received from the server.
/// For each step the executor:
/// <list type="number">
///   <item>Resolves the first registered <see cref="IStepHandler"/> that can handle the step type.</item>
///   <item>Optionally downloads and extracts the step's package (if the handler requires it).</item>
///   <item>Delegates execution to the handler.</item>
///   <item>Streams log lines back via <see cref="IServerLink"/>.</item>
///   <item>Signals completion to the server.</item>
/// </list>
/// </summary>
public sealed class DeploymentExecutor(
    IServerLink serverLink,
    IPackageSource packageDownloader,
    IArtifactSink artifactUploader,
    StepPackageLoader stepPackageLoader,
    MachineExecutionGate executionGate,
    IOptions<AgentConfig> agentConfig,
    ILogger<DeploymentExecutor> logger)
{
    /// <summary>
    /// True while a deployment is executing. Read by <see cref="Services.AgentUpdateService"/>
    /// to avoid swapping the agent binary during an in-flight deployment.
    /// </summary>
    public bool IsExecuting => !_running.IsEmpty;

    /// <summary>
    /// B6 — in-flight tasks by task id (<c>DeploymentPlan.DeploymentId</c>;
    /// covers runbook runs too). One entry per task: duplicate dispatches of
    /// the SAME attempt are ignored, a NEWER attempt supersedes (cancels) the
    /// old one, and <see cref="TryCancel"/> signals the entry's token — which
    /// flows through every step handler into <c>ScriptRunner</c>'s
    /// process-tree kill.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, RunningDeployment> _running = new();

    /// <summary>Default for <see cref="SupersedeUnwindTimeout"/>: how long a
    /// superseding dispatch waits for the cancelled old attempt to unwind before
    /// force-detaching it (a kill normally unwinds in well under a second; this
    /// only guards a pathologically stuck reap).</summary>
    internal static readonly TimeSpan DefaultSupersedeUnwindTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default for <see cref="WedgedGateAcquireTimeout"/>: bounded wait
    /// for the machine execution gate when a new attempt has force-detached a
    /// stuck predecessor that may still hold it.</summary>
    internal static readonly TimeSpan DefaultWedgedGateAcquireTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a superseding dispatch waits for the cancelled old
    /// attempt to unwind before force-detaching it. Overridable for tests.</summary>
    internal TimeSpan SupersedeUnwindTimeout { get; init; } = DefaultSupersedeUnwindTimeout;

    /// <summary>Bounded wait for the machine execution gate after force-detaching
    /// a stuck superseded predecessor that may still hold it: on expiry the new
    /// attempt escalates (logs + reports a failed completion) instead of wedging
    /// the agent forever behind the stuck step. Overridable for tests.</summary>
    internal TimeSpan WedgedGateAcquireTimeout { get; init; } = DefaultWedgedGateAcquireTimeout;

    // B7/F2/F5 — the machine's execution gate. Registration in _running happens
    // BEFORE queueing, so a queued plan is still cancellable / supersedable; the
    // wait observes the run's token.
    // F2 moved the semaphore into the shared MachineExecutionGate singleton so the
    // ad-hoc path takes the SAME gate, and made it opt-out per target via
    // DeploymentPlan.AllowParallelTaskExecution.
    // F5 turned the gate into a READER-WRITER lock, so that flag no longer bypasses
    // anything: it downgrades this plan from EXCLUSIVE to SHARED (Octopus
    // ScriptIsolationMutex parity). A shared plan co-runs with other shared work and
    // still serializes against every exclusive holder — consent is mutual.

    /// <summary>
    /// F5 — which side of the machine gate a plan takes. EXCLUSIVE is the default;
    /// the per-target <c>AllowParallelTaskExecution</c> opt-in downgrades it to
    /// SHARED. Never a bypass: a shared plan still waits behind an exclusive one.
    /// </summary>
    private static MachineExecutionGate.Mode ModeFor(DeploymentPlan plan)
        => plan.AllowParallelTaskExecution
            ? MachineExecutionGate.Mode.Shared
            : MachineExecutionGate.Mode.Exclusive;

    private sealed class RunningDeployment(Guid dispatchId, MachineExecutionGate.Mode mode)
    {
        public Guid DispatchId { get; } = dispatchId;

        /// <summary>
        /// F5 — which side of the machine gate this attempt takes. Recorded at
        /// registration so a SUPERSEDING attempt can reason about the predecessor's
        /// actual isolation rather than guessing from its own mode: the per-target flag
        /// can be flipped between two attempts of one task, and the question that
        /// matters ("would the gate let these two co-run?") is about the pair.
        /// </summary>
        public MachineExecutionGate.Mode Mode { get; } = mode;

        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Why the token fired (server push reason / supersede note).
        /// Written before <see cref="Cts"/>.Cancel(), read after the token
        /// observes cancellation — the cancel call is the memory barrier.</summary>
        public string? CancelReason { get; set; }
    }

    /// <summary>
    /// B6 — cooperatively aborts the in-flight task <paramref name="taskId"/>:
    /// signals its cancellation token (the running step's process tree is
    /// killed by <c>ScriptRunner</c>) and lets <see cref="ExecuteAsync"/>
    /// report a failed completion for the attempt. Returns <c>false</c> when
    /// the task is not in flight (already finished, never received, or a
    /// duplicate cancel) — a no-op by design, the push is best-effort.
    /// </summary>
    public bool TryCancel(Guid taskId, string? reason)
    {
        if (!_running.TryGetValue(taskId, out var run))
        {
            return false;
        }
        run.CancelReason = reason;
        run.Cts.Cancel();
        return true;
    }

    // Per-step-flow current step index, stored +1 so the AsyncLocal default (0)
    // reads as "no step / plan-level" (-1). Set at the top of RunStepInWaveAsync;
    // it flows to every LogAsync in that step's async branch. Waves run steps via
    // Task.WhenAll, so each branch keeps its own value — parallel steps don't clash.
    private readonly AsyncLocal<int> _stepIndexPlusOne = new();

    // B6: the running plan's DispatchId, stamped onto every AppendLog line so the
    // server can drop log noise from a dispatch attempt it has already retired.
    // AsyncLocal for the same reason as the step index — it must flow into the
    // wave's parallel step branches, and a superseding dispatch (new attempt of
    // the same task) must not relabel the old attempt's still-flushing lines.
    private readonly AsyncLocal<Guid> _dispatchId = new();

    // T0-6: value-based secret redactor for the running plan. Seeded from the
    // plan's sensitive variable values at the top of ExecuteAsync and grown as
    // sensitive output variables are captured mid-run. Every log line the agent
    // emits passes through it (see LogAsync) so a secret is masked even when a
    // script echoes it. Held in an AsyncLocal for symmetry with the step index;
    // a single executor instance runs one plan at a time, but this also keeps it
    // out of the way of any future concurrent execution.
    private readonly AsyncLocal<SecretRedactor?> _redactor = new();

    /// <param name="orchestrateSteps">
    /// When <c>true</c>, the executor itself applies per-step run conditions,
    /// timeouts, retries, and Required-aware gating — the orchestration the
    /// SERVER normally drives for online deployments. Used by the offline runner,
    /// where no server orchestrates: the same plan executes with identical
    /// semantics. Defaults to <c>false</c> so the online agent path (server
    /// orchestrates) is unchanged.
    /// </param>
    /// <param name="hostStopping">
    /// F2-followup 1 — the agent host's shutdown token. Linked into the MACHINE GATE
    /// WAIT only (not into step execution: a disconnect or shutdown must never abort
    /// a step that is already running — see docs/execution-engine.md §6). Without it
    /// a plan queued on the gate at shutdown would park forever: the gate's ordinary
    /// wait observes only the per-run token, which nothing fires at shutdown, so
    /// <see cref="ExecuteAsync"/>'s finally would never run and the registry entry
    /// would leak — keeping <see cref="IsExecuting"/> true and blocking self-upgrade.
    /// Unreachable before the push handlers were detached, hence the default.
    /// </param>
    public async Task ExecuteAsync(
        DeploymentPlan plan,
        bool orchestrateSteps = false,
        CancellationToken hostStopping = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // ── B6: single-flight per task id ─────────────────────────────────
        // Exactly one attempt of a task runs at a time. A re-delivered copy of
        // the SAME attempt (at-least-once transport) is dropped — the original
        // in-flight run will report. A NEWER attempt (different DispatchId)
        // supersedes: the server only re-dispatches after abandoning the old
        // attempt (wave deadline / retry), so the old one is cancelled and
        // awaited before the new one starts — two attempts must never touch
        // the same extract dirs / IIS handles concurrently. The old attempt's
        // late reports carry the old DispatchId and are swallowed server-side.
        var mode = ModeFor(plan);
        var run = new RunningDeployment(plan.DispatchId, mode);
        // Set when a stuck old attempt is force-detached below: it may still hold
        // the machine execution gate, so this attempt's gate acquisition must be
        // BOUNDED rather than wait forever behind it (see the gate block below).
        var forceDetachedStuck = false;
        // F5 — which side the force-detached predecessor holds, when there is one. The
        // refusal below turns on whether the GATE would keep the two attempts apart,
        // which is a property of the PAIR: an exclusive predecessor excludes a shared
        // successor perfectly well, so keying the refusal on the successor's own mode
        // (as the first cut did) abandons attempts the gate would have serialized
        // correctly. The two can genuinely differ — the per-target flag is stamped at
        // plan-build time, so an operator toggling it between attempts changes it.
        MachineExecutionGate.Mode? detachedPredecessorMode = null;
        while (true)
        {
            var existing = _running.GetOrAdd(plan.DeploymentId, run);
            if (ReferenceEquals(existing, run))
            {
                break;
            }
            if (existing.DispatchId == plan.DispatchId)
            {
                logger.LogWarning(
                    "Duplicate dispatch of task {DeploymentId} attempt {DispatchId} ignored — " +
                    "the original delivery is still executing.",
                    plan.DeploymentId, plan.DispatchId);
                return;
            }

            logger.LogWarning(
                "Task {DeploymentId} re-dispatched as attempt {NewDispatch} while attempt " +
                "{OldDispatch} is still running; cancelling the old attempt.",
                plan.DeploymentId, plan.DispatchId, existing.DispatchId);
            existing.CancelReason = "Superseded by a newer dispatch of the same task.";
            existing.Cts.Cancel();
            // CancellationToken.None deliberately: this is the bounded wait for the
            // OLD attempt to unwind, and a host shutdown must not cut it short — the
            // whole point is that two attempts never overlap on the same extract
            // dirs / IIS handles. The force-detach below is the escape hatch.
            var unwound = await Task
                .WhenAny(
                    existing.Completed.Task,
                    Task.Delay(SupersedeUnwindTimeout, CancellationToken.None))
                .ConfigureAwait(false) == existing.Completed.Task;
            if (!unwound)
            {
                // Pathological: the old attempt's kill/reap is stuck. Detach its
                // registry entry so the new attempt can proceed; the stuck run
                // can no longer be addressed by TryCancel but its late reports
                // are already stale server-side.
                logger.LogError(
                    "Old attempt {OldDispatch} of task {DeploymentId} did not unwind within " +
                    "{Timeout}; detaching it and proceeding with the new attempt.",
                    existing.DispatchId, plan.DeploymentId, SupersedeUnwindTimeout);
                ((ICollection<KeyValuePair<Guid, RunningDeployment>>)_running)
                    .Remove(new(plan.DeploymentId, existing));
                forceDetachedStuck = true;
                detachedPredecessorMode = existing.Mode;
            }
        }

        try
        {

        // T0-6: seed the log redactor with this plan's sensitive values before
        // any step runs, so every log line — including plan-level ones below —
        // is masked. Grows later as sensitive output variables are captured.
        _redactor.Value = SecretRedactor.ForPlan(plan);
        _dispatchId.Value = plan.DispatchId;

        logger.LogInformation(
            "Starting deployment {DeploymentId} ({StepCount} step(s)) in environment {Env}.",
            plan.DeploymentId, plan.Steps.Length, plan.EnvironmentName);

        // The per-run token: fired by a server cancel push (TryCancel) or a
        // superseding dispatch. Flows into every step handler and from there
        // into ScriptRunner's process-tree kill.
        var ct = run.Cts.Token;

        // Accumulates Set-OctopusVariable captures per step name across the run.
        // Made available to subsequent steps as Octopus.Action[StepName].Output.X.
        // M14.4: inside a parallel wave siblings DO NOT see each other's
        // outputs — we snapshot pre-wave state once, run the wave's steps
        // against that frozen snapshot, and merge captures into the
        // accumulator only AFTER the wave completes (in SortOrder, so
        // last-writer-wins by Index when names collide).
        var outputsByStep = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            // ── B7/F2/F5: the machine's execution gate ──────────────────
            // By default a dispatched sub-plan takes the EXCLUSIVE side, so
            // concurrent deployments, runbook runs and ad-hoc scripts hitting the
            // same box serialize instead of interleaving file/IIS/service mutations.
            // A target with AllowParallelTaskExecution takes the SHARED side
            // instead: it co-runs with other shared work but still waits behind any
            // exclusive holder. Waves WITHIN a sub-plan keep their parallelism
            // either way. NOTE the unit is the dispatched sub-plan, i.e. one WAVE —
            // the server dispatches per wave, so the gate is released and re-taken
            // at every wave boundary (F6 closes that gap server-side).
            var slot = await AcquireMachineSlotAsync(
                    plan, mode, forceDetachedStuck, detachedPredecessorMode, ct, hostStopping)
                .ConfigureAwait(false);
            if (slot.Abandoned)
            {
                // AcquireMachineSlotAsync already logged the escalation and
                // reported the failed completion. The outer finally removes our
                // registry entry and signals completion; the gate was never
                // acquired, so there is nothing to release. Do NOT enter the
                // execution try below.
                return;
            }
            // Disposing null is a no-op, so the release needs no "did I take it?"
            // flag even on the abandoned paths.
            using (slot.Lease)
            {

            // F2 — the plan is now EXECUTING, not queued. The server arms the wave
            // deadline from this report instead of from dispatch, so queue time
            // behind a busy target no longer burns the wave's budget. Advisory and
            // best-effort: if it never lands, the server's dispatch-time backstop
            // ceiling still bounds the wave (B3's always-armed invariant).
            await ReportExecutionStartedAsync(plan, ct).ConfigureAwait(false);

            // ── M14.4 — partition into waves agent-side ─────────────────
            // The server already pre-flattens waves (one wave per sub-plan
            // dispatched), but the agent re-walks the trigger field so its
            // behaviour stays correct if the contract evolves to send
            // multi-wave sub-plans in the future.
            var waves = PartitionIntoWaves(plan.Steps);

            string? firstFailureMessage = null;
            var anyStepFailed = false;     // any failure (legacy break + status)
            var requiredFailed = false;    // a Required step failed (orchestrate break + status)
            var hasFailed = false;         // a non-Required step failed (drives Failure conditions)

            foreach (var wave in waves)
            {
                // Snapshot prior outputs + failure state once before the wave so
                // all siblings see the same baseline — siblings don't see each
                // other's captures or failures.
                var preWavePlan = AugmentPlanWithPriorOutputs(plan, outputsByStep);
                var hasFailedSnapshot = hasFailed;

                var stepTasks = wave.Select(step =>
                    RunStepInWaveAsync(
                        plan, preWavePlan, step, orchestrateSteps, hasFailedSnapshot, ct)).ToArray();

                var stepOutcomes = await Task.WhenAll(stepTasks).ConfigureAwait(false);

                // Merge captures into accumulator in declared order
                // (wave order == SortOrder ascending, set in PartitionIntoWaves)
                // so last-writer-wins by Index for any name overlaps.
                foreach (var outcome in stepOutcomes)
                {
                    if (outcome.CapturedOutputs.Count > 0)
                    {
                        // M15.2: key by AccumulatorKey (ForEach-iteration
                        // synthetic name like "Deploy[0]") when set. Falls
                        // back to step.Name for non-iteration steps + pre-M15
                        // plans. Octostache references resolve to the same
                        // key on the server side.
                        var accKey = outcome.Step.AccumulatorKey ?? outcome.Step.Name;
                        outputsByStep[accKey] = outcome.CapturedOutputs;
                    }
                    if (outcome.Skipped || outcome.Success)
                    {
                        continue;
                    }

                    anyStepFailed = true;
                    firstFailureMessage ??= $"Step '{outcome.Step.Name}' failed.";
                    // Required failures abort (below); non-required failures
                    // flip hasFailed so Failure-conditioned cleanup/finalisation
                    // steps in later waves run — mirrors the server orchestrator.
                    if (outcome.Step.Required)
                    {
                        requiredFailed = true;
                    }
                    else
                    {
                        hasFailed = true;
                    }
                }

                // Break semantics:
                //   * orchestrate mode — only a Required failure aborts; non-
                //     required failures continue so Failure/Always steps run.
                //   * legacy (online agent) — any failure stops; the server
                //     applies Required attribution from the per-step reports.
                if (orchestrateSteps ? requiredFailed : anyStepFailed)
                {
                    break;
                }
            }

            // orchestrate mode succeeds unless a Required step failed (non-
            // required failures are warnings); legacy mode fails on any failure.
            var deploymentSucceeded = orchestrateSteps ? !requiredFailed : !anyStepFailed;
            await serverLink
                .CompleteDeploymentAsync(
                    plan.DeploymentId,
                    plan.DispatchId,
                    success:      deploymentSucceeded,
                    errorMessage: firstFailureMessage,
                    ct)
                .ConfigureAwait(false);

            } // B7: disposing the lease hands the machine to the next queued plan.
        }
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            // B6: cooperative abort — a server cancel push or a superseding
            // dispatch fired this run's token; the running step's process tree
            // was killed by ScriptRunner. Report a failed completion for THIS
            // attempt (CancellationToken.None — the report must outlive the
            // cancelled token): for an operator cancel the server already
            // recorded the Cancelled verdict and the terminal guard swallows
            // this; for a superseded attempt the stale DispatchId drops it.
            var reason = run.CancelReason ?? "Cancelled on server request.";
            logger.LogInformation(
                "Deployment {DeploymentId} attempt {DispatchId} aborted: {Reason}",
                plan.DeploymentId, plan.DispatchId, reason);
            try
            {
                await serverLink
                    .CompleteDeploymentAsync(plan.DeploymentId, plan.DispatchId, false,
                        $"Aborted on the agent: {reason}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception inner)
            {
                logger.LogError(inner,
                    "Failed to report the aborted completion for {DeploymentId}.", plan.DeploymentId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled error executing deployment {DeploymentId}.", plan.DeploymentId);
            try
            {
                await serverLink
                    .CompleteDeploymentAsync(plan.DeploymentId, plan.DispatchId, false, ex.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception inner)
            {
                logger.LogError(inner,
                    "Failed to report deployment failure for {DeploymentId}.", plan.DeploymentId);
            }
        }
        }
        finally
        {
            // E8 — best-effort sweep of THIS attempt's dispatch subtree only
            // (staging/{deploymentId:N}/{dispatchId:N}). Per-step cleanup already
            // removes each step dir on every exit path; this catches anything a
            // hard-killed step left behind. It must NOT sweep the whole
            // staging/{deploymentId:N} tree: a superseding attempt that
            // force-detached this one may already be running under its own
            // dispatch dir, and a recursive delete of the shared parent would race
            // and destroy its live staging. A truly-orphaned prior attempt's
            // dispatch dir is reclaimed by the boot sweep. Non-fatal.
            try
            {
                var dir = StagingDispatchDir(
                    agentConfig.Value.ResolvedDataPath, plan.DeploymentId, plan.DispatchId);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Staging sweep for deployment {Id} dispatch {Dispatch} failed (non-fatal).",
                    plan.DeploymentId, plan.DispatchId);
            }

            // Remove exactly OUR entry — a superseding attempt may have already
            // force-detached it and registered its own. Signal completion so a
            // waiting superseder proceeds. The CTS is deliberately not disposed:
            // TryCancel may race the teardown, and an undisposed CTS without a
            // timer costs nothing.
            ((ICollection<KeyValuePair<Guid, RunningDeployment>>)_running)
                .Remove(new(plan.DeploymentId, run));
            run.Completed.TrySetResult();
        }
    }

    /// <summary>
    /// What <see cref="AcquireMachineSlotAsync"/> decided. Three states, because "did
    /// not acquire" splits two ways that the caller must treat differently: an
    /// escalation that has ALREADY been logged and reported (report again and the server
    /// sees two completions for one attempt) versus a silent unwind that must NOT be
    /// reported at all (nothing executed and the server will re-dispatch).
    /// <para>
    /// <see cref="AbandonedQuietly"/> is deliberately the ZERO value, so
    /// <c>default(MachineSlot)</c> — an unassigned field, a stray <c>return default</c>
    /// — means "do not execute" rather than "no lease, carry on", which is the ungated
    /// run F5 exists to make unrepresentable.
    /// </para>
    /// </summary>
    private enum SlotOutcome
    {
        /// <summary>Nothing executed and nothing was reported; unwind silently.</summary>
        AbandonedQuietly = 0,

        /// <summary>The gate is held.</summary>
        Held,

        /// <summary>Abandoned; the failed completion has already been sent.</summary>
        AbandonedAfterReporting,
    }

    private readonly record struct MachineSlot(
        MachineExecutionGate.Releaser? Lease, SlotOutcome Outcome)
    {
        /// <summary>The gate is held; dispose <see cref="Lease"/> to release it.</summary>
        internal static MachineSlot Held(MachineExecutionGate.Releaser lease)
            => new(
                lease ?? throw new ArgumentNullException(nameof(lease)),
                SlotOutcome.Held);

        /// <summary>Abandoned and already reported (wedged gate, or two attempts of one
        /// task that the gate cannot serialize).</summary>
        internal static MachineSlot AbandonedWedged { get; }
            = new(null, SlotOutcome.AbandonedAfterReporting);

        /// <summary>Abandoned with nothing to report: the agent or host is shutting down
        /// while this attempt was still QUEUED, so it never executed and the server's
        /// disconnect reconciliation owns the re-dispatch.</summary>
        internal static MachineSlot AbandonedQuietly { get; }
            = new(null, SlotOutcome.AbandonedQuietly);

        /// <summary>Whether the caller must skip the execution body.</summary>
        internal bool Abandoned => Outcome != SlotOutcome.Held;
    }

    /// <summary>
    /// B7/F2/F5 — takes this machine's execution gate for <paramref name="plan"/> on
    /// the side <see cref="ModeFor"/> picks, announcing the wait in the task log when
    /// the gate is busy. A target with <c>AllowParallelTaskExecution</c> takes the
    /// SHARED side rather than skipping the gate: it co-runs with other shared work
    /// but still queues behind any exclusive holder. Either way the F1
    /// project/environment/tenant serialization is unaffected — that is enforced
    /// server-side at claim time.
    /// <para>
    /// Returns <see cref="MachineSlot.AbandonedWedged"/> ONLY on the two
    /// force-detached-predecessor escalations, both reported before returning. Which one
    /// applies turns on whether the gate can keep the two attempts apart, i.e. on the
    /// PAIR of modes — not on this attempt's mode alone:
    /// <list type="bullet">
    ///   <item>BOTH shared — the gate would admit both as readers, so it cannot
    ///         serialize them; refuse outright, BEFORE acquiring.</item>
    ///   <item>Otherwise — one side is exclusive, so the gate DOES exclude them. The
    ///         wait is BOUNDED by <see cref="WedgedGateAcquireTimeout"/> and expiry
    ///         abandons this attempt rather than wedging the agent forever behind the
    ///         stuck step (a zombie heartbeating Online while silently never executing
    ///         again).</item>
    /// </list>
    /// </para>
    /// <para>
    /// A cancel or supersede while queued unblocks the wait via the run token; the
    /// resulting <see cref="OperationCanceledException"/> propagates to the
    /// aborted-completion catch in <see cref="ExecuteAsync"/> with nothing executed
    /// and nothing to release.
    /// </para>
    /// </summary>
    private async Task<MachineSlot> AcquireMachineSlotAsync(
        DeploymentPlan plan, MachineExecutionGate.Mode mode, bool forceDetachedStuck,
        MachineExecutionGate.Mode? detachedPredecessorMode, CancellationToken ct,
        CancellationToken hostStopping)
    {
        // F2-followup 2, still load-bearing after F5 — the flag opts out of
        // serializing against OTHER tasks. It must NOT opt out of the SAME-task
        // guarantee ("two attempts must never touch the same extract dirs / IIS
        // handles concurrently", above). Normally the supersede block upholds that by
        // cancelling and awaiting the old attempt; when that attempt is
        // non-cooperative it is force-detached and the machine gate is what keeps the
        // two apart. It can only do that if at least ONE of the pair is exclusive: if
        // both are shared the detached attempt holds a READ lease and this one would be
        // granted a second READ lease immediately, so both attempts of ONE task would
        // run over the same app pool, physical site path and services. Refuse then —
        // BEFORE acquiring, so no lease is taken — with the same shape as the
        // wedged-gate escalation: the server re-dispatches, and the stuck machine
        // surfaces for an operator.
        // Keyed on the PAIR, deliberately: an earlier cut tested only this attempt's
        // mode, which abandoned a shared successor to an EXCLUSIVE predecessor that the
        // gate would have excluded perfectly well — and, because the next re-dispatch
        // arrives with forceDetachedStuck false, sent it into the unbounded wait with no
        // escalation at all.
        if (forceDetachedStuck
            && mode == MachineExecutionGate.Mode.Shared
            && detachedPredecessorMode == MachineExecutionGate.Mode.Shared)
        {
            logger.LogError(
                "Task {DeploymentId} attempt {DispatchId} abandoned: a previous attempt " +
                "did not unwind and was force-detached, and this target allows parallel " +
                "task execution — so the machine gate would admit both attempts as " +
                "readers instead of serializing them. Refusing to run them concurrently.",
                plan.DeploymentId, plan.DispatchId);
            await LogAsync(plan.DeploymentId, "error",
                "--- A previous attempt of this task is still running and could not be " +
                "stopped. Because this target allows parallel task execution the machine " +
                "gate would admit both attempts at once, so this attempt is abandoned " +
                "rather than run concurrently with it. The machine likely needs the agent " +
                "restarted. ---", CancellationToken.None)
                .ConfigureAwait(false);
            await ReportAbandonedAttemptAsync(plan,
                "A previous attempt of this task did not unwind, and the target allows " +
                "parallel task execution, so the two attempts could not be serialized.")
                .ConfigureAwait(false);
            return MachineSlot.AbandonedWedged;
        }

        // F2-followup 1: the QUEUE wait also unblocks at host shutdown, so a plan
        // parked here does not survive the gate's DI disposal. Linked per-acquisition
        // and disposed immediately, so no registration accumulates on the long-lived
        // host token. Step execution is NOT linked: a shutdown must not abort a
        // running step.
        using var queueWait = CancellationTokenSource.CreateLinkedTokenSource(ct, hostStopping);
        var queueToken = queueWait.Token;

        try
        {
            if (await executionGate.TryAcquireNowAsync(mode, queueToken).ConfigureAwait(false)
                    is { } uncontended)
            {
                return MachineSlot.Held(uncontended);
            }

            await LogAsync(plan.DeploymentId, "info",
                mode == MachineExecutionGate.Mode.Shared
                    ? "--- Waiting for exclusive work to finish on this machine ---"
                    : "--- Waiting for other work to finish on this machine ---", ct)
                .ConfigureAwait(false);

            if (!forceDetachedStuck)
            {
                return MachineSlot.Held(
                    await executionGate.AcquireAsync(mode, queueToken).ConfigureAwait(false));
            }
        }
        catch (ObjectDisposedException)
        {
            // F5: the gate now completes its queued waiters on Dispose, where the old
            // SemaphoreSlim left them parked forever. That is the better failure, but it
            // must not be reported as a DEPLOYMENT failure: nothing executed, and letting
            // it reach ExecuteAsync's blanket catch would send the server
            // success: false with "Cannot access a disposed object. Object name:
            // 'KrakenDeploy.Agent.Deployment.MachineExecutionGate'" — an internal type
            // name in the operator's task log for a wave that never started. Unwind
            // silently instead and let B3's disconnect reconciliation re-dispatch, which
            // is what an agent going away mid-queue is supposed to look like.
            logger.LogInformation(
                "Task {DeploymentId} attempt {DispatchId} unwound while queued on the " +
                "machine execution gate: the agent is shutting down. Nothing executed; " +
                "the server will re-dispatch.",
                plan.DeploymentId, plan.DispatchId);
            return MachineSlot.AbandonedQuietly;
        }
        catch (OperationCanceledException) when (
            hostStopping.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Same shape via the other route: the host stopped while we were queued, so
            // the linked token fired but the RUN was never cancelled. ExecuteAsync's
            // cancellation catch filters on run.Cts, so this would otherwise land in the
            // blanket catch and be reported as a hard failure too.
            logger.LogInformation(
                "Task {DeploymentId} attempt {DispatchId} unwound while queued on the " +
                "machine execution gate: the host is stopping. Nothing executed; " +
                "the server will re-dispatch.",
                plan.DeploymentId, plan.DispatchId);
            return MachineSlot.AbandonedQuietly;
        }

        if (await executionGate.AcquireAsync(mode, WedgedGateAcquireTimeout, queueToken)
                .ConfigureAwait(false) is { } bounded)
        {
            return MachineSlot.Held(bounded);
        }

        logger.LogError(
            "Task {DeploymentId} attempt {DispatchId} could not acquire the machine " +
            "execution gate ({Mode}) within {Timeout} after force-detaching a stuck " +
            "predecessor; the agent appears wedged. Abandoning this attempt.",
            plan.DeploymentId, plan.DispatchId, mode, WedgedGateAcquireTimeout);
        await LogAsync(plan.DeploymentId, "error",
            "--- The agent is wedged: a previous task is not releasing the machine " +
            "execution slot. Abandoning this attempt; the machine likely needs the " +
            "agent restarted. ---", CancellationToken.None)
            .ConfigureAwait(false);
        await ReportAbandonedAttemptAsync(plan,
            "Agent wedged: a previous task did not release the machine execution slot.")
            .ConfigureAwait(false);
        return MachineSlot.AbandonedWedged;
    }

    /// <summary>
    /// Reports a failed completion for an attempt that never executed. Shared by the
    /// two abandonment paths in <see cref="AcquireMachineSlotAsync"/>. Best-effort by
    /// contract and on <see cref="CancellationToken.None"/> — the report must outlive
    /// whatever token caused the abandonment, and failing to send it must not mask the
    /// abandonment itself.
    /// </summary>
    private async Task ReportAbandonedAttemptAsync(DeploymentPlan plan, string reason)
    {
        try
        {
            await serverLink.CompleteDeploymentAsync(
                plan.DeploymentId, plan.DispatchId, success: false,
                errorMessage: reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to report the abandoned attempt {DispatchId} of {DeploymentId}.",
                plan.DispatchId, plan.DeploymentId);
        }
    }

    /// <summary>
    /// F2 — reports "this sub-plan has the machine and is executing now" so the
    /// server re-arms the wave deadline from gate acquisition rather than from
    /// dispatch. Swallows every failure: the report is purely advisory (a lost one
    /// leaves the wave on the server's dispatch-time backstop ceiling), so it must
    /// never fail a run that is otherwise about to execute fine.
    /// </summary>
    private async Task ReportExecutionStartedAsync(DeploymentPlan plan, CancellationToken ct)
    {
        try
        {
            await serverLink
                .ReportExecutionStartedAsync(plan.DeploymentId, plan.DispatchId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Failed to report execution start for {DeploymentId} attempt {DispatchId}; " +
                "the server falls back to its dispatch-time deadline ceiling.",
                plan.DeploymentId, plan.DispatchId);
        }
    }

    /// <summary>
    /// One step's outcome inside a wave. <see cref="Step"/> is kept on the
    /// record so the wave-merger can index outputs by step name without
    /// holding a parallel array.
    /// </summary>
    private sealed record StepOutcome(
        DeploymentStepPlan Step,
        bool Success,
        Dictionary<string, string> CapturedOutputs,
        bool Skipped = false);

    /// <summary>
    /// M14.4 — agent-side wave partitioning. Delegates the trigger-based
    /// grouping to the shared <c>WaveGrouping.Partition</c>
    /// (KrakenDeploy.Execution) — the same algorithm the server's
    /// <c>WavePartitioner</c> uses — so online and offline group identically.
    /// A wave = first step + all subsequent StartWithPrevious steps until the
    /// next StartAfterPrevious opens a new wave; the first step's trigger is
    /// ignored. The contract's <see cref="DeploymentStepPlan.StartTrigger"/>
    /// int is the source of truth (1 = StartWithPrevious). The agent
    /// intentionally omits the server's server/target classification +
    /// mixed-wave validation — offline a plan is single-side.
    /// </summary>
    private static List<List<DeploymentStepPlan>> PartitionIntoWaves(
        DeploymentStepPlan[] steps)
        => WaveGrouping.Partition(steps, s => s.Index, s => s.StartTrigger == 1);

    /// <summary>
    /// Runs a single step inside a wave, capturing outputs locally then
    /// reporting the per-step boundary to the server via M14.4's
    /// <see cref="IServerLink.ReportStepCompletedAsync"/>. Catches handler
    /// exceptions so one step's failure doesn't tear down sibling
    /// <c>Task.WhenAll</c> branches.
    /// </summary>
    private async Task<StepOutcome> RunStepInWaveAsync(
        DeploymentPlan basePlan,
        DeploymentPlan preWavePlan,
        DeploymentStepPlan step,
        bool orchestrateSteps,
        bool hasFailed,
        CancellationToken ct)
    {
        // Stamp this step's index for the async flow so every LogAsync in this
        // branch attributes its lines to the right step (used by the server's log
        // compactor). +1 so the AsyncLocal default reads as plan-level (-1). Waves
        // run steps via Task.WhenAll — each branch keeps its own value.
        _stepIndexPlusOne.Value = step.Index + 1;

        // Orchestrate mode (offline): evaluate the step's Run Condition. Skipped
        // steps don't run, don't fail the deployment, and produce no outputs.
        if (orchestrateSteps)
        {
            // Uses the SAME StepConditionEvaluator the server orchestrator runs
            // (KrakenDeploy.Execution) — identical Run/Skip decisions online and
            // offline. The contract's int Condition maps directly to the pinned
            // StepCondition enum values.
            var condition = (StepCondition)step.Condition;
            // Only a Variable run-condition reads the bag; Success/Failure/Always
            // ignore it. Build the (potentially copying) effective-variable
            // overlay + dictionary only for Variable so the common case doesn't
            // clone the deployment-wide variable set per step. The overlay
            // ensures a Variable expression referencing a step-scoped variable
            // sees the same value the step body will.
            VariableDictionary variableBag;
            if (condition == StepCondition.Variable)
            {
                // Array-index parity: a Variable condition referencing an
                // indexed element (e.g. #{Arr[0]}) must make the same Run/Skip
                // decision offline as online. The server expands StringArrays
                // into name[i] keys in its condition varDict
                // (BuildTargetDispatchContextAsync); without the same expansion
                // here, plan.Variables carries arrays only in comma-joined
                // scalar form, so #{Arr[0]} stays unresolved → the step is
                // wrongly skipped offline. Feed the effective overlay's
                // ArrayVariables through the shared formatter so the keys match.
                var effective = ApplyStepVariables(preWavePlan, step);
                variableBag = effective.Variables.ToVariableDictionary(effective.ArrayVariables);
            }
            else
            {
                variableBag = new VariableDictionary();
            }
            var decision = StepConditionEvaluator.Evaluate(
                condition,
                step.ConditionVariableExpression,
                hasFailed,
                variableBag);
            if (decision.Action == StepConditionEvaluator.Action.Skip)
            {
                // An Unresolved Variable condition (expression referenced a missing
                // variable or failed to parse) is an author error, not an intentional
                // skip — log it at warning so it stands out in the offline run log.
                // Online the server discriminates this via a dedicated
                // DeploymentVariableConditionUnresolved audit event; offline the log
                // line is the only operator-facing signal, so the level carries it.
                var level = decision.Kind == StepConditionEvaluator.Kind.Unresolved
                    ? "warning"
                    : "info";
                await LogAsync(basePlan.DeploymentId, level,
                    $"--- Step {step.Index + 1}: {step.Name} skipped: {decision.Reason} ---", ct)
                    .ConfigureAwait(false);
                return new StepOutcome(
                    step, Success: true,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Skipped: true);
            }
        }

        bool success;
        Dictionary<string, string> capturedOutputs;
        HashSet<string> sensitiveOutputs;
        string? errorMessage = null;
        try
        {
            (success, capturedOutputs, sensitiveOutputs) = orchestrateSteps
                ? await ExecuteStepWithRetriesAsync(preWavePlan, step, ct).ConfigureAwait(false)
                : await ExecuteStepAsync(preWavePlan, step, ct).ConfigureAwait(false);
            if (!success)
            {
                errorMessage = $"Step '{step.Name}' failed.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Re-throw so Task.WhenAll surfaces cancellation cleanly to the
            // outer dispatcher; nothing to report per-step (the server's
            // CT cancel is the source of truth).
            throw;
        }
        catch (Exception ex)
        {
            success = false;
            capturedOutputs = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            sensitiveOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            errorMessage = ex.Message;
            logger.LogError(ex,
                "Step '{Step}' threw during wave execution (deployment {Id}).",
                step.Name, basePlan.DeploymentId);
        }

        // M15.2 — report by AccumulatorKey when set (ForEach iterations).
        // Falls back to step.Name for non-iteration steps + pre-M15 plans
        // where AccumulatorKey is null. The accumulator key is what the
        // server stores against the output variables so cross-iteration
        // Octostache references via #{Octopus.Action[Deploy[0]].Output.X}
        // resolve correctly.
        var reportingKey = step.AccumulatorKey ?? step.Name;
        try
        {
            await serverLink.ReportStepCompletedAsync(
                basePlan.DeploymentId,
                basePlan.DispatchId,
                stepIndex:            step.Index,
                stepName:             reportingKey,
                success:              success,
                errorMessage:         errorMessage,
                outputVariables:      capturedOutputs,
                sensitiveOutputNames: sensitiveOutputs,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to report step completion for '{Step}' of deployment {Id}.",
                reportingKey, basePlan.DeploymentId);
        }

        return new StepOutcome(step, success, capturedOutputs);
    }

    // ── Orchestration (offline opt-in: condition / timeout / retry) ──────────────
    // Online these are server-driven; offline the executor owns them so a process
    // author sees identical semantics. Run-condition evaluation is shared with the
    // server via KrakenDeploy.Execution's StepConditionEvaluator (called from
    // RunStepInWaveAsync); the per-step timeout + retry loop mirrors
    // DeploymentWorker's RunServerStepWithRetries/Timeout.

    /// <summary>
    /// Runs a step with its per-step timeout, retried up to
    /// <see cref="DeploymentStepPlan.MaxRetries"/> times with
    /// <see cref="DeploymentStepPlan.RetryDelaySeconds"/> between attempts.
    /// Each attempt returns a fresh capture bag, so the returned outputs are the
    /// final attempt's only.
    /// <para>
    /// Delegates the retry loop + per-attempt timeout to the shared
    /// <see cref="StepRetryRunner"/> (KrakenDeploy.Execution), the same loop the
    /// server orchestrator runs, and keeps the agent's own log side-effects: a
    /// timeout-error line per timed-out attempt and the retry marker before each
    /// delay. No late-success marker (the agent never emitted one).
    /// </para>
    /// </summary>
    private async Task<(bool Success, Dictionary<string, string> Outputs, HashSet<string> SensitiveOutputs)> ExecuteStepWithRetriesAsync(
        DeploymentPlan preWavePlan, DeploymentStepPlan step, CancellationToken ct)
    {
        var outcome = await StepRetryRunner.RunAsync<(bool Success, Dictionary<string, string> Outputs, HashSet<string> SensitiveOutputs)>(
            step.Name,
            step.MaxRetries,
            step.RetryDelaySeconds,
            step.TimeoutSeconds,
            runAttempt: (CancellationToken attemptCt) => ExecuteStepAsync(preWavePlan, step, attemptCt),
            isSuccess: r => r.Success,
            onTimeoutResult: () => (false,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            onAttemptTimedOutAsync: timeoutSeconds => LogAsync(preWavePlan.DeploymentId, "error",
                $"--- Step '{step.Name}' timed out after " +
                $"{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}s ---", ct),
            onRetryAsync: info => LogAsync(preWavePlan.DeploymentId, "warning", info.Marker, ct),
            onLateSuccessAsync: null,
            ct).ConfigureAwait(false);

        return outcome.Result;
    }

    // ── Step execution ─────────────────────────────────────────────────────────

    private async Task<(bool Success, Dictionary<string, string> CapturedOutputs, HashSet<string> SensitiveOutputs)> ExecuteStepAsync(
        DeploymentPlan plan, DeploymentStepPlan step, CancellationToken ct)
    {
        await LogAsync(plan.DeploymentId, "info",
            $"--- Step {step.Index + 1}: {step.Name} ---", ct).ConfigureAwait(false);

        // Per-step bucket for Set-OctopusVariable captures. The wrapped LogAsync
        // intercepts ##octopus[...] markers and writes here instead of sending them
        // through as visible log lines.
        var capturedOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // T0-6: names (subset of capturedOutputs) marked sensitive via
        // Set-OctopusVariable -sensitive. Reported to the server so the value is
        // encrypted at rest + masked in the UI. The value is also folded into the
        // run's redactor at capture time so later log lines echoing it are masked.
        var sensitiveOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Resolve a handler. Two paths (Phase D-6):
        //   1. If the server pinned a StepPackageVersion, try the
        //      StepPackageLoader first — the package owns the step type and
        //      its handler takes precedence over any in-DI built-in that
        //      coincidentally claims the same step type. On cache miss the
        //      loader pulls the package via IStepPackageSource (D-5).
        //   2. Otherwise fall back to the in-DI handlers (the pre-D-6 path).
        //      Once D-8 has extracted every built-in into a package this
        //      fallback becomes the empty case and can be removed.
        var handler = await ResolveHandlerAsync(step, ct).ConfigureAwait(false);
        if (handler is null)
        {
            await LogAsync(plan.DeploymentId, "error",
                $"Unknown step type '{step.StepType}'. No handler is registered for it " +
                $"(pin={step.StepPackageName ?? "<null>"} {step.StepPackageVersion ?? "<null>"}).",
                ct).ConfigureAwait(false);
            return (false, capturedOutputs, sensitiveOutputs);
        }

        // E8: the DispatchId is in the path so a superseding re-dispatch of the
        // same task never shares a staging dir with the old attempt still
        // unwinding (SupersedeUnwindTimeout window) — otherwise the new attempt
        // could upload the OLD attempt's artifacts as its own.
        var tempRoot = StagingStepDir(
            agentConfig.Value.ResolvedDataPath, plan.DeploymentId, plan.DispatchId, step.Index);

        Directory.CreateDirectory(tempRoot);

        // E8: everything that writes under tempRoot runs inside this try so the
        // staging dir is removed on EVERY exit path — the early failure returns
        // (download / extract / ref-package) and the per-step timeout / cancel
        // OperationCanceledException, not just the normal tail (pre-E8 those all
        // leaked). Cleanup is best-effort (a locked file must not fail the step).
        try
        {
        // Per-step artifacts directory — scripts write files here and they are
        // streamed back to the server after the step completes.
        var artifactsDir = Path.Combine(tempRoot, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        var extractDir = string.Empty;

        // ── Package download + extract (skipped for steps that don't need it) ──
        if (handler.RequiresPackage && !string.IsNullOrWhiteSpace(step.PackageId))
        {
            string zipPath;
            try
            {
                await LogAsync(plan.DeploymentId, "info",
                    $"Downloading {step.PackageId} v{step.PackageVersion}…", ct)
                    .ConfigureAwait(false);

                zipPath = await packageDownloader
                    .DownloadAsync(step.PackageId, step.PackageVersion, tempRoot, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Per-step timeout / deployment cancel — let it reach the timeout
                // handler (orchestrate mode) or the outer cancel path, not get
                // mislabelled as a download failure.
                throw;
            }
            catch (Exception ex)
            {
                await LogAsync(plan.DeploymentId, "error",
                    $"Package download failed: {ex.Message}", ct).ConfigureAwait(false);
                return (false, capturedOutputs, sensitiveOutputs);
            }

            extractDir = Path.Combine(tempRoot, "extracted");
            try
            {
                await LogAsync(plan.DeploymentId, "info", "Extracting package…", ct)
                    .ConfigureAwait(false);
                await PackageExtractor.ExtractAsync(zipPath, extractDir, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // timeout / cancel — propagate, don't mislabel as extraction failure
            }
            catch (Exception ex)
            {
                await LogAsync(plan.DeploymentId, "error",
                    $"Package extraction failed: {ex.Message}", ct).ConfigureAwait(false);
                return (false, capturedOutputs, sensitiveOutputs);
            }
        }
        else if (handler.RequiresPackage)
        {
            // Handler wants a package but none is configured — use the staging root.
            extractDir = tempRoot;
        }

        // ── Referenced package download + extract ─────────────────────────────
        // For steps that declare Octopus.Action.Package.PackageReferences,
        // extract each one to extract/refs/<Name>/ and expose its path as an
        // env var / system variable (handled by the step handler).
        var refExtractRoot = string.Empty;
        var referencedExtractedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (step.ReferencedPackages is { Count: > 0 } refs)
        {
            refExtractRoot = Path.Combine(string.IsNullOrEmpty(extractDir) ? tempRoot : extractDir, "refs");
            Directory.CreateDirectory(refExtractRoot);

            foreach (var r in refs)
            {
                if (string.IsNullOrWhiteSpace(r.Version))
                {
                    await LogAsync(plan.DeploymentId, "warning",
                        $"Referenced package '{r.Name}' ({r.PackageId}) has no resolved version; skipping.", ct)
                        .ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await LogAsync(plan.DeploymentId, "info",
                        $"Downloading referenced package '{r.Name}': {r.PackageId} v{r.Version}…", ct)
                        .ConfigureAwait(false);

                    var refZipPath = await packageDownloader
                        .DownloadAsync(r.PackageId, r.Version, refExtractRoot, ct)
                        .ConfigureAwait(false);

                    if (r.Extract)
                    {
                        var refDir = Path.Combine(refExtractRoot, SanitisePathSegment(r.Name));
                        await PackageExtractor.ExtractAsync(refZipPath, refDir, ct).ConfigureAwait(false);
                        referencedExtractedPaths[r.Name] = refDir;
                    }
                    else
                    {
                        referencedExtractedPaths[r.Name] = refZipPath;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // timeout / cancel — propagate, don't mislabel as a fetch failure
                }
                catch (Exception ex)
                {
                    await LogAsync(plan.DeploymentId, "error",
                        $"Failed to fetch referenced package '{r.Name}': {ex.Message}", ct)
                        .ConfigureAwait(false);
                    return (false, capturedOutputs, sensitiveOutputs);
                }
            }
        }

        // ── Delegate to the handler ────────────────────────────────────────────
        // ##octopus[...] marker interceptor: a "sticky" log level set by
        // ##octopus[stdout-warning|error|default] persists until overridden.
        var stickyLevel = "info";

        async Task InterceptingLogAsync(string level, string message)
        {
            var msg = OctopusMessageParser.TryParse(message);
            switch (msg)
            {
                case SetVariableMessage v:
                    capturedOutputs[v.Name] = v.Value;
                    if (v.Sensitive)
                    {
                        sensitiveOutputs.Add(v.Name);
                        // Mask this value in every subsequent log line of this run
                        // (this step and later steps share the same redactor).
                        _redactor.Value?.Add([v.Value]);
                    }
                    return; // marker is not user-visible log output
                case SetLogLevelMessage l:
                    stickyLevel = l.Level;
                    return;
                case CreateArtifactMessage a:
                    // Artifact files are collected from the artifacts dir after the
                    // step; the marker itself is informational.
                    await LogAsync(plan.DeploymentId, "info",
                        $"[Artifact] {a.Name} ({a.Path})", ct).ConfigureAwait(false);
                    return;
                case ProgressMessage p:
                    await LogAsync(plan.DeploymentId, "info",
                        $"[Progress {p.Percentage}%] {p.Message}", ct).ConfigureAwait(false);
                    return;
                case UnknownMessage u:
                    logger.LogDebug(
                        "Unknown ##octopus[{Cmd}] directive in step '{Step}'; passing through as a log line.",
                        u.Command, step.Name);
                    break;
            }

            // Plain log line — apply sticky level if it overrides "info".
            var effectiveLevel = level.Equals("info", StringComparison.OrdinalIgnoreCase)
                ? stickyLevel
                : level;
            await LogAsync(plan.DeploymentId, effectiveLevel, message, ct).ConfigureAwait(false);
        }

        bool success;
        try
        {
            var handlerCtx = new StepHandlerContext
            {
                // Overlay this step's per-step variable delta (step/action scope)
                // onto the deployment-wide variables so $OctopusParameters +
                // Octostache for this step see step-scoped values. No-op when the
                // step carries no delta (the common case).
                Plan                   = ApplyStepVariables(plan, step),
                Step                   = step,
                ExtractDir             = extractDir,
                ArtifactsDir           = artifactsDir,
                LogAsync               = InterceptingLogAsync,
                ReferencedPackagePaths = referencedExtractedPaths,
            };

            success = await handler.HandleAsync(handlerCtx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Per-attempt timeout (StepRetryRunner's linked CancelAfter, orchestrate
            // mode) or a deployment-level cancel — a handler that honours its token
            // surfaces it here. Propagate (the package download/extract blocks above
            // already do) so StepRetryRunner classifies it as a timeout
            // ("--- Step 'X' timed out after Ns ---") instead of the generic catch
            // below mislabelling it "Step handler threw an unhandled exception".
            throw;
        }
        catch (Exception ex)
        {
            await LogAsync(plan.DeploymentId, "error",
                $"Step handler threw an unhandled exception: {ex.Message}", ct)
                .ConfigureAwait(false);
            success = false;
        }

        await LogAsync(plan.DeploymentId, success ? "info" : "error",
            success ? $"Step '{step.Name}' succeeded." : $"Step '{step.Name}' failed.",
            ct).ConfigureAwait(false);

        // ── Artifact collection ────────────────────────────────────────────────
        await CollectArtifactsAsync(plan, step, artifactsDir, ct).ConfigureAwait(false);

        return (success, capturedOutputs, sensitiveOutputs);
        }
        finally
        {
            // ── Cleanup staging (E8: reached on every exit path) ────────────────
            try { Directory.Delete(tempRoot, recursive: true); }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>
    /// Phase D-6 handler resolution (D-8.9: package-only, no in-DI fallback).
    /// Every step must have a <see cref="DeploymentStepPlan.StepPackageName"/>
    /// + <see cref="DeploymentStepPlan.StepPackageVersion"/> pin — set by
    /// <c>ProcessService.AddStepAsync</c> on author and re-resolved on
    /// release snapshot. The loader downloads the package on cache miss
    /// (gRPC) and instantiates the handler via Activator. Returns
    /// <c>null</c> when:
    /// <list type="bullet">
    ///   <item>the plan has no pin (the server should have failed earlier),</item>
    ///   <item>the loader can't find or load the package,</item>
    ///   <item>the loaded handler refuses the step type.</item>
    /// </list>
    /// The caller surfaces all three as an "unknown step type" error.
    /// Per the pre-prod policy in docs/architecture.md, there is no
    /// fallback to a hardcoded in-DI handler.
    /// </summary>
    private async Task<IStepHandler?> ResolveHandlerAsync(
        DeploymentStepPlan step, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.StepPackageName)
            || string.IsNullOrWhiteSpace(step.StepPackageVersion))
        {
            logger.LogError(
                "Step '{StepName}' (type {StepType}) has no step-package pin — " +
                "server failed to resolve a package for this step type at " +
                "release-creation. Re-create the release after installing a " +
                "package that claims this step type.",
                step.Name, step.StepType);
            return null;
        }

        try
        {
            var pkg = await stepPackageLoader
                .TryLoadOrDownloadAsync(step.StepPackageName, step.StepPackageVersion, ct)
                .ConfigureAwait(false);

            if (pkg is null) { return null; }

            // Activator-created — per-step-execution lifecycle.
            if (Activator.CreateInstance(pkg.HandlerType) is IStepHandler instance
                && instance.CanHandle(step.StepType))
            {
                return instance;
            }

            logger.LogError(
                "Step package {Name} {Version} loaded but its handler doesn't accept step type '{StepType}'.",
                step.StepPackageName, step.StepPackageVersion, step.StepType);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Step package {Name} {Version} failed to load.",
                step.StepPackageName, step.StepPackageVersion);
            return null;
        }
    }

    /// <summary>
    /// Replaces filesystem-unfriendly characters in a reference name so it can
    /// be used as a directory segment. The original name is still surfaced as
    /// <c>Octopus.Action.Package[Name].ExtractedPath</c>; this is only the
    /// on-disk path. Mirrors Octopus's behaviour: dots, dashes, alphanumerics
    /// kept; everything else collapsed to underscore.
    /// </summary>
    private static string SanitisePathSegment(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }
        var safe = sb.ToString();
        return string.IsNullOrEmpty(safe) ? "pkg" : safe;
    }

    // ── Output-variable plumbing ───────────────────────────────────────────────

    /// <summary>
    /// M15.2 + follow-up — delegates to
    /// <see cref="OutputVariableAccumulator.AugmentPlanWithPriorOutputs"/>.
    /// The accumulator was extracted from this file so the cross-iteration
    /// reference contract can be unit-tested without spinning up the full
    /// executor; this wrapper preserves the original call site for the
    /// per-wave snapshot path.
    /// </summary>
    private static DeploymentPlan AugmentPlanWithPriorOutputs(
        DeploymentPlan basePlan,
        Dictionary<string, Dictionary<string, string>> outputsByStep)
        => OutputVariableAccumulator.AugmentPlanWithPriorOutputs(basePlan, outputsByStep);

    /// <summary>
    /// Overlays a step's per-step variable delta onto the plan's deployment-wide
    /// variables. Returns the plan unchanged when the step has no delta. The
    /// delta values already won scope resolution server-side, so they take
    /// precedence over the deployment-wide values for this step.
    /// </summary>
    private static DeploymentPlan ApplyStepVariables(DeploymentPlan plan, DeploymentStepPlan step)
    {
        if (step.StepVariables is not { Count: > 0 } delta)
        {
            return plan;
        }

        var mergedScalars = new Dictionary<string, string>(plan.Variables, StringComparer.OrdinalIgnoreCase);
        var mergedArrays = new Dictionary<string, string[]>(plan.ArrayVariables, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in delta)
        {
            // Mirror the deployment-wide split: a StringArray arrives as its raw
            // JSON, so expose it BOTH as a parsed array ($OctopusArrays / #{each})
            // and a comma-joined scalar ($OctopusParameters), exactly like the
            // deployment-wide variables. A step-scoped scalar overriding a
            // deployment-wide array drops the stale array form.
            if (value.StartsWith('[') && TryParseStringArray(value, out var items))
            {
                mergedArrays[name] = items;
                mergedScalars[name] = string.Join(", ", items);
            }
            else
            {
                mergedScalars[name] = value;
                mergedArrays.Remove(name);
            }
        }

        return plan with { Variables = mergedScalars, ArrayVariables = mergedArrays };
    }

    private static bool TryParseStringArray(string value, out string[] items)
    {
        try
        {
            items = System.Text.Json.JsonSerializer.Deserialize<string[]>(value) ?? [];
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            items = [];
            return false;
        }
    }

    // ── Artifact collection ────────────────────────────────────────────────────

    private async Task CollectArtifactsAsync(
        DeploymentPlan plan,
        DeploymentStepPlan step,
        string artifactsDir,
        CancellationToken ct)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(artifactsDir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return; // directory was cleaned up or never created — nothing to do
        }

        if (files.Length == 0)
        {
            return;
        }

        await LogAsync(plan.DeploymentId, "info",
            $"Collecting {files.Length} artifact(s) from step '{step.Name}'…", ct)
            .ConfigureAwait(false);

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var artifactId = await artifactUploader.UploadAsync(
                plan.DeploymentId, step.Name, filePath, ct)
                .ConfigureAwait(false);

            if (artifactId is not null)
            {
                var rel = Path.GetRelativePath(artifactsDir, filePath);
                await LogAsync(plan.DeploymentId, "info",
                    $"Artifact collected: {rel}", ct).ConfigureAwait(false);
            }
        }
    }

    // ── Logging helper ─────────────────────────────────────────────────────────

    private async Task LogAsync(
        Guid deploymentId, string level, string message, CancellationToken ct)
    {
        // T0-6: mask known sensitive values before the line is persisted or
        // streamed. Also mask the local debug log — a secret is a secret in
        // every sink. Redact is a no-op when no secrets are registered.
        message = _redactor.Value?.Redact(message) ?? message;
        logger.LogDebug("[Deployment {Id}] {Level}: {Message}", deploymentId, level, message);
        // -1 = plan-level (no step in this async flow); otherwise the running step.
        var stepIndex = _stepIndexPlusOne.Value - 1;
        try
        {
            await serverLink
                .AppendLogAsync(deploymentId, _dispatchId.Value, stepIndex, level, message, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to send log line to server for deployment {Id}.", deploymentId);
        }
    }

    // ── Staging paths + orphan sweep (E8) ────────────────────────────────────

    /// <summary>Root of all per-step staging trees under the agent data dir.</summary>
    internal static string StagingRoot(string dataPath) =>
        Path.Combine(dataPath, "staging");

    /// <summary>A task's staging subtree: <c>staging/{deploymentId:N}</c>. Holds
    /// every dispatch attempt of the task. Only ever wholesale-deleted by the
    /// boot sweep (nothing runs at boot) — a per-run finally must NOT delete this
    /// level or it can race a concurrent sibling attempt (see
    /// <see cref="StagingDispatchDir"/>).</summary>
    internal static string StagingDeploymentDir(string dataPath, Guid deploymentId) =>
        Path.Combine(StagingRoot(dataPath), deploymentId.ToString("N"));

    /// <summary>One dispatch attempt's staging subtree:
    /// <c>staging/{deploymentId:N}/{dispatchId:N}</c>. The DispatchId segment
    /// isolates concurrent attempts of the same task (E8), so this is the coarsest
    /// dir a per-run finally may safely sweep.</summary>
    internal static string StagingDispatchDir(
        string dataPath, Guid deploymentId, Guid dispatchId) =>
        Path.Combine(
            StagingDeploymentDir(dataPath, deploymentId),
            dispatchId.ToString("N"));

    /// <summary>One step's staging dir:
    /// <c>staging/{deploymentId:N}/{dispatchId:N}/{stepIndex}</c>.</summary>
    internal static string StagingStepDir(
        string dataPath, Guid deploymentId, Guid dispatchId, int stepIndex) =>
        Path.Combine(
            StagingDispatchDir(dataPath, deploymentId, dispatchId),
            stepIndex.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// E8 — best-effort wipe of the ENTIRE staging root at agent boot. Nothing is
    /// executing yet (the machine gate is empty and no plan handler is wired), so
    /// every <c>staging/…</c> tree present is an orphan a previous process left when
    /// it died mid-step. Non-fatal: a locked/held path is logged and skipped.
    /// Called once from <c>ServerLinkHostedService</c> before the hub connection
    /// opens. NOT called on the offline-runner path — an offline run may share the
    /// box with a live agent, and per-step / per-deployment cleanup already covers
    /// a single offline invocation.
    /// </summary>
    public void SweepOrphanedStagingOnBoot()
    {
        var root = StagingRoot(agentConfig.Value.ResolvedDataPath);
        if (!Directory.Exists(root))
        {
            return;
        }
        try
        {
            Directory.Delete(root, recursive: true);
            logger.LogInformation("Swept orphaned agent staging root {Root} on boot.", root);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Boot sweep of the staging root {Root} failed (non-fatal).", root);
        }
    }
}
