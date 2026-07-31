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
    }
}
