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
    BackupEngine engine,
    ILogger<BackupService> logger,
    TimeProvider time)
{
    /// <summary>Loads the persisted settings or returns a default-shaped
    /// row when none exist (first-run convenience — operator sees the
    /// defaults pre-populated in the form).</summary>
    public async Task<BackupSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.BackupSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == BackupSettings.SingletonId, ct)
            .ConfigureAwait(false);
        return row ?? new BackupSettings { Id = BackupSettings.SingletonId };
    }

    /// <summary>Upsert. Validates target-directory + retention; returns the
    /// persisted row (without re-reading — saves a round trip).</summary>
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

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.BackupSettings
            .FirstOrDefaultAsync(s => s.Id == BackupSettings.SingletonId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new BackupSettings { Id = BackupSettings.SingletonId };
            db.BackupSettings.Add(existing);
        }

        existing.TargetDirectory = input.TargetDirectory.Trim();
        existing.ScheduleCron    = string.IsNullOrWhiteSpace(input.ScheduleCron)
            ? null
            : input.ScheduleCron.Trim();
        existing.ScheduleEnabled = input.ScheduleEnabled;
        existing.RetainLastN     = input.RetainLastN;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
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
