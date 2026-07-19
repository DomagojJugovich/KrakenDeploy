using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Identity;
using KrakenDeploy.Agent.Machine;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// Periodically polls the server for agent updates and applies them atomically
/// during a maintenance window (default 02:00–04:00 local).
/// <para>
/// C6 — the swap replaces the WHOLE publish directory (the agent is not
/// PublishSingleFile, so a new apphost must load its own managed DLLs), keeps a
/// backup, and is transactional: any failure rolls back in-process and keeps the
/// current binary running. A SHA-256 hash is verified on EVERY apply (not just
/// on download), an update is refused if the server supplies no hash or a
/// contract-skewed build, and after the restart a health gate confirms the new
/// version registered — otherwise it automatically rolls back to the backup and
/// reports the failure. See <see cref="SelfUpdateFileOps"/> and
/// <see cref="AgentUpgradeMarker"/>.
/// </para>
/// <para>
/// Residual limitation (documented, out of this WP's reach): an in-process
/// self-updater cannot recover from a hard process kill in the sub-millisecond
/// window between two directory-content moves, nor from a new build so broken its
/// apphost will not launch at all (the probation code never runs). The marker
/// file is persisted so an external supervisor could recover those cases; every
/// failure the probation code CAN observe is handled here.
/// </para>
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

    private readonly HttpClient _http = new();

    /// <summary>Outcome of evaluating an update the server offered.</summary>
    internal enum UpdateDecision
    {
        NoUpdate,
        HashMissing,
        ContractSkew,
        Proceed,
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = updateConfig.Value;
        var dataPath = agentConfig.Value.ResolvedDataPath;
        var updatesDir = Path.Combine(dataPath, "updates");
        var markerPath = Path.Combine(updatesDir, "upgrade-pending.json");

        // ── Post-restart probation ───────────────────────────────────────────
        // A pending marker means a swap already happened; its health MUST be
        // confirmed or rolled back. This runs even when auto-update is now
        // disabled — the swap already occurred and cannot be left unresolved.
        var marker = AgentUpgradeMarker.TryLoad(markerPath);
        if (marker is not null)
        {
            try
            {
                await HandlePendingUpgradeAsync(marker, markerPath, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // shutting down mid-probation — resume on next boot
            }
            catch (Exception ex)
            {
                // Probation must never fault the host (which would kill the agent
                // and defeat the health gate). Log and fall through — the marker is
                // retained, so the next boot retries.
                logger.LogError(ex, "Self-upgrade probation failed unexpectedly.");
            }
        }

        if (!cfg.Enabled)
        {
            logger.LogInformation("Agent auto-update is disabled in configuration.");
            return;
        }

        // Wait for identity to be resolved before polling.
        try
        {
            await context.IdentityReady.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Directory.CreateDirectory(updatesDir);
        using var timer = new PeriodicTimer(cfg.CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckAndApplyUpdateAsync(updatesDir, markerPath, cfg, stoppingToken)
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

    // ── Update check + apply ──────────────────────────────────────────────────

    private async Task CheckAndApplyUpdateAsync(
        string updatesDir, string markerPath, AgentUpdateConfig cfg, CancellationToken ct)
    {
        var identity = context.Identity;
        if (identity is null)
        {
            return;
        }

        var currentVersion = machineCollector
            .Collect(agentConfig.Value.ResolvedDataPath).AgentVersion;

        // 1. Poll the server for update info.
        var info = await GetUpdateInfoAsync(identity, currentVersion, ct).ConfigureAwait(false);
        if (info is null)
        {
            return;
        }

        switch (EvaluateOffer(info))
        {
            case UpdateDecision.NoUpdate:
                return;

            case UpdateDecision.HashMissing:
                // C6: an update with no server-supplied hash is unverifiable — refuse.
                logger.LogError(
                    "Server offered update {Latest} without a SHA-256 hash — refusing.",
                    info.LatestVersion);
                await ReportAsync(AgentUpdateOutcome.HashMissing,
                    currentVersion, info.LatestVersion, "server supplied no hash", ct)
                    .ConfigureAwait(false);
                return;

            case UpdateDecision.ContractSkew:
                // C6: refuse a build the running server cannot talk to (it would be
                // refused at registration and brick the agent's dispatch link).
                logger.LogError(
                    "Refusing update {Latest}: its wire-contract v{Target} does not match " +
                    "the server's v{Server}.",
                    info.LatestVersion, info.TargetContractVersion, info.ServerContractVersion);
                await ReportAsync(AgentUpdateOutcome.ContractSkew,
                    currentVersion, info.LatestVersion,
                    $"contract skew target=v{info.TargetContractVersion} " +
                    $"server=v{info.ServerContractVersion}", ct)
                    .ConfigureAwait(false);
                return;
        }

        logger.LogInformation(
            "Agent update available: {Current} → {Latest}.", currentVersion, info.LatestVersion);

        // 2. Download the archive if it is not already staged.
        var versionDir = Path.Combine(updatesDir, info.LatestVersion ?? "unknown");
        Directory.CreateDirectory(versionDir);

        var ext = AgentRid.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            ? ".zip" : ".tar.gz";
        var downloadPath = Path.Combine(versionDir, $"agent{ext}");

        if (!File.Exists(downloadPath))
        {
            await DownloadAsync(identity, info.DownloadUrl!, downloadPath, ct).ConfigureAwait(false);
        }

        // 3. C6: verify the hash on EVERY apply — a cached / partially-written
        //    archive from a previous killed tick is re-verified, never trusted.
        if (!VerifyHash(downloadPath, info.Sha256!))
        {
            logger.LogError(
                "SHA-256 mismatch for staged update at {Path} — deleting and refusing.",
                downloadPath);
            TryDeleteFile(downloadPath);
            await ReportAsync(AgentUpdateOutcome.HashMismatch,
                currentVersion, info.LatestVersion, "archive hash mismatch", ct)
                .ConfigureAwait(false);
            return;
        }

        // 4. Only swap during the maintenance window, when idle and connected.
        //    C6/E-B: DeploymentExecutor is now a real singleton, so IsExecuting
        //    reads the LIVE in-flight registry — the swap is refused mid-deployment.
        var inWindow = InMaintenanceWindow(cfg);
        var deploymentInFlight = deploymentExecutor.IsExecuting;
        var connected = serverLink.IsConnected;
        if (!CanSwapNow(inWindow, deploymentInFlight, connected))
        {
            if (!inWindow)
            {
                logger.LogDebug(
                    "Agent update staged at {Path} — waiting for maintenance window " +
                    "({Start:HH\\:mm}–{End:HH\\:mm}).",
                    versionDir, cfg.MaintenanceWindowStart, cfg.MaintenanceWindowEnd);
            }
            else if (deploymentInFlight)
            {
                logger.LogInformation("Skipping agent update swap — a deployment is in progress.");
            }
            else
            {
                logger.LogDebug("Skipping agent update swap — not connected to server.");
            }

            return;
        }

        await ApplyUpdateAsync(downloadPath, ext, versionDir, markerPath, cfg,
            currentVersion, info, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// C6 — pure evaluation of an offered update: verifiable and contract-compatible?
    /// Extracted so the refusal logic is unit-testable without HTTP.
    /// </summary>
    internal static UpdateDecision EvaluateOffer(AgentUpdateInfo info)
    {
        if (!info.UpdateAvailable || string.IsNullOrEmpty(info.DownloadUrl))
        {
            return UpdateDecision.NoUpdate;
        }

        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            return UpdateDecision.HashMissing;
        }

        if (info.TargetContractVersion is null ||
            info.TargetContractVersion != info.ServerContractVersion)
        {
            return UpdateDecision.ContractSkew;
        }

        return UpdateDecision.Proceed;
    }

    /// <summary>
    /// C6 — a swap may proceed only inside the maintenance window, with no
    /// deployment in flight (acceptance: no swap while a deployment runs), and
    /// while connected to the server. Pure so the gate is unit-testable.
    /// </summary>
    internal static bool CanSwapNow(bool inMaintenanceWindow, bool deploymentInFlight, bool connected)
        => inMaintenanceWindow && !deploymentInFlight && connected;

    /// <summary>
    /// C6 — true when the new version has already been probed <paramref name="maxAttempts"/>
    /// times without ever confirming health, so probation must roll back instead of
    /// granting yet another fresh health window. <paramref name="maxAttempts"/> is
    /// floored at 1 so a misconfigured 0/negative value still bounds the loop.
    /// </summary>
    internal static bool AttemptsExhausted(int attemptsUsed, int maxAttempts)
        => attemptsUsed >= Math.Max(1, maxAttempts);

    /// <summary>
    /// C6 — true when <paramref name="processPath"/> is the agent's own apphost (so a
    /// whole-directory swap of its directory is safe). Guards against a
    /// framework-dependent `dotnet KrakenDeploy.Agent.dll` launch, where
    /// <see cref="Environment.ProcessPath"/> is the shared dotnet muxer. Compared
    /// case-INsensitively because on Windows ProcessPath echoes the (possibly
    /// non-canonical) launch-path casing — the muxer name still differs regardless.
    /// </summary>
    internal static bool IsAgentApphost(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        var name = Path.GetFileName(processPath);
        return SelfUpdateFileOps.AgentExeNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts, verifies, and atomically swaps the new publish directory in, then
    /// writes the probation marker and exits for the supervisor to restart. Any
    /// pre-exit failure leaves the current binary running (no exit).
    /// </summary>
    private async Task ApplyUpdateAsync(
        string downloadPath, string archiveExt, string versionDir, string markerPath,
        AgentUpdateConfig cfg, string currentVersion, AgentUpdateInfo info, CancellationToken ct)
    {
        var currentExe = Environment.ProcessPath;
        var installDir = string.IsNullOrEmpty(currentExe) ? null : Path.GetDirectoryName(currentExe);
        if (string.IsNullOrEmpty(installDir))
        {
            logger.LogError("Cannot determine the agent install directory for auto-update.");
            return;
        }

        // C6: refuse to swap unless THIS process is the agent's own apphost. When
        // the agent is launched framework-dependent (`dotnet KrakenDeploy.Agent.dll`),
        // Environment.ProcessPath is the dotnet muxer and installDir is the shared
        // .NET runtime directory — a whole-directory swap would clobber the runtime.
        // A self-contained agent (the only shape the update archive ships) always
        // runs as its apphost, so this only blocks the unsupported muxer launch.
        if (!IsAgentApphost(currentExe))
        {
            logger.LogError(
                "Refusing self-upgrade: the running process '{Exe}' is not the agent apphost " +
                "(framework-dependent / muxer launch?). Swapping '{Dir}' would target the wrong files.",
                currentExe, installDir);
            await ReportAsync(AgentUpdateOutcome.SwapFailed,
                currentVersion, info.LatestVersion, "not running as the agent apphost", ct)
                .ConfigureAwait(false);
            return;
        }

        // Extract to a fresh staging directory.
        var stagingDir = Path.Combine(versionDir, "staging");
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }
        Directory.CreateDirectory(stagingDir);

        if (!await ExtractArchiveAsync(downloadPath, archiveExt, stagingDir, ct).ConfigureAwait(false))
        {
            await ReportAsync(AgentUpdateOutcome.SwapFailed,
                currentVersion, info.LatestVersion, "archive extraction failed", ct)
                .ConfigureAwait(false);
            return;
        }

        // The payload may be wrapped in a top-level folder — use the directory that
        // actually contains the apphost as the "new install" root.
        var newExe = SelfUpdateFileOps.FindAgentExecutable(stagingDir);
        if (newExe is null)
        {
            logger.LogError("No agent executable in the extracted update at {Dir}.", stagingDir);
            await ReportAsync(AgentUpdateOutcome.SwapFailed,
                currentVersion, info.LatestVersion, "no agent executable in payload", ct)
                .ConfigureAwait(false);
            return;
        }
        var newDir = Path.GetDirectoryName(newExe)!;

        // Backup lives right next to the install dir (SAME volume — the swap renames
        // locked exe/DLLs, which only works within a volume).
        var backupDir = installDir + ".backup";

        // ── The swap (transactional; a failure rolls back and keeps us running) ──
        try
        {
            SelfUpdateFileOps.ApplySwap(installDir, newDir, backupDir);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Self-upgrade swap failed and was rolled back in-process; " +
                "continuing on the current binary {Version}.", currentVersion);
            await ReportAsync(AgentUpdateOutcome.SwapFailed,
                currentVersion, info.LatestVersion, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        // On Unix the copied apphost needs its execute bit (File.Copy does not
        // preserve mode).
        if (!OperatingSystem.IsWindows() &&
            SelfUpdateFileOps.FindAgentExecutable(installDir) is { } installedExe)
        {
            MakeExecutable(installedExe);
        }

        // Persist the probation marker BEFORE exit so the new binary (or a future
        // external supervisor) can confirm health or roll back. If the write fails
        // the swap is ALREADY committed to disk — we MUST still restart into the
        // new binary rather than keep running the old code with new files in place;
        // the only loss is that this upgrade won't be health-gated (fails open).
        try
        {
            AgentUpgradeMarker.Save(markerPath, new AgentUpgradeMarker
            {
                FromVersion             = currentVersion,
                ToVersion               = info.LatestVersion ?? "unknown",
                InstallDir              = installDir,
                BackupDir               = backupDir,
                WrittenUtc              = DateTimeOffset.UtcNow,
                HealthTimeoutSeconds    = (int)cfg.HealthCheckTimeout.TotalSeconds,
                ExpectedContractVersion = info.ServerContractVersion,
                AttemptsUsed            = 0,
            });

            logger.LogInformation(
                "Agent binary swapped {From} → {To}. Exiting for supervisor restart; " +
                "backup retained at {Backup} until the new version is confirmed healthy.",
                currentVersion, info.LatestVersion, backupDir);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Self-upgrade swap committed but the probation marker could not be written; " +
                "restarting into the new version {To} WITHOUT a health gate.", info.LatestVersion);
        }

        // Brief delay to let logs flush, then exit for restart.
        await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
        Environment.Exit(0);
    }

    // ── Post-restart probation (health gate + rollback) ───────────────────────

    private async Task HandlePendingUpgradeAsync(
        AgentUpgradeMarker marker, string markerPath, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, updateConfig.Value.MaxHealthAttempts);

        logger.LogInformation(
            "Self-upgrade probation (attempt {Attempt}/{Max}): confirming new version {To} " +
            "(was {From}) is healthy; backup at {Backup}.",
            marker.AttemptsUsed + 1, maxAttempts,
            marker.ToVersion, marker.FromVersion, marker.BackupDir);

        // Cross-boot crash guard: if the new binary has already restarted
        // maxAttempts times without ever confirming health (i.e. it keeps dying
        // before its per-restart window elapses), stop trying and roll back.
        if (AttemptsExhausted(marker.AttemptsUsed, maxAttempts))
        {
            await RollBackAsync(marker, markerPath,
                $"new version never became healthy across {maxAttempts} restart attempts", ct)
                .ConfigureAwait(false);
            return;
        }

        // Count THIS attempt BEFORE waiting so a crash during the wait still
        // advances the cross-boot counter (best-effort persist — a failed counter
        // write only weakens the crash-loop bound, it does not break correctness).
        try
        {
            AgentUpgradeMarker.Save(markerPath, marker with { AttemptsUsed = marker.AttemptsUsed + 1 });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist the probation attempt counter (non-fatal).");
        }

        // Give the new binary a FULL window from NOW. Anchoring to the marker's
        // write time would let a delayed restart (a machine reboot after the
        // maintenance-window swap) consume the window and roll back a perfectly
        // healthy upgrade; the crash-before-window loop is bounded by the attempt
        // counter above instead of by wall-clock since the swap.
        var window = TimeSpan.FromSeconds(Math.Max(1, marker.HealthTimeoutSeconds));
        var healthy = await WaitForHealthyAsync(window, ct).ConfigureAwait(false);

        if (healthy)
        {
            // Confirm the version that actually registered healthy is the one the
            // marker expected. If NOT (e.g. a prior rollback restored the OLD binary
            // but its marker-delete was interrupted), this is an already-resolved
            // state: do NOT report a bogus success or discard the backup — just clear
            // the marker. Reliable because the update only converges when a healthy
            // agent's reported version equals the manifest version, so a genuine
            // upgrade always has runningVersion == marker.ToVersion here.
            var runningVersion = machineCollector
                .Collect(agentConfig.Value.ResolvedDataPath).AgentVersion;
            if (!string.Equals(runningVersion, marker.ToVersion, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Probation marker expected {To} but the running agent is {Running} — the " +
                    "upgrade was already resolved (likely rolled back). Clearing the marker " +
                    "without committing.", marker.ToVersion, runningVersion);
                AgentUpgradeMarker.Delete(markerPath);
                return;
            }

            // Commit: discard the backup, clear the marker, report success.
            TryDeleteDir(marker.BackupDir);
            AgentUpgradeMarker.Delete(markerPath);
            logger.LogInformation(
                "Self-upgrade to {To} confirmed healthy; backup discarded.", marker.ToVersion);
            await ReportAsync(AgentUpdateOutcome.Succeeded,
                marker.FromVersion, marker.ToVersion, "health gate passed", ct)
                .ConfigureAwait(false);
            return;
        }

        // Alive but never registered within a full window → unhealthy → roll back.
        await RollBackAsync(marker, markerPath,
            $"new version did not register healthy within {marker.HealthTimeoutSeconds}s", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Restores the backed-up previous version over the (unhealthy) new install and
    /// exits non-zero so the supervisor relaunches the restored binary. On rollback
    /// failure the marker is retained so the next boot retries.
    /// </summary>
    private async Task RollBackAsync(
        AgentUpgradeMarker marker, string markerPath, string reason, CancellationToken ct)
    {
        logger.LogError(
            "Rolling back self-upgrade to {To}: {Reason}. Restoring {From}.",
            marker.ToVersion, reason, marker.FromVersion);

        string? rollbackError = null;
        try
        {
            SelfUpdateFileOps.RestoreFromBackup(
                marker.InstallDir, marker.BackupDir, marker.InstallDir + ".failed");
            AgentUpgradeMarker.Delete(markerPath);
            logger.LogWarning(
                "Rollback complete. Exiting so the supervisor relaunches {From}.",
                marker.FromVersion);
        }
        catch (Exception ex)
        {
            // Leave the marker in place so the next boot retries the rollback.
            rollbackError = ex.Message;
            logger.LogCritical(ex,
                "Rollback FAILED. Manual intervention may be required: restore '{Backup}' " +
                "over '{Install}'.", marker.BackupDir, marker.InstallDir);
        }

        await ReportAsync(AgentUpdateOutcome.RolledBack,
            marker.FromVersion, marker.ToVersion,
            rollbackError is null ? reason : $"rollback FAILED: {rollbackError}",
            ct).ConfigureAwait(false);

        // Exit so the supervisor relaunches the (restored) previous binary.
        await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
        Environment.Exit(70); // non-zero: this run failed its health gate
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the agent to register healthy
    /// (<see cref="AgentContext.RegistrationAccepted"/>). Returns false if the
    /// deadline passes without a healthy registration.
    /// </summary>
    private async Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero)
        {
            // Deadline already elapsed (repeated bounce) — one instantaneous check.
            return context.RegistrationAccepted.IsCompletedSuccessfully;
        }

        try
        {
            await context.RegistrationAccepted.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // ── Server I/O ────────────────────────────────────────────────────────────

    private async Task<AgentUpdateInfo?> GetUpdateInfoAsync(
        AgentIdentity identity, string currentVersion, CancellationToken ct)
    {
        var url = $"{identity.ServerUrl.TrimEnd('/')}/api/agents/update-info" +
                  $"?rid={Uri.EscapeDataString(AgentRid)}" +
                  $"&currentVersion={Uri.EscapeDataString(currentVersion)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AgentToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AgentUpdateInfo>(ct).ConfigureAwait(false);
    }

    private async Task DownloadAsync(
        AgentIdentity identity, string downloadUrl, string downloadPath, CancellationToken ct)
    {
        var url = $"{identity.ServerUrl.TrimEnd('/')}{downloadUrl}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AgentToken);

        using var resp = await _http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var fs = new FileStream(downloadPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 8192, useAsync: true);
        await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Agent update downloaded to {Path} ({Size} bytes).",
            downloadPath, new FileInfo(downloadPath).Length);
    }

    /// <summary>
    /// C6 — best-effort report of a self-upgrade outcome to the server so it is
    /// visible as an audit entry on the target. Never throws.
    /// </summary>
    private async Task ReportAsync(
        string outcome, string? from, string? to, string? detail, CancellationToken ct)
    {
        try
        {
            var identity = context.Identity;
            if (identity is null || string.IsNullOrEmpty(identity.ServerUrl))
            {
                logger.LogDebug("Cannot report update outcome {Outcome} — identity not ready.", outcome);
                return;
            }

            var url = $"{identity.ServerUrl.TrimEnd('/')}/api/agents/update-status";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(
                    new AgentUpdateStatusReport(outcome, from, to, detail)),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AgentToken);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Update-status report ({Outcome}) returned {Status}.",
                    outcome, (int)resp.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to report update outcome {Outcome} to server (best-effort).", outcome);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> ExtractArchiveAsync(
        string archivePath, string archiveExt, string stagingDir, CancellationToken ct)
    {
        if (archiveExt == ".zip")
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, stagingDir);
            return true;
        }

        // tar.gz — use the platform tar (present on Linux/macOS agents).
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-xzf \"{archivePath}\" -C \"{stagingDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            logger.LogError("Failed to start tar to extract the agent update.");
            return false;
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            logger.LogError("tar extraction failed with exit code {Code}.", process.ExitCode);
            return false;
        }

        return true;
    }

    private static bool VerifyHash(string path, string expectedHex)
    {
        using var fs = File.OpenRead(path);
        var actualHex = Convert.ToHexStringLower(SHA256.HashData(fs));
        return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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

        // Window spans midnight (e.g. 22:00–02:00).
        return now >= start || now <= end;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            /* non-fatal */
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            /* non-fatal — a leftover backup is harmless and cleaned on next swap */
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
