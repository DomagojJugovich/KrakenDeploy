using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// The compacted output of one completed step (×target) — the FINAL half of the
/// hybrid log model (<c>task_step_logs</c>). One row per (task, step, target); the
/// step's live lines are serialized into <see cref="Content"/> as
/// <c>seq|iso8601-ts|level|message</c> per line, which TOAST/lz4 compresses
/// transparently while keeping <c>ILIKE</c> working. Postgres cannot append to a
/// TOASTed value, so live streaming stays in <see cref="TaskLogLiveEntry"/> and
/// only completed steps land here.
///
/// <para>
/// Not <see cref="ISpaceScoped"/>: scope inherits through <see cref="TaskId"/>.
/// Summary columns support the task detail Steps tab / list surfaces without
/// re-parsing the blob. NO trgm/GIN index over <see cref="Content"/> — global
/// text search is the out-of-band Seq pipeline.
/// </para>
/// </summary>
public sealed class TaskStepLog : Entity
{
    public Guid TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    /// <summary>Plan-level step index (matches <see cref="TaskStepOutcome.StepIndex"/>).</summary>
    public int StepIndex { get; set; }

    /// <summary>The target this step ran on, or <c>null</c> for server-side steps.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>The step's lines, one per physical line as
    /// <c>sequence|iso8601-timestamp|level|message</c> (message newline-escaped).</summary>
    public string Content { get; set; } = "";

    /// <summary>Number of lines serialized into <see cref="Content"/>.</summary>
    public int LineCount { get; set; }

    /// <summary>Count of <c>error</c>-level lines (drives Steps-tab badges without a re-parse).</summary>
    public int ErrorCount { get; set; }

    /// <summary>Count of <c>warning</c>-level lines.</summary>
    public int WarnCount { get; set; }

    /// <summary>Sequence of the first <c>error</c> line, or <c>null</c> when none —
    /// lets the UI jump straight to the first failure.</summary>
    public int? FirstErrorLine { get; set; }

    /// <summary>Byte size of <see cref="Content"/> (pre-compression) for quick sizing.</summary>
    public long ByteSize { get; set; }

    /// <summary>When the compaction of this step's lines completed.</summary>
    public DateTimeOffset CompletedUtc { get; set; }
}
