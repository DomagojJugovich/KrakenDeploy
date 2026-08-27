using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Tenants;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// The unified execution spine — Octopus's "ServerTask" — persisted in the one
/// <c>server_tasks</c> table (table-per-hierarchy on <see cref="Kind"/>). Both
/// <see cref="Deployment"/> and <see cref="Runbooks.RunbookRun"/> derive from it,
/// so log lines, step outcomes, output variables, artifacts and target
/// assignments all FK to <c>task_id</c> regardless of kind and the
/// agent/worker/hub path is kind-agnostic (one lookup, one child schema).
/// </summary>
public abstract class ServerTask : AuditableEntity, ISpaceScoped
{
    /// <summary>Inherited from the owning release/runbook's Space; stamped on insert.</summary>
    public Guid SpaceId { get; set; }

    /// <summary>TPH discriminator. Set by the derived type's constructor and
    /// managed by EF's <c>HasDiscriminator</c> mapping.</summary>
    public ServerTaskKind Kind { get; set; }

    // ── Denormalized ownership (schema-hardening decision 5) ─────────────────

    /// <summary>Project this task belongs to — stamped at creation from the
    /// release's project (deployment) or the runbook's project (runbook run).
    /// NOT NULL. Lets dashboards / pivot / the project matrix filter without the
    /// task -> release -> project join.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Channel the task ran under. Set for deployments (from the
    /// release's channel), <c>null</c> for runbook runs (not channel-scoped).</summary>
    public Guid? ChannelId { get; set; }

    // ── Common execution state ───────────────────────────────────────────────

    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironment Environment { get; set; } = null!;

    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;

    /// <summary>How the rolling orchestrator reacts when a target fails a
    /// Required step. Defaults to <see cref="DeploymentFailureMode.BestEffort"/>.
    /// Applies to both kinds now that runbook runs share the orchestrator.</summary>
    public DeploymentFailureMode FailureMode { get; set; } = DeploymentFailureMode.BestEffort;

    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    // E-D: the log-sequence counter moved off this row into task_log_counters —
    // allocating a sequence no longer bumps this row's xmin (the B5 token). See
    // TaskLogService.AllocateSequenceRangeAsync / TaskLogCounter.

    /// <summary>When set, the task is held <c>Queued</c> until this instant; the
    /// Hangfire scheduled-dispatch job enqueues it when due. <c>null</c> = now.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    // ── Dispatch lease (B1 durable dispatch) ─────────────────────────────────

    /// <summary>Which process instance claimed this task (informational, for
    /// forensics — liveness is decided by <see cref="LeaseUntil"/>, never by
    /// matching this value). Stamped by the atomic <c>Queued→Running</c> claim;
    /// cleared on terminal states and when an offline-drop task parks at
    /// <c>PendingOfflineResult</c>.</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>Lease expiry for the claim. The owning worker renews it for the
    /// WHOLE orchestration — both kinds since the D1 engine merge; the
    /// reconciler treats a <c>Running</c> task whose lease has expired (or was
    /// never stamped) as orphaned by a dead process and fails it.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Relative path to the offline drop-bundle zip for offline-drop
    /// tasks; <c>null</c> for agent-dispatched tasks.</summary>
    public string? DropBundlePath { get; set; }

    /// <summary>When triggered by an <c>Octopus.DeployRelease</c> step in another
    /// task, this is that parent task's id (self-FK, SetNull on delete). <c>null</c>
    /// for top-level tasks. Powers the audit trail and "child tasks" surfaces.</summary>
    public Guid? ParentTaskId { get; set; }
    public ServerTask? ParentTask { get; set; }

    /// <summary>Prompted-variable values (jsonb). Sensitive values are stored only
    /// inside <see cref="PromptedVariableFormValues.SensitiveValuesEncrypted"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FormValues { get; set; }

    /// <summary>
    /// WP3 — the orchestration state a <see cref="DeploymentStatus.Paused"/> task
    /// must resume from: the wave to restart at, the failure/alive/dropped/
    /// soft-failed bookkeeping, and the accumulated output-variable bags. Written
    /// when the task pauses at a manual-intervention gate, consumed and CLEARED on
    /// resume (and on any terminal status).
    /// <para>
    /// Encrypted (AES-GCM, DEK) because the output bags carry captured SENSITIVE
    /// values — the same treatment <see cref="TaskOutputVariable"/> gives them. The
    /// <c>Encrypted</c> suffix is load-bearing: the DEK-rotation completeness test
    /// reflects over <c>*Encrypted</c> members, and <c>DekRotationWalk</c> re-encrypts
    /// this column.
    /// </para>
    /// <para>
    /// Non-null ONLY while paused. A resumed or terminal task carries <c>null</c>;
    /// a paused task carrying <c>null</c> is a violated invariant and fails the task
    /// rather than resuming with silently-empty state.
    /// </para>
    /// </summary>
    public string? PauseCheckpointEncrypted { get; set; }

    // ── Provenance (schema-hardening fix 6) ──────────────────────────────────

    /// <summary>Acting user who created the task, when attributable. Stored as a
    /// bare <c>Guid</c> (no navigation — house convention for domain→Identity refs);
    /// the FK to <c>users</c> is <c>SET NULL</c> on user delete so history outlives
    /// the user. <c>null</c> for automated causes (scheduled, subscription, …).</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Denormalized initiator label — the acting user's name, or a
    /// <c>"System (…)"</c> label for automated causes. NOT NULL; captured from claims
    /// at creation and never looked up later (there is no <c>User.DisplayName</c> to
    /// join). Survives everything, including user deletion.</summary>
    public string CreatedByDisplay { get; set; } = "";

    /// <summary>Why/how the task was created. Required — enforced by the creation
    /// guard (<see cref="TaskInitiator.EnsureValid"/>), because a non-nullable enum
    /// defaults to a real value and a DB NOT NULL alone can't catch a missing cause.</summary>
    public ServerTaskCause Cause { get; set; }

    /// <summary>Optional extra provenance (parent task id, API-key name, subscription
    /// + event ids, …). Max 256 chars.</summary>
    public string? CauseDetail { get; set; }

    // ── Children (FK task_id, CASCADE) ───────────────────────────────────────

    /// <summary>The task's target SET — the single authority for "which targets
    /// does this task hit". One row for classic single-target, N for fan-out.</summary>
    public ICollection<TaskTargetAssignment> Targets { get; set; } = [];

    /// <summary>Files collected from the agent at the end of each step.</summary>
    public ICollection<TaskArtifact> Artifacts { get; set; } = [];

    /// <summary>Output variables captured via <c>Set-OctopusVariable</c> markers.</summary>
    public ICollection<TaskOutputVariable> OutputVariables { get; set; } = [];

    /// <summary>Terminal per-step outcomes surfaced on the detail Steps tab.</summary>
    public ICollection<TaskStepOutcome> StepOutcomes { get; set; } = [];

    /// <summary>WP3 — manual-intervention gates this task hit, at most one per
    /// gating step. The change-control trail for who approved or rejected.</summary>
    public ICollection<Interruption> Interruptions { get; set; } = [];
}
