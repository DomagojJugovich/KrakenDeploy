using System.Globalization;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Logging;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Octostache;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// What the orchestrator should do when it reaches a wave containing an
/// <c>Octopus.Manual</c> step.
/// </summary>
internal enum ManualGateAction
{
    /// <summary>A gate on this wave has no decision yet: persist the checkpoint,
    /// park the task <c>Paused</c> and return, freeing the node slot.</summary>
    Pause,

    /// <summary>Every gate on this wave was approved — run the wave.</summary>
    Approved,

    /// <summary>The gate's own run condition (or role filter) excludes it, so it must
    /// NOT pause: record it <c>Skipped</c> like any other skipped step and carry on.
    /// Without this, a gate authored <c>Condition=Failure</c> — the natural way to
    /// write "approve the rollback" — paused every healthy deployment.</summary>
    Skip,

    /// <summary>A gate on this wave was rejected (or timed out). Skip the wave's
    /// work, set the task's failing flag so later waves' <c>Failure</c>/<c>Always</c>
    /// cleanup steps run, and finalise <c>Failed</c>.</summary>
    Rejected,

    /// <summary>The gate cannot be evaluated — a misconfigured responsible-team
    /// list, or a state the engine's invariants forbid. Fail the task with
    /// <see cref="ManualGateDecision.FailureReason"/> rather than guessing.</summary>
    Fail,
}

/// <summary>
/// Outcome of evaluating a wave's manual-intervention gates. Each action reads a
/// different field, so they are separate rather than one overloaded
/// <c>Interruption?</c> that is meaningless on three of four branches:
/// <list type="bullet">
///   <item><see cref="ManualGateAction.Pause"/> → <see cref="Pending"/>, the gate
///     just created.</item>
///   <item><see cref="ManualGateAction.Approved"/> / <see cref="ManualGateAction.Rejected"/>
///     → <see cref="Resolved"/>, every already-answered gate on the wave, so the
///     caller records their outcomes WITHOUT re-querying what this evaluation
///     already loaded.</item>
///   <item><see cref="ManualGateAction.Skip"/> → <see cref="Resolved"/> is empty;
///     the gate's run condition excluded it.</item>
///   <item><see cref="ManualGateAction.Fail"/> → <see cref="FailureReason"/>.</item>
/// </list>
/// <para>
/// <see cref="SkippedSteps"/> / <see cref="SkipReason"/> are populated on EVERY action,
/// not only <see cref="ManualGateAction.Skip"/>. The caller removes the wave's whole
/// gate set whichever branch fires, so a condition-excluded gate that shared a wave
/// with an applicable one previously disappeared with no log line and no
/// <c>TaskStepOutcome</c> — unlike every other step type, which records
/// <c>Skipped</c>.
/// </para>
/// </summary>
internal sealed record ManualGateDecision(
    ManualGateAction Action,
    Interruption? Pending = null,
    IReadOnlyList<Interruption>? Resolved = null,
    string? FailureReason = null,
    IReadOnlyList<DeploymentStepPlan>? SkippedSteps = null,
    string? SkipReason = null,
    // Names of the responsible teams, for the pause log line and the audit details.
    // Ids alone ("1 responsible team(s)") told a reviewer nothing about WHO could
    // answer, which is the first thing a change-control review asks.
    IReadOnlyList<string>? ResponsibleTeamNames = null);

/// <summary>
/// WP3 — the manual-intervention gate: the server-side decision point that turns
/// an <c>Octopus.Manual</c> step into a real pause/approve/reject.
/// <para>
/// The gate is TASK-GLOBAL (not per-target) and sits at a wave boundary, which is
/// why <c>Octopus.Manual</c> is in <see cref="WavePartitioner.ServerOnlyStepTypes"/>.
/// It never executes anything: it either parks the task or reports what a human
/// already decided, and the orchestrator records the matching
/// <see cref="StepOutcomeKind"/>.
/// </para>
/// </summary>
internal static class ManualInterventionGate
{
    /// <summary>The gate steps in a wave, in declared order. Empty for the
    /// overwhelming majority of waves — the orchestrator's fast path.</summary>
    public static List<DeploymentStepPlan> GateStepsIn(WavePartitioner.Wave wave)
    {
        ArgumentNullException.ThrowIfNull(wave);
        return [.. wave.Steps.Where(s => s.StepType.Equals(
            ManualInterventionConfigKeys.StepType, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Decides what to do about <paramref name="gateSteps"/> on the wave about to run.
    /// <para>
    /// <b>Order matters, and it is: recorded refusals → run condition (recorded
    /// approvals bypass it, WP3-c) → approvals.</b>
    /// </para>
    /// <para>
    /// <b>1. A recorded REFUSAL wins outright</b>, before the condition is consulted.
    /// Both the <c>Condition</c> and the role filter are evaluated against LIVE state
    /// (target roles are re-read every dispatch; a runbook run resolves variables
    /// live), so they can flip between the pause and the resume. Filtering first meant
    /// a reviewer's rejection was never read: the gate reported <c>Skip</c>, the wave
    /// ran, and the task finalised <c>Succeeded</c> — deploying exactly what a human
    /// refused, leaving only an orphaned <c>Rejected</c> row behind.
    /// </para>
    /// <para>
    /// <b>2. Then the run condition + role filter.</b> A gate is a step like any other,
    /// so a gate that does not apply must not pause. Skipping this check made a
    /// <c>Condition=Failure</c> gate — the natural way to write "approve the
    /// rollback" — pause every HEALTHY deployment for the full timeout while holding
    /// its (project, environment, tenant) slot. An excluded gate is recorded
    /// <c>Skipped</c> like any other skipped step, and the excluded set rides along on
    /// EVERY action (not just <see cref="ManualGateAction.Skip"/>) because the caller
    /// strips the whole gate set from the wave whichever branch fires.
    /// </para>
    /// <para>
    /// <b>3. Then the approval state</b> of the gates that do apply, as an ALLOW-LIST:
    /// only <see cref="InterruptionStatus.Approved"/> proceeds. A deny-list ("anything
    /// that is not a rejection") made <see cref="InterruptionStatus.Cancelled"/> — and
    /// would make any status added later — read as an approval and run the gated wave.
    /// A <c>Pending</c> gate on a <c>Running</c> task is an INVARIANT VIOLATION rather
    /// than a state to recover from: the only way to leave <c>Paused</c> is a resolved
    /// gate, so a pending one here means the resume guard was bypassed. Both fail the
    /// task (pre-production policy: throw, don't paper over).
    /// </para>
    /// </summary>
    public static async Task<ManualGateDecision> EvaluateAsync(
        KrakenDbContext db,
        ServerTask task,
        IReadOnlyList<DeploymentStepPlan> gateSteps,
        StepSnapshot[] snapshotSteps,
        VariableDictionary varDict,
        VariableDictionary instructionVarDict,
        bool hasFailedAtWaveStart,
        Func<DeploymentStepPlan, bool> appliesToTask,
        SecretRedactor redactor,
        TimeSpan defaultTimeout,
        TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(gateSteps);
        ArgumentNullException.ThrowIfNull(appliesToTask);

        // ── 1. Load every gate's recorded state FIRST ───────────────────────────
        // Before the run condition, deliberately. A gate that already has a DECISION
        // must be honoured no matter what the condition or role filter now says: both
        // are evaluated against LIVE state (target roles are re-read on every dispatch,
        // and a runbook run resolves its variables live), so between the pause and the
        // resume they can flip. Evaluating the filter first meant a reviewer's recorded
        // REJECTION was never even read — the gate reported Skip, the wave ran, and the
        // task finalised Succeeded, deploying exactly what a human refused.
        // Filter-free: the worker's background scope has no ambient Space.
        var allIndexes = gateSteps.Select(s => s.Index).ToArray();
        var existing = await db.Interruptions
            .IgnoreQueryFilters()
            .Where(i => i.TaskId == task.Id && allIndexes.Contains(i.StepIndex))
            .ToDictionaryAsync(i => i.StepIndex, ct)
            .ConfigureAwait(false);

        // ── 2. Any recorded refusal wins outright ───────────────────────────────
        // Walked in declared order so the FIRST refusal is the one reported. Also
        // short-circuits materialising a later gate after an earlier refusal, which
        // would park the task again and ask a second team to approve work that is
        // already dead, buying another full timeout window on the F1 slot.
        foreach (var step in gateSteps.OrderBy(s => s.Index))
        {
            if (existing.TryGetValue(step.Index, out var answered)
                && answered.Status.IsRejection())
            {
                return new ManualGateDecision(
                    ManualGateAction.Rejected, Resolved: [answered]);
            }
        }

        // ── 3. Run condition + role filter ──────────────────────────────────────
        var applicable = new List<DeploymentStepPlan>(gateSteps.Count);
        var skipped = new List<DeploymentStepPlan>();
        string? skipReason = null;
        foreach (var step in gateSteps)
        {
            var snapshot = snapshotSteps[step.Index];

            // WP3-c — a recorded APPROVAL is honoured before the filter, the same
            // way §1 honours a refusal and for the same reason: both filters read
            // LIVE state and can flip during the pause. Without this, a fail-through
            // resume (hasFailed forced true) recorded a human's approval as
            // "Run condition excluded this gate" — the approval vanished from the
            // change-control record. Identity is checked by NAME too: after a
            // partition shift the same index can hold a different gate, and an
            // approval must not launder onto it (step 4 refuses that case when the
            // new gate is applicable; when the filter excludes it, the stale row is
            // simply not consumed and the gate skips like any other excluded step).
            if (existing.TryGetValue(step.Index, out var priorDecision)
                && priorDecision.Status == InterruptionStatus.Approved
                && string.Equals(priorDecision.StepName, snapshot.Name, StringComparison.Ordinal))
            {
                applicable.Add(step);
                continue;
            }

            var decision = StepConditionEvaluator.Evaluate(
                snapshot.Condition, snapshot.ConditionVariableExpression,
                hasFailedAtWaveStart, varDict);
            if (decision.Action == StepConditionEvaluator.Action.Skip)
            {
                skipped.Add(step);
                skipReason ??= decision.Reason;
                continue;
            }
            if (!appliesToTask(step))
            {
                skipped.Add(step);
                skipReason ??= "Step roles don't overlap the task's target roles.";
                continue;
            }
            applicable.Add(step);
        }

        if (applicable.Count == 0)
        {
            return new ManualGateDecision(
                ManualGateAction.Skip,
                SkippedSteps: skipped,
                SkipReason: skipReason ?? "Run condition excluded this gate.");
        }

        // ── 4. Approval state of the gates that DO apply ────────────────────────
        // `skipped` rides along on EVERY action from here on: the caller strips the
        // whole gate set from the wave regardless of which branch fires, so without
        // this a condition-excluded gate sharing a wave with an applicable one
        // vanished — no log line, no TaskStepOutcome — while every other step type
        // records Skipped.
        var resolved = new List<Interruption>(applicable.Count);
        foreach (var step in applicable)
        {
            if (!existing.TryGetValue(step.Index, out var interruption))
            {
                // First arrival at this gate — materialise it and park.
                return await CreateAsync(
                    db, task, step, snapshotSteps[step.Index], instructionVarDict,
                    redactor, defaultTimeout, time, skipped, skipReason, ct)
                    .ConfigureAwait(false);
            }

            // ALLOW-LIST, not a deny-list. Only Approved proceeds; every other state —
            // including Cancelled and any variant added later — refuses. Testing for
            // "not a rejection" instead made Cancelled read as an APPROVAL and run the
            // gated wave, with OutcomeFor then throwing out of the wave loop.
            switch (interruption.Status)
            {
                // WP3-c — the approval must belong to THIS gate. StepIndex alone is
                // not identity once a partition shift is survivable (the fail-through
                // resume): the same index can now hold a different gate, and applying
                // the old approval to it would run work nobody reviewed.
                case InterruptionStatus.Approved when string.Equals(
                    interruption.StepName, snapshotSteps[step.Index].Name,
                    StringComparison.Ordinal):
                    resolved.Add(interruption);
                    break;

                case InterruptionStatus.Approved:
                    return new ManualGateDecision(ManualGateAction.Fail, interruption,
                        FailureReason:
                        $"A recorded approval belongs to step '{interruption.StepName}', " +
                        $"but this wave's gate is '{snapshotSteps[step.Index].Name}'. The " +
                        "plan shifted while the task was paused — refusing to apply an " +
                        "approval to a different gate.",
                        SkippedSteps: skipped, SkipReason: skipReason);

                case InterruptionStatus.Pending:
                    // An INVARIANT VIOLATION, not a state to recover from: the only way
                    // to leave Paused is a resolved gate, so a pending one on a Running
                    // task means the resume guard was bypassed.
                    return new ManualGateDecision(ManualGateAction.Fail, interruption,
                        FailureReason:
                        $"Manual intervention '{interruption.StepName}' is still awaiting a " +
                        "decision, but the task is Running rather than Paused. The resume " +
                        "guard was bypassed — refusing to run the gated wave.",
                        SkippedSteps: skipped, SkipReason: skipReason);

                default:
                    return new ManualGateDecision(ManualGateAction.Fail, interruption,
                        FailureReason:
                        $"Manual intervention '{interruption.StepName}' is " +
                        $"{interruption.Status}, which is not an approval — refusing to run " +
                        "the gated wave. A gate proceeds only when it was explicitly " +
                        "approved.",
                        SkippedSteps: skipped, SkipReason: skipReason);
            }
        }

        return new ManualGateDecision(
            ManualGateAction.Approved, Resolved: resolved,
            SkippedSteps: skipped, SkipReason: skipReason);
    }

    /// <summary>
    /// Builds (but does not save) the <see cref="Interruption"/> for a gate the task
    /// has just reached. Staged on <paramref name="db"/> so the caller's
    /// <c>ServerTaskStatusWriter</c> transition saves the row and the
    /// <c>Running → Paused</c> flip in ONE transaction — a crash between them would
    /// otherwise leave either a paused task nobody can answer or a gate on a task
    /// that kept running.
    /// </summary>
    private static async Task<ManualGateDecision> CreateAsync(
        KrakenDbContext db,
        ServerTask task,
        DeploymentStepPlan step,
        StepSnapshot snapshot,
        VariableDictionary instructionVarDict,
        SecretRedactor redactor,
        TimeSpan defaultTimeout,
        TimeProvider time,
        IReadOnlyList<DeploymentStepPlan> skipped,
        string? skipReason,
        CancellationToken ct)
    {
        // Resolve the responsible teams BEFORE creating the gate, through the SHARED
        // resolver that ProcessService also uses at save time. Sharing is the point: an
        // unresolvable token or an "Everyone" team must never degrade to an empty list,
        // because empty means "anyone holding the respond permission" — so a second
        // implementation of these rules that drifted would fail OPEN. Validating at save
        // is the earlier feedback; this remains the fail-closed backstop, because a
        // process can also arrive by REST or by import without passing the editor.
        var teams = await ResponsibleTeamResolver
            .ResolveAsync(db, task.SpaceId, snapshot.Name, step.Config, ct)
            .ConfigureAwait(false);
        if (!teams.IsValid)
        {
            return new ManualGateDecision(
                ManualGateAction.Fail,
                FailureReason: $"{teams.Error} Refusing to run the gate.");
        }

        var now = time.GetUtcNow();

        // Per-step timeout wins; blank (or refused) falls back to the engine default.
        // Both are guaranteed POSITIVE — ParseTimeout rejects 0 and EngineOptions'
        // validator refuses a zero default — so the gate always gets an expiry and the
        // timeout sweeper can always reap it. An unexpiring gate would park the task on
        // its (project, environment, tenant) key indefinitely.
        var timeout = ManualInterventionConfigKeys.ParseTimeout(
            ManualInterventionConfigKeys.Read(
                step.Config, ManualInterventionConfigKeys.TimeoutHours))
            ?? defaultTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            return new ManualGateDecision(
                ManualGateAction.Fail,
                FailureReason:
                $"Manual intervention '{snapshot.Name}' resolved to a non-positive " +
                $"timeout ({timeout}). A gate with no expiry is never reaped and would " +
                "hold this project + environment slot indefinitely. Set " +
                "Engine:DefaultInterventionTimeout to a positive duration.",
                SkippedSteps: skipped, SkipReason: skipReason);
        }

        var rawInstructions = ManualInterventionConfigKeys.Read(
            step.Config, ManualInterventionConfigKeys.Instructions);
        if (string.IsNullOrWhiteSpace(rawInstructions))
        {
            rawInstructions = ManualInterventionConfigKeys.Read(
                step.Config, ManualInterventionConfigKeys.LegacyInstructionsKey);
        }

        var interruption = new Interruption
        {
            SpaceId            = task.SpaceId,
            TaskId             = task.Id,
            StepIndex          = step.Index,
            StepName           = snapshot.Name,
            // Evaluated NOW so the approver reads real values (a resumed run would
            // re-resolve against a different bag; the gate's text must be the one the
            // human actually agreed to), against a bag whose SENSITIVE VALUES ARE
            // ALREADY MASKED. This column is plain text, served over REST and rendered
            // to holders of InterruptionView, who do not need VariableView — so
            // `#{Db.Password}` in instructions must not reach it.
            //
            // Masking the dictionary is what makes that hold; redacting the rendered
            // output does not. Redact is an ordinal substring match on the raw secret,
            // so `#{Db.Password | ToBase64}` (or | ToUpper, | Md5 — all shipped in
            // Octostache) produces a string it cannot recognise, and the transformed
            // secret persists in the clear on a row no retention sweep touches. A
            // filter cannot launder what was never in the bag. Redact still runs
            // afterwards as defence in depth, for a secret that reached the text by
            // some path other than a variable reference.
            Instructions       = string.IsNullOrWhiteSpace(rawInstructions)
                                     ? null
                                     : redactor.Redact(
                                           instructionVarDict.Evaluate(rawInstructions)),
            ResponsibleTeamIds = teams.TeamIds,
            // Names captured HERE, while the teams demonstrably still exist. Resolving
            // them at decision time would fail in the one case that matters most: the
            // break-glass path exists because a named team can be deleted while the gate
            // waits, and a change-control record reading "team 3f9a…" is no record.
            ResponsibleTeamNames = [.. teams.TeamNames],
            Status             = InterruptionStatus.Pending,
            ExpiresUtc         = now + timeout,
            CreatedUtc         = now,
        };

        db.Interruptions.Add(interruption);
        return new ManualGateDecision(
            ManualGateAction.Pause, Pending: interruption,
            SkippedSteps: skipped, SkipReason: skipReason,
            ResponsibleTeamNames: teams.TeamNames);
    }

    /// <summary>
    /// Human-readable summary of a gate for the task log — the line an operator sees
    /// when they open a paused deployment.
    /// </summary>
    public static string DescribePause(
        Interruption interruption, IReadOnlyList<string>? responsibleTeamNames)
    {
        ArgumentNullException.ThrowIfNull(interruption);
        // NAME the teams. The previous "N responsible team(s)" told a reviewer nothing
        // about who was supposed to answer — the first question asked when a change
        // stalls, and the only way to notice a mis-pointed team id from the log alone.
        // Prefer the names the caller just resolved, else the ones persisted on the row
        // (WP3-b), so this reads correctly on a resume too — before that, a restart left
        // only the count.
        var names = responsibleTeamNames is { Count: > 0 }
            ? responsibleTeamNames
            : interruption.ResponsibleTeamNames;
        var who = interruption.ResponsibleTeamIds.Length == 0
            ? "anyone in this Space with permission to respond"
            : names is { Count: > 0 }
                ? $"team(s) {string.Join(", ", names)}"
                : $"{interruption.ResponsibleTeamIds.Length.ToString(CultureInfo.InvariantCulture)} " +
                  "responsible team(s)";
        // Always present since WP3-b: a zero timeout (which produced a null expiry) is
        // refused at save and by the options validator, because an unexpiring gate parks
        // the task on its F1 slot forever. The null arm stays for rows written before.
        var deadline = interruption.ExpiresUtc is { } expiry
            ? $"Auto-fails at {expiry:O} if unanswered."
            : "No auto-fail timeout recorded — this gate waits indefinitely.";
        return $"--- PAUSED for manual intervention: '{interruption.StepName}'. " +
               $"Awaiting approval from {who}. {deadline} ---";
    }

    /// <summary>
    /// Maps a gate DECISION to the step outcome recorded on the Steps tab. Only the
    /// three <c>IsDecision</c> states map; <see cref="InterruptionStatus.Pending"/> and
    /// <see cref="InterruptionStatus.Cancelled"/> have no outcome (nothing was decided),
    /// and callers must not reach here with them — the gate's allow-list refuses both
    /// before any outcome is recorded.
    /// </summary>
    public static StepOutcomeKind OutcomeFor(InterruptionStatus status) => status switch
    {
        InterruptionStatus.Approved => StepOutcomeKind.ManualInterventionApproved,
        InterruptionStatus.Rejected => StepOutcomeKind.ManualInterventionRejected,
        InterruptionStatus.TimedOut => StepOutcomeKind.ManualInterventionTimedOut,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status,
            "Only a gate DECISION has a step outcome."),
    };
}
