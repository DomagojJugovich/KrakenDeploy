using System.Globalization;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Operator-facing surface on top of <see cref="BackupEngine"/> for M13.G:
/// reads + writes <see cref="BackupSettings"/>, persists each run into the
/// <c>backup_runs</c> table, and exposes "trigger now" + "history" queries
/// the Razor page binds to. The Hangfire scheduled job also goes through
/// here so manual + scheduled runs share the audit + history trail.
/// </summary>
public sealed class BackupService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    SettingsService settings,
    BackupEngine engine,
    ILogger<BackupService> logger,
    TimeProvider time,
    IAuditLog audit)
{
    /// <summary>Loads the persisted settings or returns a default-shaped
    /// document when none exist (first-run convenience — operator sees the
    /// defaults pre-populated in the form).</summary>
    public Task<BackupSettings> GetSettingsAsync(CancellationToken ct = default)
        => settings.GetAsync<BackupSettings>(ct: ct);

    /// <summary>Upsert. Validates target-directory + retention; returns the
    /// persisted document.</summary>
    public async Task<BackupSettings> UpsertSettingsAsync(
        BackupSettings input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.TargetDirectory))
        {
            throw new ArgumentException(
                "Target directory is required.", nameof(input));
        }
        if (input.RetainLastN < 0)
        {
            throw new ArgumentException(
                "RetainLastN must be 0 (keep all) or positive.", nameof(input));
        }

        return await settings.MutateAsync<BackupSettings>(scopeId: null, existing =>
        {
            existing.TargetDirectory = input.TargetDirectory.Trim();
            existing.ScheduleCron    = string.IsNullOrWhiteSpace(input.ScheduleCron)
                ? null
                : input.ScheduleCron.Trim();
            existing.ScheduleEnabled = input.ScheduleEnabled;
            existing.RetainLastN     = input.RetainLastN;
            return existing;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one backup using the persisted settings + writes a
    /// <see cref="BackupRun"/> row regardless of outcome. Returns the
    /// final run record so the UI can render the result inline.
    /// </summary>
    public async Task<BackupRun> RunOnceAsync(
        string triggeredBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggeredBy);

        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);

        // Persist the run as "in flight" first so an operator visiting the
        // history page mid-run sees an "in-progress" row instead of nothing.
        var run = new BackupRun
        {
            StartedUtc  = time.GetUtcNow(),
            TriggeredBy = triggeredBy,
            Outcome     = BackupOutcome.Failed, // pessimistic default; flipped on success
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Execute. BackupEngine.RunAsync never throws — it captures the
        // exception into the result so the row write below stays clean.
        var result = await engine.RunAsync(settings.TargetDirectory, ct).ConfigureAwait(false);

        // Finalise the row.
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var tracked = await db.BackupRuns.FirstAsync(r => r.Id == run.Id, ct).ConfigureAwait(false);
            tracked.CompletedUtc    = time.GetUtcNow();
            tracked.Duration        = result.Elapsed;
            tracked.Outcome         = result.Succeeded ? BackupOutcome.Success : BackupOutcome.Failed;
            tracked.BundlePath      = result.BundlePath;
            tracked.BundleSizeBytes = result.BundleBytes;
            tracked.ErrorMessage    = result.Error;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            run = tracked;
        }

        // Audit here (the SHARED path) so BOTH the UI "Run now" and the
        // Hangfire scheduled job record a Backup.Completed/Failed event —
        // previously only the Razor handler audited, so scheduled runs left
        // no audit trail.
        if (run.Outcome == BackupOutcome.Success)
        {
            await audit.RecordAsync(
                AuditEventType.BackupCompleted,
                subjectType: "BackupRun",
                subjectId:   run.Id.ToString(),
                details: string.Format(CultureInfo.InvariantCulture,
                    "Bundle={0}, Size={1} B, Duration={2:F0} ms, TriggeredBy={3}",
                    run.BundlePath, run.BundleSizeBytes,
                    run.Duration?.TotalMilliseconds ?? 0, run.TriggeredBy),
                ct: ct).ConfigureAwait(false);
        }
        else
        {
            await audit.RecordAsync(
                AuditEventType.BackupFailed,
                subjectType: "BackupRun",
                subjectId:   run.Id.ToString(),
                details: string.Format(CultureInfo.InvariantCulture,
                    "TriggeredBy={0}, Error={1}", run.TriggeredBy, run.ErrorMessage),
                ct: ct).ConfigureAwait(false);
        }

        // Prune AFTER a successful run, never on failure (operator might
        // need the partial state for diagnosis). Wrapped in try/catch so a
        // prune failure doesn't mask the backup success.
        if (result.Succeeded && settings.RetainLastN > 0)
        {
            try
            {
                var deleted = engine.PruneOldBundles(settings.TargetDirectory, settings.RetainLastN);
                if (deleted > 0)
                {
                    logger.LogInformation(
                        "Pruned {Count} old bundle(s) under {Target}",
                        deleted, settings.TargetDirectory);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Backup succeeded but pruning under {Target} failed.",
                    settings.TargetDirectory);
            }
        }

        return run;
    }

    /// <summary>Last N completed-or-in-flight runs, newest first.</summary>
    public async Task<List<BackupRun>> GetRecentRunsAsync(
        int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.BackupRuns
            .OrderByDescending(r => r.StartedUtc)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Most recent successful run, or null if none. The page uses
    /// this for the "last successful backup: ..." prominent label.</summary>
    public async Task<BackupRun?> GetLastSuccessfulRunAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.BackupRuns
            .Where(r => r.Outcome == BackupOutcome.Success)
            .OrderByDescending(r => r.StartedUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
