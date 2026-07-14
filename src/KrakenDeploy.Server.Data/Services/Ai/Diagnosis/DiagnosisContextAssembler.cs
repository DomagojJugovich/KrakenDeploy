using System.Globalization;
using System.Text;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using KrakenDeploy.Server.Data.Services.Ai.Curators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai.Diagnosis;

/// <summary>
/// M11.C — assembles the diagnosis prompt for a failed deployment from the
/// shared M11.B context builders. Tail-of-failure focus: the deployment
/// summary, the failed step(s) + their curated config, the log tail, the
/// diff vs the last green run, and the target health. Also gathers the
/// deployment's decrypted Sensitive variable values so
/// <c>IPromptSanitizer</c> can redact any that leaked into the log before
/// the prompt crosses to the provider.
/// </summary>
public sealed class DiagnosisContextAssembler(
    IDbContextFactory<KrakenDbContext> dbFactory,
    DeploymentContextBuilder deploymentContext,
    DeploymentDiffBuilder diffBuilder,
    TargetHealthBuilder targetHealth,
    StepConfigCuratorRegistry curators,
    IEncryptionService encryption,
    ILogger<DiagnosisContextAssembler> logger)
{
    private const int LogTailLines = 200;

    /// <summary>The assembled prompt body + the sensitive values to redact.
    /// Null when the deployment id is unknown.</summary>
    public sealed record AssembledContext(
        string PromptBody,
        IReadOnlyDictionary<string, string> SensitiveValues);

    public async Task<AssembledContext?> AssembleAsync(Guid deploymentId, CancellationToken ct = default)
    {
        var logTail = await deploymentContext.GetLogTailAsync(deploymentId, LogTailLines, ct)
            .ConfigureAwait(false);
        if (logTail is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var release = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == deploymentId)
            .Select(d => d.Release)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var failedOutcomes = await db.TaskStepOutcomes.AsNoTracking()
            .Where(o => o.TaskId == deploymentId
                     && (o.Outcome == StepOutcomeKind.Failed || o.Outcome == StepOutcomeKind.TimedOut))
            .OrderBy(o => o.StepIndex)
            .ToListAsync(ct).ConfigureAwait(false);

        var diff = await diffBuilder.BuildAsync(deploymentId, ct).ConfigureAwait(false);

        // Target health for each target the deployment ran against.
        var targetNames = logTail.Deployment.TargetNames;
        var healthSnapshots = new List<ContextBuilders.TargetHealthDto>();
        foreach (var name in targetNames)
        {
            var h = await targetHealth.GetByNameAsync(name, ct).ConfigureAwait(false);
            if (h is not null)
            {
                healthSnapshots.Add(h);
            }
        }

        var sb = new StringBuilder();
        var d = logTail.Deployment;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Deployment {d.Id} of project '{d.ProjectName}' release '{d.ReleaseVersion}' " +
            $"to environment '{d.EnvironmentName}' (targets: {string.Join(", ", d.TargetNames)}) " +
            $"ended with status {d.Status}.");
        sb.AppendLine();

        // ── Failed steps + their curated config ────────────────────────
        if (failedOutcomes.Count > 0 && release is not null)
        {
            var snapshotByIndex = release.ProcessSnapshot.OrderBy(s => s.SortOrder).ToList();
            sb.AppendLine("Failed steps:");
            foreach (var o in failedOutcomes)
            {
                var errorSuffix = string.IsNullOrEmpty(o.ErrorMessage) ? "" : $": {o.ErrorMessage}";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- [{o.StepIndex}] '{o.StepName}' → {o.Outcome}{errorSuffix}");
                if (o.StepIndex >= 0 && o.StepIndex < snapshotByIndex.Count)
                {
                    var snap = snapshotByIndex[o.StepIndex];
                    var summary = curators.Curate(snap.StepType, snap.Config);
                    if (summary.Count > 0)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture,
                            $"    config: {string.Join(", ", summary.Select(kv => $"{kv.Key}={kv.Value}"))}");
                    }
                }
            }
            sb.AppendLine();
        }

        // ── Diff vs last green ─────────────────────────────────────────
        if (diff is { HasBaseline: true })
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Changes since the last successful run (release {diff.FromReleaseVersion} → {diff.ToReleaseVersion}):");
            foreach (var p in diff.PackageChanges)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- package on step '{p.StepName}': {p.FromVersion ?? "(none)"} → {p.ToVersion ?? "(removed)"}");
            }
            if (diff.VariableChanges.Added.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- variables added: {string.Join(", ", diff.VariableChanges.Added)}");
            }
            if (diff.VariableChanges.Removed.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- variables removed: {string.Join(", ", diff.VariableChanges.Removed)}");
            }
            if (diff.VariableChanges.Changed.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- variables changed: {string.Join(", ", diff.VariableChanges.Changed)}");
            }
            if (diff.TargetsAdded.Count > 0 || diff.TargetsRemoved.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- targets added: [{string.Join(", ", diff.TargetsAdded)}], " +
                    $"removed: [{string.Join(", ", diff.TargetsRemoved)}]");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("No prior successful run to diff against (first deployment to this environment).");
            sb.AppendLine();
        }

        // ── Target health ──────────────────────────────────────────────
        if (healthSnapshots.Count > 0)
        {
            sb.AppendLine("Target health:");
            foreach (var h in healthSnapshots)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- '{h.Name}': status={h.Status}, lastSeen={h.LastSeenUtc:O}, " +
                    $"os={h.OperatingSystem ?? "?"}, agent={h.AgentVersion ?? "?"}, " +
                    $"lastDeploy={h.LastDeploymentStatus ?? "?"}");
            }
            sb.AppendLine();
        }

        // ── Log tail ────────────────────────────────────────────────────
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Log tail (last {logTail.Tail.Count} of {logTail.TotalLogLines} lines; each prefixed with its sequence number):");
        foreach (var line in logTail.Tail)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"[{line.Sequence}] {line.Level}: {line.Message}");
        }

        var sensitive = release is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : DecryptSensitiveValues(release.VariableSnapshot);

        // Also feed sensitive OUTPUT variable values (T0-6) to the sanitizer, so
        // the diagnosis prompt stays in sync with the same sensitivity source the
        // log-redactor uses. A value could reach the prompt via curated config or
        // a pre-redaction log line even though live logs are masked at write time.
        await AddSensitiveOutputValuesAsync(db, deploymentId, sensitive, ct).ConfigureAwait(false);

        return new AssembledContext(sb.ToString(), sensitive);
    }

    /// <summary>
    /// Decrypts the deployment's Sensitive snapshot variables into a
    /// name→plaintext map so the sanitizer can redact any that appear in
    /// the prompt (e.g. a secret echoed into the log). Best-effort:
    /// a decrypt failure (e.g. post key-rotation) skips that variable
    /// rather than failing the whole diagnosis.
    /// </summary>
    private Dictionary<string, string> DecryptSensitiveValues(
        IReadOnlyList<Core.Domain.Releases.VariableSnapshot> snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in snapshot)
        {
            if (v.Type != VariableType.Sensitive || string.IsNullOrEmpty(v.Value))
            {
                continue;
            }
            try
            {
                var plain = encryption.Decrypt(v.Value);
                if (!string.IsNullOrEmpty(plain))
                {
                    map[v.Name] = plain;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Diagnosis sanitisation: could not decrypt sensitive variable '{Name}' " +
                    "(key rotated?). It will NOT be redacted from the prompt — skipping.", v.Name);
            }
        }
        return map;
    }

    /// <summary>
    /// Merges the deployment's sensitive OUTPUT variables (encrypted at rest,
    /// <see cref="TaskOutputVariable.IsSensitive"/>) into the redaction map,
    /// decrypting best-effort. Keyed by <c>name@step</c> so a value never
    /// collides with — and evicts — a same-named snapshot variable; the
    /// sanitizer matches on the value, so the label just needs to be unique.
    /// </summary>
    private async Task AddSensitiveOutputValuesAsync(
        KrakenDbContext db, Guid deploymentId,
        Dictionary<string, string> map, CancellationToken ct)
    {
        var rows = await db.TaskOutputVariables.AsNoTracking()
            .Where(o => o.TaskId == deploymentId && o.IsSensitive && o.Value != "")
            .Select(o => new { o.StepName, o.Name, o.Value })
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var o in rows)
        {
            try
            {
                var plain = encryption.Decrypt(o.Value);
                if (!string.IsNullOrEmpty(plain))
                {
                    map[$"{o.Name}@{o.StepName}"] = plain;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Diagnosis sanitisation: could not decrypt sensitive output variable " +
                    "'{Name}' of step '{Step}' (key rotated?). It will NOT be redacted — skipping.",
                    o.Name, o.StepName);
            }
        }
    }
}
