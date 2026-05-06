using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Machine;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// Periodically polls the server for agent updates. Downloads new versions to a
/// staging directory and swaps the agent binary during a configurable maintenance
/// window (default 02:00–04:00 local).
/// </summary>
public sealed class AgentUpdateService(
    AgentContext context,
    MachineInfoCollector machineCollector,
    DeploymentExecutor deploymentExecutor,
    IServerLink serverLink,
    IOptions<AgentConfig> agentConfig,
    IOptions<AgentUpdateConfig> updateConfig,
    ILogger<AgentUpdateService> logger)
    : BackgroundService
{
    private static readonly string AgentRid = RuntimeInformation.RuntimeIdentifier;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = updateConfig.Value;
        if (!cfg.Enabled)
        {
            logger.LogInformation("Agent auto-update is disabled in configuration.");
            return;
        }

        // Wait for identity to be resolved.
        try
        {
            await context.IdentityReady.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var dataPath = agentConfig.Value.ResolvedDataPath;
        var updatesDir = Path.Combine(dataPath, "updates");
        Directory.CreateDirectory(updatesDir);

        using var timer = new PeriodicTimer(cfg.CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckAndApplyUpdateAsync(updatesDir, cfg, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent update check failed — will retry next tick.");
            }
        }
    }

    private async Task CheckAndApplyUpdateAsync(
        string updatesDir, AgentUpdateConfig cfg, CancellationToken ct)
    {
        var identity = context.Identity;
        if (identity is null)
        {
            return;
        }

        var currentVersion = machineCollector.Collect(agentConfig.Value.ResolvedDataPath).AgentVersion;

        // ── 1. Poll server for update info ──────────────────────────────────
        var url = $"{identity.ServerUrl.TrimEnd('/')}/api/agents/update-info" +
                  $"?rid={Uri.EscapeDataString(AgentRid)}" +
                  $"&currentVersion={Uri.EscapeDataString(currentVersion)}";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", identity.AgentToken);

        var info = await http.GetFromJsonAsync<AgentUpdateInfo>(url, ct).ConfigureAwait(false);
        if (info is null || !info.UpdateAvailable || info.DownloadUrl is null)
        {
            return; // no update needed or server not configured
        }

        logger.LogInformation(
            "Agent update available: {CurrentVersion} → {LatestVersion}.",
            currentVersion, info.LatestVersion);

        // ── 2. Download the new binary ──────────────────────────────────────
        var versionDir = Path.Combine(updatesDir, info.LatestVersion ?? "unknown");
        Directory.CreateDirectory(versionDir);

        var ext = AgentRid.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            ? ".zip" : ".tar.gz";
        var downloadPath = Path.Combine(versionDir, $"agent{ext}");

        if (!File.Exists(downloadPath))
        {
            var downloadUrl = $"{identity.ServerUrl.TrimEnd('/')}{info.DownloadUrl}";
            using var response = await http.GetAsync(downloadUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var fs = new FileStream(downloadPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 8192, useAsync: true);
            await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);

            // Verify SHA256 if the server provided one
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                var actual = SHA256.HashData(File.ReadAllBytes(downloadPath));
                var actualHex = Convert.ToHexStringLower(actual);
                if (!string.Equals(actualHex, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError(
                        "SHA256 mismatch for downloaded agent update. Expected {Expected}, got {Actual}.",
                        info.Sha256, actualHex);
                    File.Delete(downloadPath);
                    return;
                }
            }

            logger.LogInformation(
                "Agent update downloaded to {Path} ({Size} bytes).",
                downloadPath, new FileInfo(downloadPath).Length);
        }

        // ── 3. Swap during maintenance window ────────────────────────────────
        if (!InMaintenanceWindow(cfg))
        {
            logger.LogDebug(
                "Agent update staged at {Path} — waiting for maintenance window " +
                "({Start:HH\\:mm}–{End:HH\\:mm}).",
                versionDir, cfg.MaintenanceWindowStart, cfg.MaintenanceWindowEnd);
            return;
        }

        if (deploymentExecutor.IsExecuting)
        {
            logger.LogInformation(
                "Skipping agent update swap — a deployment is in progress.");
            return;
        }

        if (!serverLink.IsConnected)
        {
            logger.LogDebug("Skipping agent update swap — not connected to server.");
            return;
        }

        await ApplyUpdateAsync(downloadPath, ext, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the running agent binary and exits the process so the service
    /// supervisor restarts with the new version.
    /// </summary>
    private async Task ApplyUpdateAsync(
        string downloadPath, string archiveExt, CancellationToken ct)
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentPath))
        {
            logger.LogError("Cannot determine current process path for auto-update.");
            return;
        }

        // Extract the archive to a staging directory
        var stagingDir = Path.Combine(Path.GetDirectoryName(downloadPath)!, "staging");
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        Directory.CreateDirectory(stagingDir);

        if (archiveExt == ".zip")
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(downloadPath, stagingDir);
        }
        else
        {
            // tar.gz — use tar command on Linux
            var psi = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{downloadPath}\" -C \"{stagingDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    logger.LogError("tar extraction failed with exit code {Code}.", process.ExitCode);
                    return;
                }
            }
        }

        // Find the main executable in the staging directory
        var newExe = Directory.GetFiles(stagingDir, "KrakenDeploy.Agent*",
            SearchOption.AllDirectories)
            .FirstOrDefault(f =>
            {
                var name = Path.GetFileName(f);
                return name is "KrakenDeploy.Agent" or "KrakenDeploy.Agent.exe";
            });

        if (newExe is null)
        {
            logger.LogError(
                "Cannot find agent executable in extracted update at {Dir}.", stagingDir);
            return;
        }

        // On Unix, ensure the new binary is executable
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(newExe,
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        logger.LogInformation(
            "Swapping agent binary: {Current} → {New}. Process will exit and restart.",
            currentPath, newExe);

        // ── Atomic swap ─────────────────────────────────────────────────────
        // 1. Rename current exe → current.old (succeeds even on a running exe)
        // 2. Copy new exe to current location
        // 3. Exit — the service supervisor (Windows Service / systemd) restarts
        var oldPath = currentPath + ".old";

        // Clean up leftover .old from a previous update
        try
        {
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
        }
        catch
        {
            /* non-fatal */
        }

        File.Move(currentPath, oldPath);
        File.Copy(newExe, currentPath, overwrite: true);

        logger.LogInformation("Agent binary swapped. Exiting for restart.");

        // Brief delay to let the log flush, then exit
        await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
        Environment.Exit(0);
    }

    private static bool InMaintenanceWindow(AgentUpdateConfig cfg)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var start = cfg.MaintenanceWindowStart;
        var end = cfg.MaintenanceWindowEnd;

        if (start <= end)
        {
            return now >= start && now <= end;
        }

        // Window spans midnight (e.g. 22:00–02:00)
        return now >= start || now <= end;
    }
}
