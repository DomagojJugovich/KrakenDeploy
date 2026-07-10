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

    /// <summary>Next log-sequence counter. Allocated DB-atomically by the task
    /// log writer (<c>ITaskLogWriter</c>) — never a bare read-modify-write, so
    /// parallel server-side steps and multi-target agents can't collide.</summary>
    public int NextLogSequence { get; set; }

    /// <summary>When set, the task is held <c>Queued</c> until this instant; the
    /// Hangfire scheduled-dispatch job enqueues it when due. <c>null</c> = now.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Relative path to the offline drop-bundle zip for offline-drop
    /// tasks; <c>null</c> for agent-dispatched tasks.</summary>
    public string? DropBundlePath { get; set; }

    /// <summary>When triggered by an <c>Octopus.DeployRelease</c> step in another
    /// task, this is that parent task's id (self-FK, SetNull on delete). <c>null</c>
    /// for top-level tasks. Powers the audit trail and "child tasks" surfaces.</summary>
    public Guid? ParentTaskId { get; set; }
    public ServerTask? ParentTask { get; set; }

    /// <summary>Future prompted-variable values (jsonb). Inert — written
    /// <c>null</c> today; reserved so provenance can be backfilled once
    /// prompted variables land.</summary>
    public string? FormValues { get; set; }

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
}
