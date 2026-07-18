using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// B4 — the single upsert path for captured output variables, shared by the
/// two capture sources: <c>AgentHub.ReportStepCompletedAsync</c> (agent-side
/// steps) and <c>DeploymentWorker</c>'s server-wave fold
/// (<c>ServerScriptStepRunner</c> captures, T1-6). Rows are keyed
/// (task, stepName, name); a re-run/retry overwrites in place.
/// T0-6: a sensitive value is stored ENCRYPTED (never plaintext); the read
/// path masks it.
/// </summary>
public static class TaskOutputVariableStore
{
    /// <summary>Upserts one step's captured outputs. Caller SaveChanges-es via
    /// this method (single unit); no-op when <paramref name="outputs"/> is empty.</summary>
    public static async Task UpsertAsync(
        KrakenDbContext db,
        Guid taskId,
        Guid spaceId,
        string stepName,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyCollection<string>? sensitiveNames,
        DateTimeOffset capturedUtc,
        IEncryptionService encryption,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(encryption);

        if (outputs.Count == 0)
        {
            return;
        }

        var sensitiveSet = new HashSet<string>(
            sensitiveNames ?? [], StringComparer.OrdinalIgnoreCase);

        // Race-free upsert. The read-then-insert this replaced let two concurrent
        // callers for the same (taskId, stepName, name) — an at-least-once
        // duplicate step report racing the original, or two parallel-wave targets
        // sharing a step name — both miss the read and both INSERT, violating
        // ix_task_output_variables_task_id_step_name_name and throwing
        // DbUpdateException out of AgentHub.ReportStepCompletedAsync. PostgreSQL
        // INSERT ... ON CONFLICT DO UPDATE makes each write atomic. T0-6: a
        // sensitive value is encrypted BEFORE binding, so plaintext never reaches
        // the DB (nor the parameterised SQL log). space_id is not updated on
        // conflict — a given (task, step, name) belongs to exactly one Space.
        foreach (var (name, value) in outputs)
        {
            var isSensitive = sensitiveSet.Contains(name);
            var storedValue = isSensitive ? encryption.Encrypt(value) : value;

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO task_output_variables
                    (id, space_id, task_id, step_name, name, value, is_sensitive, captured_utc)
                VALUES
                    ({Guid.CreateVersion7()}, {spaceId}, {taskId}, {stepName}, {name},
                     {storedValue}, {isSensitive}, {capturedUtc})
                ON CONFLICT (task_id, step_name, name) DO UPDATE SET
                    value        = EXCLUDED.value,
                    is_sensitive = EXCLUDED.is_sensitive,
                    captured_utc = EXCLUDED.captured_utc
                """, ct).ConfigureAwait(false);
        }

        // The rows are already persisted by the statements above. This flush
        // preserves the documented "caller SaveChanges-es via this method (single
        // unit)" contract — it commits any co-pending tracked changes on the
        // caller's context (a no-op when there are none), so callers' flush timing
        // is unchanged by the switch to raw upserts.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
