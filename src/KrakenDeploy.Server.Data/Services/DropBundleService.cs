using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Contracts.Offline;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Generates offline drop bundles for deployments targeting offline-drop targets.
/// <para>
/// The bundle carries the SAME <see cref="DeploymentPlan"/> the online path
/// dispatches to an agent — resolved + Octostache-substituted variables,
/// flattened waves, per-step deltas — so the offline runner executes it through
/// the identical <c>DeploymentExecutor</c>. The plan is AES-256-GCM encrypted
/// with the target's per-target bundle key; the deployable packages and the
/// step-handler archives the plan needs are embedded so the bundle runs on a
/// machine with no server connectivity.
/// </para>
/// <para>
/// Bundle layout (zip):
/// <list type="bullet">
///   <item><c>plan.enc</c> — AES-GCM-encrypted serialized <see cref="DeploymentPlan"/></item>
///   <item><c>manifest.json</c> — non-sensitive metadata (HMAC-signed)</item>
///   <item><c>machine-info.json</c> — target metadata</item>
///   <item><c>packages/{packageId}/{version}/{file}</c> — deployable packages</item>
///   <item><c>step-packages/{name}/{version}/package.kdeploy-step</c> — step-handler archives</item>
///   <item><c>artifacts/</c> — runner output</item>
///   <item><c>deployment-result.json</c>, <c>deployment-log.txt</c> — runner output</item>
///   <item><c>signature.bin</c> — HMAC-SHA256 of <c>manifest.json</c></item>
/// </list>
/// The self-contained runner (<c>runner/</c>) + bootstrap are embedded by a
/// later phase; the plan itself is integrity-protected by AES-GCM, the metadata
/// by the HMAC signature.
/// </para>
/// </summary>
public class DropBundleService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPackageStore packageStore,
    IEncryptionService encryption,
    ILogger<DropBundleService> logger)
{
    /// <summary>Bundle format discriminator (2 = plan-based + encrypted).</summary>
    public const int BundleFormat = 2;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Generates a drop bundle for <paramref name="deployment"/> from its fully
    /// resolved <paramref name="plan"/> and stores it on disk. Returns the
    /// relative path to the bundle zip.
    /// </summary>
    /// <param name="plan">
    /// The same <see cref="DeploymentPlan"/> the online path builds — already
    /// resolved, substituted, flattened, with per-step deltas attached.
    /// </param>
    /// <param name="bundleKey">
    /// Decrypted 32-byte AES-256-GCM key (the target's per-target bundle key)
    /// used to encrypt <c>plan.enc</c>.
    /// </param>
    /// <param name="stepPackageArchivePath">
    /// Resolves the full path of a stored <c>.kdeploy-step</c> archive for a
    /// <c>(name, version)</c> pair, or <c>null</c> if not installed. Wired to
    /// <c>StepPackageService.TryGetArchivePath</c>.
    /// </param>
    public async Task<string> GenerateAsync(
        Deployment deployment,
        DeploymentPlan plan,
        byte[] bundleKey,
        Func<string, string, string?> stepPackageArchivePath,
        string dataPath,
        string? runnerStageDir = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bundleKey);
        ArgumentNullException.ThrowIfNull(stepPackageArchivePath);
        ArgumentNullException.ThrowIfNull(deployment.Release);
        ArgumentNullException.ThrowIfNull(deployment.Environment);
        ArgumentNullException.ThrowIfNull(deployment.Target);

        var target = deployment.Target;
        var release = deployment.Release;

        // ── Manifest (non-sensitive metadata only; signed for integrity) ─────
        var manifest = new DropManifest
        {
            BundleFormat = BundleFormat,
            DeploymentId = deployment.Id,
            ProjectName = release.Project?.Name ?? "",
            ReleaseVersion = release.Version,
            EnvironmentName = deployment.Environment.Name,
            TargetName = target.Name,
            TargetId = target.Id,
            CreatedUtc = DateTimeOffset.UtcNow,
            Steps = plan.Steps
                .Select(s => new DropManifestStep
                {
                    Index = s.Index,
                    Name = s.Name,
                    StepType = s.StepType,
                })
                .ToList(),
        };
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOpts);

        // ── Encrypt the plan ─────────────────────────────────────────────────
        // The serialized plan carries resolved (incl. sensitive) variables, so
        // it's AES-GCM encrypted with the per-target bundle key. GCM is
        // authenticated — tampering fails decryption, so the plan needs no
        // separate signature.
        var planJson = JsonSerializer.Serialize(plan, JsonOpts);
        var planEnc = AesGcmCipher.Encrypt(bundleKey, planJson);

        var machineInfoJson = JsonSerializer.Serialize(new
        {
            target.MachineName,
            target.OperatingSystem,
            target.AgentVersion,
            target.Roles,
        }, JsonOpts);

        // ── Create zip ──────────────────────────────────────────────────────
        var bundleDir = Path.Combine(dataPath, "drop-bundles", deployment.Id.ToString());
        Directory.CreateDirectory(bundleDir);
        var zipPath = Path.Combine(bundleDir, $"drop-{deployment.Id}.zip");

        await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

            AddTextEntry(archive, "manifest.json", manifestJson);
            AddTextEntry(archive, OfflineBundleLayout.EncryptedPlanFile, planEnc);
            AddTextEntry(archive, "machine-info.json", machineInfoJson);

            // Deployable packages + step-handler archives the plan references.
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await AddPlanPackagesAsync(db, archive, plan, ct).ConfigureAwait(false);
            await AddStepPackagesAsync(archive, plan, stepPackageArchivePath, ct).ConfigureAwait(false);

            // Runner output placeholders (the runner overwrites these).
            AddTextEntry(archive, OfflineBundleLayout.ResultFile,
                JsonSerializer.Serialize(new OfflineDropResult { DeploymentId = deployment.Id }, JsonOpts));
            AddTextEntry(archive, OfflineBundleLayout.LogFile,
                $"# Deployment Log — {deployment.Id}\n");
            archive.CreateEntry($"{OfflineBundleLayout.ArtifactsDir}/");

            // Entrypoint: bootstrap scripts + README, and (best-effort) the
            // self-contained runner so the bundle runs without .NET installed.
            await AddRunnerAndBootstrapAsync(archive, deployment, runnerStageDir, ct)
                .ConfigureAwait(false);

            // HMAC signature of the metadata.
            var hmacKey = GetHmacKey(target);
            if (hmacKey is not null)
            {
                var sig = HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(manifestJson));
                var sigEntry = archive.CreateEntry("signature.bin", CompressionLevel.NoCompression);
                await using var sigStream = sigEntry.Open();
                await sigStream.WriteAsync(sig, ct).ConfigureAwait(false);
            }
        }

        var relativePath = Path.GetRelativePath(dataPath, zipPath).Replace('\\', '/');
        logger.LogInformation(
            "Generated offline drop bundle for deployment {DeploymentId}: {Path} ({Size} bytes, {Steps} step(s)).",
            deployment.Id, relativePath, new FileInfo(zipPath).Length, plan.Steps.Length);
        return relativePath;
    }

    /// <summary>Opens the drop bundle zip for download.</summary>
    public static Stream OpenRead(string dropBundlePath, string dataPath)
    {
        var fullPath = Path.Combine(dataPath, dropBundlePath.Replace('/', Path.DirectorySeparatorChar));
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    /// <summary>Gets the full filesystem path of a drop bundle.</summary>
    public static string GetFullPath(string dropBundlePath, string dataPath)
        => Path.Combine(dataPath, dropBundlePath.Replace('/', Path.DirectorySeparatorChar));

    // ── Private helpers ──────────────────────────────────────────────────────

    private byte[]? GetHmacKey(Core.Domain.Targets.DeploymentTarget target)
    {
        var hmacEncrypted = target.OfflineDropConfig?.HmacKeyEncrypted;
        if (string.IsNullOrEmpty(hmacEncrypted))
        {
            return null;
        }
        var base64Key = encryption.Decrypt(hmacEncrypted);
        return Convert.FromBase64String(base64Key);
    }

    /// <summary>
    /// Copies every deployable package the plan references (step packages +
    /// referenced packages) into <c>packages/{id}/{version}/</c>. A missing
    /// package is a warning — the step that needs it fails at run time, mirroring
    /// the online behaviour of an unavailable package.
    /// </summary>
    private async Task AddPlanPackagesAsync(
        KrakenDbContext db, ZipArchive archive, DeploymentPlan plan, CancellationToken ct)
    {
        var refs = new HashSet<(string Id, string Version)>();
        foreach (var s in plan.Steps)
        {
            if (!string.IsNullOrEmpty(s.PackageId) && !string.IsNullOrEmpty(s.PackageVersion))
            {
                refs.Add((s.PackageId, s.PackageVersion));
            }
            if (s.ReferencedPackages is not null)
            {
                foreach (var r in s.ReferencedPackages)
                {
                    if (!string.IsNullOrEmpty(r.PackageId) && !string.IsNullOrEmpty(r.Version))
                    {
                        refs.Add((r.PackageId, r.Version));
                    }
                }
            }
        }

        foreach (var (id, version) in refs)
        {
            var pkg = await db.Packages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PackageId == id && p.Version == version, ct)
                .ConfigureAwait(false);

            if (pkg is null)
            {
                logger.LogWarning(
                    "Package {PackageId} v{Version} not found — omitted from offline bundle.",
                    id, version);
                continue;
            }

            try
            {
                await using var pkgStream = await packageStore
                    .OpenReadAsync(pkg.StoredPath, ct).ConfigureAwait(false);
                var entryPath = $"{OfflineBundleLayout.PackageDir(pkg.PackageId, pkg.Version)}/{pkg.FileName}";
                var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
                await using var entryStream = entry.Open();
                await pkgStream.CopyToAsync(entryStream, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                logger.LogWarning(
                    "Package file missing on disk for {PackageId} v{Version} at {Path}.",
                    id, version, pkg.StoredPath);
            }
        }
    }

    /// <summary>
    /// Copies the <c>.kdeploy-step</c> handler archive for every step-package
    /// the plan pins into <c>step-packages/{name}/{version}/</c>. Missing here is
    /// FATAL — without the handler assembly the offline runner cannot execute the
    /// step, so we refuse to ship a bundle that would fail mid-run.
    /// </summary>
    private static async Task AddStepPackagesAsync(
        ZipArchive archive,
        DeploymentPlan plan,
        Func<string, string, string?> archivePathResolver,
        CancellationToken ct)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(s.StepPackageName) || string.IsNullOrWhiteSpace(s.StepPackageVersion))
            {
                continue;
            }
            if (!added.Add($"{s.StepPackageName}/{s.StepPackageVersion}"))
            {
                continue;
            }

            var archivePath = archivePathResolver(s.StepPackageName, s.StepPackageVersion);
            if (archivePath is null || !File.Exists(archivePath))
            {
                throw new InvalidOperationException(
                    $"Step package '{s.StepPackageName}' v{s.StepPackageVersion} (required by step " +
                    $"'{s.Name}') has no archive on the server. Cannot build a self-contained offline " +
                    "bundle — install the step package, then re-create the release.");
            }

            var entryPath =
                $"{OfflineBundleLayout.StepPackageDir(s.StepPackageName, s.StepPackageVersion)}/{Path.GetFileName(archivePath)}";
            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            await using var src = new FileStream(
                archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var dest = entry.Open();
            await src.CopyToAsync(dest, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the bundle entrypoint — cross-platform bootstrap scripts + a
    /// README — and, when a published runner is staged on the server
    /// (<c>{dataPath}/offline-runner/{rid}/</c>), embeds the self-contained
    /// runner under <c>runner/</c> so the bundle executes on a machine with no
    /// .NET installed. Best-effort by design (offline is secondary): with no
    /// staged runner the bootstrap falls back to a <c>KrakenDeploy.Agent</c> on
    /// PATH, and the bundle is just smaller.
    /// </summary>
    private async Task AddRunnerAndBootstrapAsync(
        ZipArchive archive, Deployment deployment, string? runnerStageDir, CancellationToken ct)
    {
        AddTextEntry(archive, "run.cmd", WindowsBootstrap);
        AddTextEntry(archive, "run.sh", LinuxBootstrap);
        AddTextEntry(archive, "README.txt", BuildReadme(deployment));

        if (string.IsNullOrEmpty(runnerStageDir) || !Directory.Exists(runnerStageDir))
        {
            logger.LogInformation(
                "No staged offline runner at '{Dir}' — bundle relies on a KrakenDeploy.Agent on PATH.",
                runnerStageDir ?? "<none>");
            return;
        }

        // Best-effort by contract: a concurrent writer on the staged runner (an
        // admin re-publishing it) must not fail the whole deployment. On any IO
        // error we log and leave embedding off — the bootstrap then falls back to
        // a KrakenDeploy.Agent on PATH.
        try
        {
            var files = Directory.GetFiles(runnerStageDir, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(runnerStageDir, file).Replace('\\', '/');
                var entry = archive.CreateEntry($"{OfflineBundleLayout.RunnerDir}/{rel}", CompressionLevel.Optimal);
                await using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var dest = entry.Open();
                await src.CopyToAsync(dest, ct).ConfigureAwait(false);
            }
            logger.LogInformation(
                "Embedded self-contained runner ({Count} file(s)) from '{Dir}' into the bundle.",
                files.Length, runnerStageDir);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex,
                "Failed to embed the staged runner from '{Dir}' (concurrent write?); " +
                "the bundle will rely on a KrakenDeploy.Agent on the target's PATH.",
                runnerStageDir);
        }
    }

    // Bootstrap chooses the bundled runner if present, else a KrakenDeploy.Agent
    // on PATH. The key comes from bundle.key (next to the script) or, failing
    // that, the KRAKEN_BUNDLE_KEY env var (the runner falls back to it).
    private const string WindowsBootstrap =
        "@echo off\r\n" +
        "setlocal\r\n" +
        "set \"BUNDLE=%~dp0\"\r\n" +
        "if exist \"%BUNDLE%runner\\KrakenDeploy.Agent.exe\" (\r\n" +
        "  set \"RUNNER=%BUNDLE%runner\\KrakenDeploy.Agent.exe\"\r\n" +
        ") else (\r\n" +
        "  set \"RUNNER=KrakenDeploy.Agent\"\r\n" +
        ")\r\n" +
        "\"%RUNNER%\" --run-offline-drop \"%BUNDLE%.\" --key-file \"%BUNDLE%bundle.key\"\r\n";

    private const string LinuxBootstrap =
        "#!/usr/bin/env bash\n" +
        "set -euo pipefail\n" +
        "BUNDLE=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\n" +
        "if [ -x \"$BUNDLE/runner/KrakenDeploy.Agent\" ]; then\n" +
        "  RUNNER=\"$BUNDLE/runner/KrakenDeploy.Agent\"\n" +
        "else\n" +
        "  RUNNER=\"KrakenDeploy.Agent\"\n" +
        "fi\n" +
        "\"$RUNNER\" --run-offline-drop \"$BUNDLE\" --key-file \"$BUNDLE/bundle.key\"\n";

    private static string BuildReadme(Deployment deployment) =>
        $"""
        KrakenDeploy offline drop bundle
        ================================
        Deployment: {deployment.Id}
        Project:    {deployment.Release.Project?.Name ?? ""}
        Release:    {deployment.Release.Version}
        Target:     {deployment.Target?.Name ?? ""}

        To run on the offline target:

        1. Obtain the bundle key (base64) delivered out-of-band by the KrakenDeploy
           administrator, then either:
             - save it to a file named 'bundle.key' next to this README, or
             - set the KRAKEN_BUNDLE_KEY environment variable.

        2. Run the bootstrap for your OS:
             Windows : run.cmd
             Linux   : ./run.sh
           If a self-contained runner is bundled under runner/, it is used and no
           .NET install is required. Otherwise a 'KrakenDeploy.Agent' on PATH runs it.

        3. After it completes, return these to the administrator to reconcile the
           deployment (re-zip this directory and upload it on the deployment page):
             - deployment-result.json
             - deployment-log.txt
             - artifacts/ (if any)
        """;

    // No BOM: manifest.json is HMAC-signed over Encoding.UTF8.GetBytes(manifestJson)
    // (no preamble) and re-verified by OfflineResultService over the raw entry
    // bytes — a StreamWriter-emitted UTF-8 BOM would make every signed bundle
    // fail verification. Applies to all text entries for consistency.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static void AddTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Utf8NoBom);
        writer.Write(content);
    }
}

// ── Internal DTOs for bundle serialization ────────────────────────────────────

internal sealed class DropManifest
{
    public int BundleFormat { get; set; }
    public Guid DeploymentId { get; set; }
    public string ProjectName { get; set; } = "";
    public string ReleaseVersion { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public Guid TargetId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public List<DropManifestStep> Steps { get; set; } = [];
}

internal sealed class DropManifestStep
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string StepType { get; set; } = "";
}
