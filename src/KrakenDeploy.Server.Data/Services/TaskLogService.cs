using System.Globalization;
using System.Text;
using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// One line of task output, projected uniformly from either the compacted
/// <see cref="TaskStepLog"/> blobs or the live <see cref="TaskLogLiveEntry"/> staging.
/// </summary>
public sealed record TaskLogLine(
    int Sequence,
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    int StepIndex,
    Guid? TargetId);

/// <summary>
/// The write + read spine for the hybrid task-log model (staging
/// <c>task_log_live</c> -> blob <c>task_step_logs</c>). All log writers
/// (<c>AgentHub</c>, <c>ServerScriptStepRunner</c>, <c>DeploymentWorker</c>,
/// <c>DeployReleaseStepRunner</c>, offline import) route their sequence allocation
/// through <see cref="AllocateSequenceAsync"/> / <see cref="AppendLiveAsync"/> so
/// parallel server-side steps and multi-target agents never collide on a sequence
/// (the pre-unification bug: unguarded <c>NextLogSequence++</c>). The counter now
/// lives in its own <c>task_log_counters</c> row (E-D) so appends don't churn the
/// task row's <c>xmin</c>.
///
/// <para>
/// Callers own the <see cref="KrakenDbContext"/> (account-routed) and any UI push;
/// this service is DB-only so it can live in Server.Data and be reused by both the
/// transport hub and the offline importer.
/// </para>
/// </summary>
public static class TaskLogService
{
    // ── Sequence allocation (DB-atomic) ──────────────────────────────────────

    /// <summary>Atomically allocate the next log sequence for a task and return
    /// the pre-increment value. Serialized at the row level by Postgres, so
    /// concurrent callers get distinct sequences.</summary>
    public static Task<int> AllocateSequenceAsync(
        KrakenDbContext db, Guid taskId, CancellationToken ct = default)
        => AllocateSequenceRangeAsync(db, taskId, 1, ct);

    /// <summary>Atomically reserve <paramref name="count"/> sequences for a task
    /// and return the FIRST of the reserved range [base, base+count-1]. Used by the
    /// offline importer to bulk-insert log lines without N round-trips.
    /// <para>
    /// E-D — the counter lives in its own one-row-per-task <c>task_log_counters</c>
    /// table, NOT on <c>server_tasks</c>: allocating a sequence no longer bumps the
    /// task row's <c>xmin</c> (the B5 concurrency token), so a log-heavy run no
    /// longer forces <c>ServerTaskStatusWriter</c> retries. The row is created
    /// lazily on first allocation via <c>INSERT … ON CONFLICT (task_id) DO UPDATE</c>;
    /// the upsert takes a row-level lock, so concurrent allocators still get
    /// distinct sequences (the DB-atomic guarantee <c>AgentHub</c>, <c>LogSequencer</c>
    /// and the offline import rely on is preserved).
    /// </para></summary>
    public static async Task<int> AllocateSequenceRangeAsync(
        KrakenDbContext db, Guid taskId, int count, CancellationToken ct = default)
    {
        if (count <= 0)
        {
            return 0;
        }

        // First allocation for a task inserts (next_sequence = count → base 0);
        // every later one conflicts and adds count under the row lock. RETURNING
        // sees the post-write value, so `next_sequence - count` is the pre-write
        // base in both branches. EF scalar SqlQuery requires a "Value" column.
        var rows = await db.Database
            .SqlQuery<int>(
                $@"INSERT INTO task_log_counters (task_id, next_sequence) VALUES ({taskId}, {count}) ON CONFLICT (task_id) DO UPDATE SET next_sequence = task_log_counters.next_sequence + {count} RETURNING next_sequence - {count} AS ""Value""")
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : 0;
    }

    // ── Live write ───────────────────────────────────────────────────────────

    /// <summary>Allocate a sequence, insert a live staging line, and return the
    /// assigned sequence (for the caller's UI push). Saves changes.</summary>
    public static async Task<int> AppendLiveAsync(
        KrakenDbContext db,
        Guid taskId,
        int stepIndex,
        Guid? targetId,
        string level,
        string message,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        var seq = await AllocateSequenceAsync(db, taskId, ct).ConfigureAwait(false);
        db.TaskLogLive.Add(new TaskLogLiveEntry
        {
            TaskId = taskId,
            StepIndex = stepIndex,
            TargetId = targetId,
            Sequence = seq,
            Level = level,
            Timestamp = timestamp,
            Message = message,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return seq;
    }

    // ── Compaction (staging -> blob) ─────────────────────────────────────────

    /// <summary>Compact one completed step's (×target) staging lines into a single
    /// <see cref="TaskStepLog"/> blob and delete them from staging. No-op when the
    /// step produced no lines.</summary>
    public static async Task CompactStepAsync(
        KrakenDbContext db,
        Guid taskId,
        int stepIndex,
        Guid? targetId,
        DateTimeOffset completedUtc,
        CancellationToken ct = default)
    {
        var lines = await db.TaskLogLive
            .Where(l => l.TaskId == taskId && l.StepIndex == stepIndex && l.TargetId == targetId)
            .OrderBy(l => l.Sequence)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (lines.Count == 0)
        {
            return;
        }

        await WriteBlobAndDeleteAsync(db, taskId, stepIndex, targetId, lines, completedUtc, ct)
            .ConfigureAwait(false);
    }

    /// <summary>At terminal task status, sweep any remaining staging lines into
    /// blobs, grouped by (step, target). Covers server-once banners (step -1),
    /// steps that never fired a completion boundary, and offline imports.</summary>
    public static async Task CompactRemainingAsync(
        KrakenDbContext db, Guid taskId, DateTimeOffset completedUtc, CancellationToken ct = default)
    {
        var remaining = await db.TaskLogLive
            .Where(l => l.TaskId == taskId)
            .OrderBy(l => l.Sequence)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (remaining.Count == 0)
        {
            return;
        }

        foreach (var group in remaining.GroupBy(l => (l.StepIndex, l.TargetId)))
        {
            await WriteBlobAndDeleteAsync(
                db, taskId, group.Key.StepIndex, group.Key.TargetId,
                [.. group.OrderBy(l => l.Sequence)], completedUtc, ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteBlobAndDeleteAsync(
        KrakenDbContext db,
        Guid taskId,
        int stepIndex,
        Guid? targetId,
        List<TaskLogLiveEntry> lines,
        DateTimeOffset completedUtc,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var errorCount = 0;
        var warnCount = 0;
        int? firstErrorLine = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (i > 0)
            {
                sb.Append('\n');
            }
            sb.Append(l.Sequence.ToString(CultureInfo.InvariantCulture))
              .Append('|')
              .Append(l.Timestamp.ToString("O", CultureInfo.InvariantCulture))
              .Append('|')
              .Append(l.Level)
              .Append('|')
              .Append(EscapeMessage(l.Message));

            if (IsError(l.Level))
            {
                errorCount++;
                firstErrorLine ??= l.Sequence;
            }
            else if (IsWarning(l.Level))
            {
                warnCount++;
            }
        }

        var content = sb.ToString();
        db.TaskStepLogs.Add(new TaskStepLog
        {
            TaskId = taskId,
            StepIndex = stepIndex,
            TargetId = targetId,
            Content = content,
            LineCount = lines.Count,
            ErrorCount = errorCount,
            WarnCount = warnCount,
            FirstErrorLine = firstErrorLine,
            ByteSize = Encoding.UTF8.GetByteCount(content),
            CompletedUtc = completedUtc,
        });
        db.TaskLogLive.RemoveRange(lines);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Read (stitch blobs + staging) ────────────────────────────────────────

    /// <summary>All of a task's log lines in sequence order — completed step blobs
    /// stitched with any not-yet-compacted staging. Caller must have resolved the
    /// task under the Space filter first (these tables are not ISpaceScoped).</summary>
    public static Task<List<TaskLogLine>> ReadAllAsync(
        KrakenDbContext db, Guid taskId, CancellationToken ct = default)
        => ReadSinceAsync(db, taskId, afterSequence: -1, ct);

    /// <summary>Log lines with sequence &gt; <paramref name="afterSequence"/>, in
    /// sequence order (powers the child-log mirror and poll-by-sequence tails).</summary>
    public static async Task<List<TaskLogLine>> ReadSinceAsync(
        KrakenDbContext db, Guid taskId, int afterSequence, CancellationToken ct = default)
    {
        var blobs = await db.TaskStepLogs
            .Where(b => b.TaskId == taskId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stagingQuery = db.TaskLogLive.Where(l => l.TaskId == taskId);
        if (afterSequence >= 0)
        {
            stagingQuery = stagingQuery.Where(l => l.Sequence > afterSequence);
        }
        var staging = await stagingQuery.ToListAsync(ct).ConfigureAwait(false);

        var lines = new List<TaskLogLine>(staging.Count + blobs.Sum(b => b.LineCount));
        foreach (var blob in blobs)
        {
            lines.AddRange(ParseBlob(blob).Where(l => l.Sequence > afterSequence));
        }
        foreach (var l in staging)
        {
            lines.Add(new TaskLogLine(l.Sequence, l.Timestamp, l.Level, l.Message, l.StepIndex, l.TargetId));
        }

        lines.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return lines;
    }

    /// <summary>Parse a compacted blob back into its lines.</summary>
    public static IEnumerable<TaskLogLine> ParseBlob(TaskStepLog blob)
    {
        if (string.IsNullOrEmpty(blob.Content))
        {
            yield break;
        }

        foreach (var raw in blob.Content.Split('\n'))
        {
            // seq|iso8601|level|escaped-message  (message may itself contain '|')
            var parts = raw.Split('|', 4);
            if (parts.Length < 4)
            {
                continue;
            }
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
            {
                continue;
            }
            _ = DateTimeOffset.TryParse(
                parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts);
            yield return new TaskLogLine(
                seq, ts, parts[2], UnescapeMessage(parts[3]), blob.StepIndex, blob.TargetId);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsError(string level) =>
        level.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static bool IsWarning(string level) =>
        level.Equals("warning", StringComparison.OrdinalIgnoreCase)
        || level.Equals("warn", StringComparison.OrdinalIgnoreCase);

    private static string EscapeMessage(string s) => s
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string UnescapeMessage(string s)
    {
        if (!s.Contains('\\', StringComparison.Ordinal))
        {
            return s;
        }
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n switch { 'n' => '\n', 'r' => '\r', '\\' => '\\', _ => n });
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }
}
