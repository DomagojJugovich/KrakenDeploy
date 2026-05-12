using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Generates offline drop bundles for deployments targeting offline-drop targets.
/// <para>
/// Bundle layout (zip):
/// <list type="bullet">
///   <item><c>manifest.json</c> — deployment metadata, steps, variables</item>
///   <item><c>variables.json</c> — resolved key/value variable pairs</item>
///   <item><c>machine-info.json</c> — target metadata</item>
///   <item><c>packages/{packageId}/{version}/{file}</c> — referenced package files</item>
///   <item><c>scripts/step-{i}-{name}.ps1</c> — per-step scripts</item>
///   <item><c>deploy.ps1</c> — orchestrator PowerShell script</item>
///   <item><c>deploy.sh</c> — orchestrator Bash script</item>
///   <item><c>signature.bin</c> — HMAC-SHA256 of <c>manifest.json</c></item>
/// </list>
/// </para>
/// </summary>
public class DropBundleService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPackageStore packageStore,
    IEncryptionService encryption,
    ILogger<DropBundleService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Generates a drop bundle for the given deployment and stores it on disk.
    /// Returns the relative path to the bundle zip file.
    /// </summary>
    public async Task<string> GenerateAsync(
        Deployment deployment,
        Dictionary<string, string> resolvedVariables,
        string dataPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(deployment.Release);
        ArgumentNullException.ThrowIfNull(deployment.Environment);
        ArgumentNullException.ThrowIfNull(deployment.Target);

        var target = deployment.Target;
        var release = deployment.Release;
        var snapshot = release.ProcessSnapshot;

        // ── Build manifest ──────────────────────────────────────────────────
        var manifest = new DropManifest
        {
            DeploymentId = deployment.Id,
            ProjectName = release.Project?.Name ?? "",
            ReleaseVersion = release.Version,
            EnvironmentName = deployment.Environment.Name,
            TargetName = target.Name,
            TargetId = target.Id,
            CreatedUtc = DateTimeOffset.UtcNow,
            Steps = snapshot.Select((s, i) => new DropManifestStep
            {
                Index = i,
                Name = s.Name,
                StepType = s.StepType,
                PackageId = s.PackageId,
                PackageVersion = s.PackageVersion,
                Config = new Dictionary<string, string>(s.Config),
            }).ToList(),
        };

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOpts);
        var variablesJson = JsonSerializer.Serialize(resolvedVariables, JsonOpts);
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

            // manifest.json
            AddTextEntry(archive, "manifest.json", manifestJson);

            // variables.json
            AddTextEntry(archive, "variables.json", variablesJson);

            // machine-info.json
            AddTextEntry(archive, "machine-info.json", machineInfoJson);

            // Per-step scripts
            for (var i = 0; i < snapshot.Count; i++)
            {
                var step = snapshot[i];
                if (step.Config.TryGetValue(KrakenScriptConfigKeys.ScriptBody, out var scriptBody) &&
                    !string.IsNullOrWhiteSpace(scriptBody))
                {
                    var safeName = SanitizeFileName(step.Name);
                    AddTextEntry(archive, $"scripts/step-{i}-{safeName}.ps1", scriptBody);
                }
            }

            // Orchestrator scripts
            AddTextEntry(archive, "deploy.ps1", GenerateOrchestratorPs1(manifest));
            AddTextEntry(archive, "deploy.sh", GenerateOrchestratorSh(manifest));

            // Packages
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await AddPackagesAsync(db, archive, snapshot, ct).ConfigureAwait(false);

            // Empty result template
            var resultTemplate = new DropResultTemplate
            {
                DeploymentId = deployment.Id,
                Status = "",
                CompletedUtc = null,
            };
            AddTextEntry(archive, "deployment-result.json",
                JsonSerializer.Serialize(resultTemplate, JsonOpts));

            // Empty log placeholder
            AddTextEntry(archive, "deployment-log.txt",
                $"# Deployment Log — {deployment.Id}\n# Paste or append log output below.\n");

            // Artifacts directory placeholder
            archive.CreateEntry("artifacts/");

            // HMAC signature
            var hmacKey = GetHmacKey(target);
            if (hmacKey is not null)
            {
                var sig = HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(manifestJson));
                var sigEntry = archive.CreateEntry("signature.bin", CompressionLevel.NoCompression);
                await using var sigStream = sigEntry.Open();
                await sigStream.WriteAsync(sig, ct).ConfigureAwait(false);
            }
        }

        // Return relative path from dataPath root
        var relativePath = Path.GetRelativePath(dataPath, zipPath).Replace('\\', '/');

        logger.LogInformation(
            "Generated drop bundle for deployment {DeploymentId}: {Path} ({Size} bytes).",
            deployment.Id, relativePath, new FileInfo(zipPath).Length);

        return relativePath;
    }

    /// <summary>
    /// Opens the drop bundle zip for download.
    /// </summary>
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

    private async Task AddPackagesAsync(
        KrakenDbContext db,
        ZipArchive archive,
        IReadOnlyList<Core.Domain.Releases.StepSnapshot> steps,
        CancellationToken ct)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (string.IsNullOrEmpty(step.PackageId) || string.IsNullOrEmpty(step.PackageVersion))
            {
                continue;
            }

            var key = $"{step.PackageId}/{step.PackageVersion}";
            if (!added.Add(key))
            {
                continue;
            }

            var pkg = await db.Packages
                .AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.PackageId == step.PackageId && p.Version == step.PackageVersion, ct)
                .ConfigureAwait(false);

            if (pkg is null)
            {
                logger.LogWarning(
                    "Package {PackageId} v{Version} not found — omitted from drop bundle.",
                    step.PackageId, step.PackageVersion);
                continue;
            }

            try
            {
                await using var pkgStream = await packageStore
                    .OpenReadAsync(pkg.StoredPath, ct)
                    .ConfigureAwait(false);

                var entryPath = $"packages/{pkg.PackageId}/{pkg.Version}/{pkg.FileName}";
                var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
                await using var entryStream = entry.Open();
                await pkgStream.CopyToAsync(entryStream, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                logger.LogWarning(
                    "Package file missing on disk for {PackageId} v{Version} at {Path}.",
                    step.PackageId, step.PackageVersion, pkg.StoredPath);
            }
        }
    }

    private static void AddTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }

    private static string GenerateOrchestratorPs1(DropManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# KrakenDeploy Offline Drop Orchestrator");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Deployment: {manifest.DeploymentId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Release:    {manifest.ReleaseVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Environment: {manifest.EnvironmentName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Generated:  {manifest.CreatedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$DropRoot = $PSScriptRoot");
        sb.AppendLine("$LogFile  = Join-Path $DropRoot 'deployment-log.txt'");
        sb.AppendLine("$ResultFile = Join-Path $DropRoot 'deployment-result.json'");
        sb.AppendLine("$ArtifactsDir = Join-Path $DropRoot 'artifacts'");
        sb.AppendLine("if (-not (Test-Path $ArtifactsDir)) { New-Item -ItemType Directory -Path $ArtifactsDir | Out-Null }");
        sb.AppendLine();
        sb.AppendLine("function Write-Log($Message) {");
        sb.AppendLine("    $ts = (Get-Date).ToUniversalTime().ToString('o')");
        sb.AppendLine("    $line = \"$ts | info | $Message\"");
        sb.AppendLine("    Add-Content -Path $LogFile -Value $line -Encoding UTF8");
        sb.AppendLine("    Write-Host $line");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Load variables");
        sb.AppendLine("$Variables = Get-Content (Join-Path $DropRoot 'variables.json') -Raw | ConvertFrom-Json");
        sb.AppendLine();
        sb.AppendLine("$Failed = $false");
        sb.AppendLine("$StartTime = (Get-Date).ToUniversalTime()");
        sb.AppendLine("Write-Log 'Deployment started'");
        sb.AppendLine();

        foreach (var step in manifest.Steps)
        {
            var safeName = SanitizeFileName(step.Name);
            sb.AppendLine(CultureInfo.InvariantCulture, $"# ── Step {step.Index}: {step.Name} ──");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Log 'Starting step {step.Index}: {step.Name}'");

            if (step.Config.TryGetValue(KrakenScriptConfigKeys.ScriptBody, out _))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"$stepScript = Join-Path $DropRoot 'scripts' 'step-{step.Index}-{safeName}.ps1'");
                sb.AppendLine("if (Test-Path $stepScript) {");
                sb.AppendLine("    try {");
                sb.AppendLine("        & $stepScript");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        Write-Log 'Step {step.Index} succeeded'");
                sb.AppendLine("    } catch {");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        Write-Log \"Step {step.Index} failed: $($_.Exception.Message)\"");
                sb.AppendLine("        $Failed = $true");
                sb.AppendLine("    }");
                sb.AppendLine("} else {");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Write-Log 'Step {step.Index}: script not found — skipped'");
                sb.AppendLine("}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Log 'Step {step.Index}: no script body — manual step'");
            }

            sb.AppendLine();
        }

        sb.AppendLine("$EndTime = (Get-Date).ToUniversalTime()");
        sb.AppendLine("$Status = if ($Failed) { 'Failed' } else { 'Succeeded' }");
        sb.AppendLine("$Result = @{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    deploymentId = '{manifest.DeploymentId}'");
        sb.AppendLine("    status       = $Status");
        sb.AppendLine("    completedUtc = $EndTime.ToString('o')");
        sb.AppendLine("} | ConvertTo-Json -Depth 3");
        sb.AppendLine("Set-Content -Path $ResultFile -Value $Result -Encoding UTF8");
        sb.AppendLine("Write-Log \"Deployment completed with status: $Status\"");
        sb.AppendLine();
        sb.AppendLine("Write-Host \"\"");
        sb.AppendLine("Write-Host \"Result written to: $ResultFile\"");
        sb.AppendLine("Write-Host \"Log written to:    $LogFile\"");
        sb.AppendLine("Write-Host \"Artifacts:         $ArtifactsDir\"");
        sb.AppendLine("Write-Host \"\"");
        sb.AppendLine("Write-Host 'Package the result files and upload them back to the KrakenDeploy server.'");

        return sb.ToString();
    }

    private static string GenerateOrchestratorSh(DropManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# KrakenDeploy Offline Drop Orchestrator");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Deployment: {manifest.DeploymentId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Release:    {manifest.ReleaseVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Environment: {manifest.EnvironmentName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Generated:  {manifest.CreatedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("DROP_ROOT=\"$(cd \"$(dirname \"$0\")\" && pwd)\"");
        sb.AppendLine("LOG_FILE=\"$DROP_ROOT/deployment-log.txt\"");
        sb.AppendLine("RESULT_FILE=\"$DROP_ROOT/deployment-result.json\"");
        sb.AppendLine("ARTIFACTS_DIR=\"$DROP_ROOT/artifacts\"");
        sb.AppendLine("mkdir -p \"$ARTIFACTS_DIR\"");
        sb.AppendLine();
        sb.AppendLine("log_msg() { local ts; ts=$(date -u +%FT%T.%3NZ); echo \"$ts | info | $1\" | tee -a \"$LOG_FILE\"; }");
        sb.AppendLine();
        sb.AppendLine("FAILED=false");
        sb.AppendLine("log_msg 'Deployment started'");
        sb.AppendLine();

        foreach (var step in manifest.Steps)
        {
            var safeName = SanitizeFileName(step.Name);
            sb.AppendLine(CultureInfo.InvariantCulture, $"# ── Step {step.Index}: {step.Name} ──");
            sb.AppendLine(CultureInfo.InvariantCulture, $"log_msg 'Starting step {step.Index}: {step.Name}'");

            if (step.Config.TryGetValue(KrakenScriptConfigKeys.ScriptBody, out _))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"STEP_SCRIPT=\"$DROP_ROOT/scripts/step-{step.Index}-{safeName}.ps1\"");
                sb.AppendLine("if [ -f \"$STEP_SCRIPT\" ]; then");
                sb.AppendLine("    if pwsh -NonInteractive -NoProfile -File \"$STEP_SCRIPT\"; then");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        log_msg 'Step {step.Index} succeeded'");
                sb.AppendLine("    else");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        log_msg 'Step {step.Index} failed'");
                sb.AppendLine("        FAILED=true");
                sb.AppendLine("    fi");
                sb.AppendLine("else");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    log_msg 'Step {step.Index}: script not found — skipped'");
                sb.AppendLine("fi");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"log_msg 'Step {step.Index}: no script body — manual step'");
            }

            sb.AppendLine();
        }

        sb.AppendLine("if $FAILED; then STATUS='Failed'; else STATUS='Succeeded'; fi");
        sb.AppendLine(CultureInfo.InvariantCulture, $"cat > \"$RESULT_FILE\" <<EOFRESULT");
        sb.AppendLine("{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  \"deploymentId\": \"{manifest.DeploymentId}\",");
        sb.AppendLine("  \"status\": \"$STATUS\",");
        sb.AppendLine("  \"completedUtc\": \"$(date -u +%FT%T.%3NZ)\"");
        sb.AppendLine("}");
        sb.AppendLine("EOFRESULT");
        sb.AppendLine();
        sb.AppendLine("log_msg \"Deployment completed with status: $STATUS\"");
        sb.AppendLine("echo ''");
        sb.AppendLine("echo \"Result: $RESULT_FILE\"");
        sb.AppendLine("echo \"Log:    $LOG_FILE\"");
        sb.AppendLine("echo 'Package the result files and upload them back to the KrakenDeploy server.'");

        return sb.ToString();
    }
}

// ── Internal DTOs for bundle serialization ────────────────────────────────────

internal sealed class DropManifest
{
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
    public string PackageId { get; set; } = "";
    public string PackageVersion { get; set; } = "";
    public Dictionary<string, string> Config { get; set; } = [];
}

internal sealed class DropResultTemplate
{
    public Guid DeploymentId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset? CompletedUtc { get; set; }
}
