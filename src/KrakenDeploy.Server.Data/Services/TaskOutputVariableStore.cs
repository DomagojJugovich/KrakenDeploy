using KrakenDeploy.Server.Core.Domain.Deployments;
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

        var existing = await db.TaskOutputVariables
            .Where(o => o.TaskId == taskId && o.StepName == stepName)
            .ToDictionaryAsync(o => o.Name, StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        foreach (var (name, value) in outputs)
        {
            // T0-6: a sensitive output is stored encrypted (never plaintext);
            // the read path masks it. Non-sensitive values are stored as-is.
            var isSensitive = sensitiveSet.Contains(name);
            var storedValue = isSensitive ? encryption.Encrypt(value) : value;
            if (existing.TryGetValue(name, out var row))
            {
                row.Value = storedValue;
                row.IsSensitive = isSensitive;
                row.CapturedUtc = capturedUtc;
            }
            else
            {
                db.TaskOutputVariables.Add(new TaskOutputVariable
                {
                    SpaceId     = spaceId,
                    TaskId      = taskId,
                    StepName    = stepName,
                    Name        = name,
                    Value       = storedValue,
                    IsSensitive = isSensitive,
                    CapturedUtc = capturedUtc,
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
