using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// One line of live output from a running task — the STAGING half of the hybrid
/// log model (<c>task_log_live</c>). The streaming write paths and the live-tail
/// UI read/write ONLY this table while a task runs. On step completion the
/// compactor moves a step's lines into a single <see cref="TaskStepLog"/> blob and
/// deletes them here; at terminal status any remainder is swept.
///
/// <para>
/// Not <see cref="ISpaceScoped"/>: scope inherits through <see cref="TaskId"/>
/// (every read resolves the parent task under the Space filter first). This keeps
/// the highest-volume table free of a redundant <c>space_id</c> column/index.
/// Logged (not UNLOGGED) — a crash must not lose in-flight logs.
/// </para>
/// </summary>
public sealed class TaskLogLiveEntry : Entity
{
    public Guid TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    /// <summary>Plan-level step index the line belongs to (the compactor groups
    /// by (StepIndex, TargetId)). -1 for lines emitted outside any step
    /// (orchestrator freeze/cancel/error banners).</summary>
    public int StepIndex { get; set; }

    /// <summary>The target that produced the line, or <c>null</c> for
    /// server-side / orchestrator lines not bound to a target.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>Monotonically increasing per-task sequence number (DB-atomic).</summary>
    public int Sequence { get; set; }

    /// <summary>"info" | "warning" | "error" — used for UI colouring.</summary>
    public string Level { get; set; } = "info";

    public DateTimeOffset Timestamp { get; set; }

    public required string Message { get; set; }
}
