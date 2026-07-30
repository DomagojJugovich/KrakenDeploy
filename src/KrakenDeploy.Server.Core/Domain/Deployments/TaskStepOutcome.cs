using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// Terminal outcome of a single step within a <see cref="ServerTask"/> (formerly
/// <c>DeploymentStepOutcome</c>). One row per <see cref="StepIndex"/> per
/// <see cref="TargetId"/> per task, written by the orchestrator (server-side steps)
/// or upserted from the agent's per-step boundary callback (target-side steps via
/// <c>AgentHub.ReportStepCompletedAsync</c>). Now shared by deployments and runbook
/// runs alike.
///
/// <para>
/// <strong>Upsert key</strong> is (<see cref="TaskId"/>, <see cref="StepIndex"/>,
/// <see cref="TargetId"/>). Postgres treats NULL <see cref="TargetId"/> as distinct
/// by default, so server-once steps (no bound target) key on NULL.
/// </para>
/// </summary>
public class TaskStepOutcome : Entity, ISpaceScoped
{
    /// <summary>Inherited from the parent task; set explicitly at each write
    /// site (agent/transport path has no real Space context).</summary>
    public Guid SpaceId { get; set; }

    public Guid TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    /// <summary>Plan-level <c>DeploymentStepPlan.Index</c> — stable across the
    /// task so callers can join against the frozen step snapshot. Forms the
    /// natural upsert key with <see cref="TaskId"/> and <see cref="TargetId"/>.</summary>
    public int StepIndex { get; set; }

    /// <summary>Step name at execution time (preserved so the Steps tab renders
    /// historical runs without re-resolving the snapshot).</summary>
    public required string StepName { get; set; }

    /// <summary>Terminal outcome of the step (after any retries).</summary>
    public StepOutcomeKind Outcome { get; set; }

    /// <summary>Total attempts including the final one. 1 = no retries fired.</summary>
    public int AttemptCount { get; set; } = 1;

    /// <summary>Final-attempt error message; null when Succeeded or Skipped.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>When the step's first attempt started. Null for Skipped.</summary>
    public DateTimeOffset? StartedUtc { get; set; }

    /// <summary>When the step's terminal outcome was recorded.</summary>
    public DateTimeOffset CompletedUtc { get; set; }

    /// <summary>True for server-side (orchestrator-executed) steps, false for
    /// target-side (agent-executed).</summary>
    public bool IsServerSide { get; set; }

    /// <summary>Snapshot of <c>StepSnapshot.Required</c> at execution time.</summary>
    public bool Required { get; set; }

    /// <summary>The target this outcome belongs to. NULL for server-once steps
    /// not bound to a specific target, or offline/drop tasks without a target.</summary>
    public Guid? TargetId { get; set; }
}

/// <summary>
/// Terminal outcome categories surfaced on the task detail Steps tab. Stored as
/// ints so adding a variant (e.g. ManualInterventionApproved) is additive.
/// </summary>
public enum StepOutcomeKind
{
    /// <summary>Step ran to completion successfully (after any retries).</summary>
    Succeeded = 0,

    /// <summary>Step ran but its final attempt failed. The <see cref="TaskStepOutcome.Required"/>
    /// snapshot determines whether this aborted the task or flipped it to
    /// <see cref="DeploymentStatus.SucceededWithWarnings"/>.</summary>
    Failed = 1,

    /// <summary>Step did not run because its Run Condition didn't match.</summary>
    Skipped = 2,

    /// <summary>Step was killed for exceeding its configured timeout.</summary>
    TimedOut = 3,

    /// <summary>WP3 — a manual-intervention gate an authorized user APPROVED; the
    /// task resumed from the checkpoint and continued with the next wave.</summary>
    ManualInterventionApproved = 4,

    /// <summary>WP3 — a manual-intervention gate an authorized user REJECTED. The
    /// task fails, but cleanly: <c>Condition=Failure</c>/<c>Always</c> cleanup steps
    /// in later waves still run per the task's <see cref="DeploymentFailureMode"/>.</summary>
    ManualInterventionRejected = 5,

    /// <summary>WP3 — a manual-intervention gate nobody answered before its
    /// auto-fail timeout elapsed. Treated exactly like
    /// <see cref="ManualInterventionRejected"/>, with an audit entry noting the
    /// timeout rather than an acting user.</summary>
    ManualInterventionTimedOut = 6,
}
