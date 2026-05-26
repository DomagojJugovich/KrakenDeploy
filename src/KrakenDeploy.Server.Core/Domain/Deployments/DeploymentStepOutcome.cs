using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// M14.5 — terminal outcome of a single step within a deployment.
/// One row per <see cref="StepIndex"/> per deployment, written by the
/// orchestrator (server-side steps) or the agent's per-step boundary
/// callback (target-side steps via <c>AgentHub.ReportStepCompletedAsync</c>).
///
/// <para>
/// Surfaces on the deployment detail page's "Steps" tab so an operator
/// gets a one-glance view of "what happened in each step" without
/// scraping the log stream. Distinguishes runtime states the M14
/// engine produces — Skipped (Condition gate didn't match), TimedOut
/// (M14.2 per-step timeout fired), Failed (handler returned false /
/// agent reported failure), Succeeded.
/// </para>
///
/// <para>
/// <strong>Retry attribution:</strong> <see cref="AttemptCount"/> is
/// the total number of attempts including the final one — 1 means no
/// retries fired, N+1 means N retries happened. The terminal
/// <see cref="Outcome"/> is the outcome of the final attempt. M14.3's
/// per-attempt audit rows (<c>Deployment.StepRetried</c>) carry the
/// non-final attempt detail; this entity captures the summary.
/// </para>
///
/// <para>
/// <strong>Upsert semantics:</strong> the orchestrator writes the row
/// once per step (final outcome). The agent's per-step boundary callback
/// upserts by (DeploymentId, StepIndex) — re-deliveries on reconnect or
/// retry attempts overwrite the prior row's outcome / attempt count /
/// duration without creating duplicates.
/// </para>
/// </summary>
public class DeploymentStepOutcome : Entity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;

    /// <summary>
    /// Plan-level <c>DeploymentStepPlan.Index</c> — stable across the
    /// deployment so callers can join against the release's
    /// <c>StepSnapshot[Index]</c>. Forms the natural upsert key with
    /// <see cref="DeploymentId"/>.
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>
    /// Step name at execution time. Mirrors what the operator sees on
    /// the live-log header and the audit rows; preserved here so the
    /// Steps tab doesn't need to re-resolve via the snapshot when
    /// rendering historical deployments.
    /// </summary>
    public required string StepName { get; set; }

    /// <summary>Terminal outcome of the step (after any retries).</summary>
    public StepOutcomeKind Outcome { get; set; }

    /// <summary>
    /// Total attempts including the final one. 1 = no retries fired;
    /// N+1 = N retries happened. The retry-loop helpers cap this at
    /// <c>MaxRetries + 1</c>.
    /// </summary>
    public int AttemptCount { get; set; } = 1;

    /// <summary>Final-attempt error message; null when
    /// <see cref="Outcome"/> is Succeeded or Skipped.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>When the step's first attempt started. Null for
    /// <see cref="StepOutcomeKind.Skipped"/> (the step never ran).</summary>
    public DateTimeOffset? StartedUtc { get; set; }

    /// <summary>When the step's terminal outcome was recorded.</summary>
    public DateTimeOffset CompletedUtc { get; set; }

    /// <summary>True for server-side steps (orchestrator-executed),
    /// false for target-side (agent-executed). Lets the Steps tab show
    /// a "Server" / "Target" chip and lets future analysis split
    /// timing per side.</summary>
    public bool IsServerSide { get; set; }

    /// <summary>Snapshot of <c>StepSnapshot.Required</c> at execution
    /// time. Preserved so the Steps tab can show a "Required" chip
    /// without re-joining the release snapshot, and so historical
    /// outcomes survive process edits.</summary>
    public bool Required { get; set; }

    /// <summary>
    /// M-RollingDeployments groundwork — the target this outcome belongs
    /// to. Pre-multi-target every deployment ran against exactly one
    /// target, so a (DeploymentId, StepIndex) tuple uniquely identified
    /// an outcome row. With multi-target dispatch, the same step runs
    /// once per target, so the tuple becomes (DeploymentId, StepIndex,
    /// TargetId).
    ///
    /// <para>
    /// Nullable for two reasons: (a) backfill compatibility — the
    /// migration sets this to the parent deployment's
    /// <c>Deployment.TargetId</c> for existing rows, but offline /
    /// drop-bundle deployments may have null TargetId; (b) server-side
    /// steps that aren't bound to a specific target (no
    /// <c>TargetRoles</c>) can leave it null since they run once per
    /// deployment regardless of the target set.
    /// </para>
    /// </summary>
    public Guid? TargetId { get; set; }
}

/// <summary>
/// Terminal outcome categories surfaced on the deployment detail
/// Steps tab. The enum mirrors the runtime states the M14 engine
/// produces; values are stored as ints so adding a new variant
/// (e.g. ManualInterventionApproved when that step type lands) is
/// additive.
/// </summary>
public enum StepOutcomeKind
{
    /// <summary>Step ran to completion successfully (after any retries).</summary>
    Succeeded = 0,

    /// <summary>Step ran but its final attempt failed. The
    /// <see cref="DeploymentStepOutcome.Required"/> snapshot determines
    /// whether this aborted the deployment (Required) or just flipped
    /// it to <see cref="DeploymentStatus.SucceededWithWarnings"/>
    /// (non-required).</summary>
    Failed = 1,

    /// <summary>Step did not run because its Run Condition didn't
    /// match (Success-conditioned step after a prior failure,
    /// Failure-conditioned step in a clean deployment, Variable
    /// expression evaluated falsy, etc.).</summary>
    Skipped = 2,

    /// <summary>Step was killed because it exceeded its configured
    /// <c>StepSnapshot.TimeoutSeconds</c>. Treated as a step failure
    /// subject to the Required flag; called out as a distinct outcome
    /// because operators frequently want to filter on "what timed out"
    /// without trawling failure reasons.</summary>
    TimedOut = 3,
}
