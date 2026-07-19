namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// One row per <see cref="ServerTask"/> holding the next log-sequence counter for
/// that task (<c>task_log_counters</c>). Split out from <see cref="ServerTask"/>
/// (E-D): the sequence allocator bumps this counter on every log append, and while
/// it lived on the <c>server_tasks</c> row every append churned that row's
/// <c>xmin</c> — the B5 optimistic-concurrency token — forcing
/// <c>ServerTaskStatusWriter</c> to burn retries under log load. Keeping the
/// counter on its own row means <c>server_tasks.xmin</c> changes only on real
/// state writes.
///
/// <para>
/// The row is created lazily by the first allocation (upsert) and cascades away
/// with its task. Not <see cref="Common.ISpaceScoped"/>: scope inherits through
/// <see cref="TaskId"/>, exactly like <see cref="TaskLogLiveEntry"/> /
/// <see cref="TaskStepLog"/>.
/// </para>
/// </summary>
public sealed class TaskLogCounter
{
    public Guid TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    /// <summary>The next sequence to hand out. Allocated DB-atomically by an
    /// upsert with <c>RETURNING</c> (see <c>TaskLogService.AllocateSequenceRangeAsync</c>)
    /// so concurrent allocators always get distinct sequences.</summary>
    public int NextSequence { get; set; }
}
