using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Server-startup seeder for built-in step packages (Phase D-8). Idempotent
/// on every startup, only inserts packages that aren't already installed.
/// <para>
/// Source: a directory of <c>.kdeploy-step</c> archives shipped alongside
/// the server binary. The default location is <c>{contentRoot}/seed/step-packages/</c>;
/// override with <c>StepPackages:SeedDirectory</c> in <c>appsettings.json</c>.
/// Each archive is installed via <see cref="StepPackageService.UploadAsync"/>
/// with <see cref="StepPackageSource.Preinstalled"/> so the catalog UI can
/// distinguish them from manual uploads / GitHub-catalog pulls.
/// </para>
/// </summary>
public sealed class BuiltInStepPackageSeeder(
    IDbContextFactory<KrakenDbContext> dbFactory,
    StepPackageService uploadService,
    IConfiguration config,
    ILogger<BuiltInStepPackageSeeder> logger)
{
    /// <summary>
    /// Scans the seed directory, installs every package that isn't already
    /// present at its exact <c>(name, version)</c> tuple. Logs each install
    /// at <see cref="LogLevel.Information"/>. Failures on individual archives
    /// are logged at <see cref="LogLevel.Error"/> and do NOT abort the seed
    /// pass — one broken archive shouldn't keep the others out of the catalog.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        // B8: anchor the default to the APPLICATION directory, not the process
        // CWD — under `dotnet run` the CWD is the project dir while the build
        // copies the archives next to the binaries, so the relative default
        // silently found nothing in local dev.
        var seedDir = config["StepPackages:SeedDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "seed", "step-packages");
        if (!Directory.Exists(seedDir))
        {
            logger.LogDebug(
                "BuiltInStepPackageSeeder: no seed directory at '{Path}', skipping.", seedDir);
            return;
        }

        var archives = Directory.GetFiles(seedDir, "*.kdeploy-step", SearchOption.TopDirectoryOnly);
        if (archives.Length == 0)
        {
            logger.LogDebug(
                "BuiltInStepPackageSeeder: seed directory '{Path}' contains no archives.", seedDir);
            return;
        }

        logger.LogInformation(
            "BuiltInStepPackageSeeder: scanning {Count} archive(s) under '{Path}'…",
            archives.Length, seedDir);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        foreach (var archivePath in archives)
        {
            try
            {
                // Derive (name, version) cheaply from the filename — every
                // archive in seed/step-packages is named {id}-{version}.kdeploy-step
                // by the pack target. Fall back to inspecting the archive's
                // manifest.json if the convention is broken.
                var fileName = Path.GetFileNameWithoutExtension(archivePath);
                var dashIdx  = fileName.LastIndexOf('-');
                if (dashIdx <= 0 || dashIdx == fileName.Length - 1)
                {
                    logger.LogWarning(
                        "BuiltInStepPackageSeeder: archive '{File}' does not match the " +
                        "{{id}}-{{version}}.kdeploy-step naming convention; skipping.",
                        archivePath);
                    continue;
                }
                var pkgName    = fileName[..dashIdx];
                var pkgVersion = fileName[(dashIdx + 1)..];

                var alreadyInstalled = await db.StepPackages
                    .AnyAsync(p => p.Name == pkgName && p.Version == pkgVersion, ct)
                    .ConfigureAwait(false);

                if (alreadyInstalled)
                {
                    logger.LogDebug(
                        "BuiltInStepPackageSeeder: {Name} {Version} already installed; skipping.",
                        pkgName, pkgVersion);
                    continue;
                }

                logger.LogInformation(
                    "BuiltInStepPackageSeeder: installing built-in {Name} {Version}…",
                    pkgName, pkgVersion);

                await using var fs = File.OpenRead(archivePath);
                var result = await uploadService
                    .UploadAsync(fs, source: StepPackageSource.Preinstalled, ct)
                    .ConfigureAwait(false);

                if (!result.Success)
                {
                    logger.LogError(
                        "BuiltInStepPackageSeeder: failed to install '{File}': {Error}",
                        archivePath, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "BuiltInStepPackageSeeder: unhandled error processing '{File}'.", archivePath);
            }
        }

        await UpgradePreinstalledPinsAndSweepAsync(db, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// SC3 / SD-11: after new built-in versions seed, live steps still pinned
    /// to an older version of a Preinstalled package are bulk-upgraded to the
    /// newest installed version (built-ins are ours; minor bumps by contract),
    /// then superseded Preinstalled versions are swept. The sweep goes through
    /// <see cref="StepPackageService.UninstallAsync"/>, so a version a release
    /// snapshot still references comes back Blocked and stays — by design.
    /// Release snapshots themselves are never re-pinned.
    /// </summary>
    private async Task UpgradePreinstalledPinsAndSweepAsync(KrakenDbContext db, CancellationToken ct)
    {
        var preinstalled = await db.StepPackages.AsNoTracking()
            .Where(p => p.Source == StepPackageSource.Preinstalled)
            .Select(p => new { p.Name, p.Version, p.Source })
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var group in preinstalled.GroupBy(p => p.Name))
        {
            var versions = group.Select(p => p.Version).ToList();
            if (versions.Count < 2) { continue; } // nothing superseded

            var latest = StepPackageResolver.PickHighestSemver(versions);
            if (latest is null) { continue; }

            try
            {
                // Re-pin live steps stuck on older versions.
                var usage = await uploadService.GetUsageAsync(group.Key, ct).ConfigureAwait(false);
                var stale = usage.Groups
                    .Where(g => !string.Equals(g.Version, latest, StringComparison.Ordinal))
                    .SelectMany(g => g.Rows)
                    .ToList();

                if (stale.Count > 0)
                {
                    var result = await uploadService.BulkUpgradeAsync(
                        group.Key, latest,
                        deploymentStepIds: [.. stale.Where(r => !r.IsRunbook).Select(r => r.StepId)],
                        runbookStepIds:    [.. stale.Where(r => r.IsRunbook).Select(r => r.StepId)],
                        ct).ConfigureAwait(false);

                    logger.LogInformation(
                        "BuiltInStepPackageSeeder: auto-upgraded {Touched} pin(s) of {Name} to {Latest} " +
                        "({Skipped} skipped).",
                        result.Touched, group.Key, latest, result.Skipped.Count);
                }

                // Sweep superseded built-in versions; Blocked results stay.
                foreach (var version in versions.Where(v => v != latest))
                {
                    var uninstall = await uploadService
                        .UninstallAsync(group.Key, version, ct).ConfigureAwait(false);
                    logger.LogInformation(
                        "BuiltInStepPackageSeeder: sweep of {Name} {Version} → {Status}.",
                        group.Key, version, uninstall.Status);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "BuiltInStepPackageSeeder: pin auto-upgrade/sweep failed for {Name}; " +
                    "old versions remain installed.", group.Key);
            }
        }
    }
}
