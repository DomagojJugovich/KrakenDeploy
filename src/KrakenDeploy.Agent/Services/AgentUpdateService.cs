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
/// F5 (locked decision P8) — the extract + swap + exit window runs under the
/// EXCLUSIVE side of <see cref="MachineExecutionGate"/>, the same gate deployments
/// and ad-hoc scripts take. That closes the 2026-07-25 parallel-safety audit CLASH:
/// <see cref="DeploymentExecutor.IsExecuting"/> is blind to ad-hoc work, so a swap
/// during an operator's diagnostic script killed it mid-run, and the gap between
/// that check and the swap was a TOCTOU that work could start inside.
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
    MachineExecutionGate executionGate,
    IServerLink serverLink,
    IOptions<AgentConfig> agentConfig,
    IOptions<AgentUpdateConfig> updateConfig,
    ILogger<AgentUpdateService> logger)
    : BackgroundService
{
    private static readonly string AgentRid = RuntimeInformation.RuntimeIdentifier;

    /// <summary>
    /// F5 — how long a ROLLBACK waits for the machine execution gate. Much shorter than
    /// the forward swap's <c>SwapGateTimeout</c> and deliberately not configurable:
    /// unlike the forward swap, expiry does not abandon the operation — the agent is
    /// running a binary that failed its health gate, so restoring takes precedence over
    /// exclusivity. The wait only buys the common case where the box happens to be busy
    /// for a moment.
    /// </summary>
    private static readonly TimeSpan RollbackGateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cap on free text taken from a SERVER response and re-published (into the local log, and
    /// back to the server as an update-status detail that lands in the audit log and is
    /// forwarded off-premises). Long enough for a real "task X wave 2 is running" explanation;
    /// short enough that a compromised or misconfigured server cannot author audit rows.
    /// </summary>
    internal const int MaxRemoteDetailLength = 512;

    /// <summary>Trims remote text to <paramref name="max"/>, marking that it was trimmed.</summary>
    internal static string? Truncate(string? value, int max)
        => value is { } v && v.Length > max
            ? string.Concat(v.AsSpan(0, max), $"… (truncated from {v.Length})")
            : value;

    /// <summary>
    /// F5 — bound on the pre-swap "does the server still have work for me?" call. Short
    /// and deliberately not configurable: it runs while the EXCLUSIVE machine lease is
    /// HELD, so it is whole-machine blocking time, and the request is one indexed
    /// existence check against a server this agent is already connected to. Anything
    /// slower is a sick path, and the right answer to a sick path is to defer.
    /// </summary>
    private static readonly TimeSpan TaskInFlightCheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// F5 — bound on a best-effort outcome report. See <see cref="ReportAsync"/>: the
    /// reports either run on the way to <see cref="Environment.Exit"/> or (before this
    /// bound existed) inside the machine lease, and <see cref="HttpClient.Timeout"/>'s
    /// 100 s default is far too long for either.
    /// </summary>
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http = new();

    /// <summary>
    /// F5 — the staged version we have already filed a <c>SwapDeferred</c> report for.
    /// A deferral recurs on EVERY check while the cause persists, and the cause can be
    /// indefinite (a task parked <c>Queued</c> by maintenance mode, or at
    /// <c>PendingOfflineResult</c>). Reporting each tick filed ~24 audit rows per target
    /// per night — and, because <c>SubscriptionMatcher</c> treats an empty pattern list
    /// as match-anything, ~24 webhook/Slack deliveries with it. The signal an operator
    /// needs is "this target is stuck on version X", which is worth exactly one row per
    /// staged version. Only ever touched from the single <see cref="PeriodicTimer"/>
    /// loop, so it needs no synchronisation.
    /// </summary>
    private string? _deferralReportedFor;

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
                // The offer is gone (withdrawn, or we already run it), so a later re-offer
                // of the same version is a NEW situation and may report its deferral again.
                _deferralReportedFor = null;
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
        //    reads the LIVE in-flight registry.
        //    F5: IsExecuting is now only a cheap PRE-check — it sees deployments and
        //    runbook runs but never ad-hoc scripts, and it was a TOCTOU besides (work
        //    could start between the check and the swap). The real guarantee is the
        //    machine gate's EXCLUSIVE side, taken below.
        var inWindow = InMaintenanceWindow(cfg);
        var deploymentInFlight = deploymentExecutor.IsExecuting;
        var connected = serverLink.IsConnected;

        // The server is refusing this agent's wire contract (426 on the handshake). That is a
        // DEADLOCK, not a delay: IsConnected can never become true, so a swap gated on it never
        // happens, so the binary that would fix the refusal is never installed. The refusal
        // overrides the window and the connected check — and only those; see CanSwapNow.
        var contractRefused = context.ContractRefused;
        if (contractRefused)
        {
            logger.LogWarning(
                "The server is refusing this agent's wire contract, so the maintenance window " +
                "and the connected check are bypassed for this swap: a refused agent can be " +
                "sent no work, and waiting for the window would leave it offline until " +
                "{Start:HH\\:mm}–{End:HH\\:mm} local. In-flight work is still respected.",
                cfg.MaintenanceWindowStart, cfg.MaintenanceWindowEnd);
        }

        if (!CanSwapNow(inWindow, deploymentInFlight, connected, contractRefused))
        {
            if (deploymentInFlight)
            {
                logger.LogInformation("Skipping agent update swap — a deployment is in progress.");
            }
            else if (!inWindow)
            {
                logger.LogDebug(
                    "Agent update staged at {Path} — waiting for maintenance window " +
                    "({Start:HH\\:mm}–{End:HH\\:mm}).",
                    versionDir, cfg.MaintenanceWindowStart, cfg.MaintenanceWindowEnd);
            }
            else
            {
                // Information, not Debug. The shipped MinimumLevel is Information, so a Debug
                // line here meant an agent that could not swap said so NOWHERE — which is how
                // the refusal deadlock stayed invisible: the archive downloaded and
                // hash-verified on every tick, then the swap was skipped in silence, forever.
                logger.LogInformation(
                    "Skipping agent update swap — not connected to the server. The swap is " +
                    "deferred rather than refused; it retries on the next check.");
            }

            return;
        }

        // 5. Deterministic refusals BEFORE the gate. These outcomes do not depend on
        //    what is running, so acquiring the machine gate first would be pure harm:
        //    a queued EXCLUSIVE writer blocks every new deployment and ad-hoc script on
        //    this box while it waits, and an agent that can NEVER swap would otherwise
        //    freeze the machine on every tick of every maintenance window, forever, for
        //    an update it will always refuse. The live case is the CONTAINER image:
        //    Dockerfile.agent's ENTRYPOINT is `dotnet KrakenDeploy.Agent.dll`, a muxer
        //    launch, so IsAgentApphost is permanently false there. (`dotnet run` is NOT
        //    such a case — it starts the apphost, so ProcessPath is the .exe.)
        var installDir = ResolveInstallDir();
        if (installDir is null)
        {
            await ReportAsync(AgentUpdateOutcome.SwapFailed, currentVersion,
                info.LatestVersion, "not running as the agent apphost", ct)
                .ConfigureAwait(false);
            return;
        }

        // 6. F5 (locked decision P8) — the swap window (extract + swap + exit) runs
        //    under the machine gate's EXCLUSIVE side. Because the gate is writer-fair
        //    this both WAITS for every kind of in-flight work (ad-hoc scripts
        //    included, which IsExecuting cannot see) and BLOCKS new work from starting
        //    while we hold it, closing the check-to-swap TOCTOU. Bounded: on expiry we
        //    swap nothing and let the next tick retry, rather than parking a writer
        //    that starves the agent of work indefinitely.
        //    NOTE the lease is deliberately NOT released on the success path —
        //    ApplyUpdateAsync ends in Environment.Exit, and holding the gate until the
        //    process dies is exactly the guarantee wanted. Every failure path inside
        //    returns, and the `using` releases it then.
        var (gate, gateOutcome) = await AcquireSwapGateAsync(
            executionGate, cfg.SwapGateTimeout, ct).ConfigureAwait(false);
        if (gateOutcome != SwapGate.Acquired)
        {
            if (gateOutcome == SwapGate.Busy)
            {
                logger.LogInformation(
                    "Skipping agent update swap — work on this machine did not finish " +
                    "within {Timeout}; retrying on the next check.", cfg.SwapGateTimeout);
                // The server must be able to see a machine that keeps deferring: a gate
                // held by a wedged step looks identical to a healthy busy agent from
                // the outside, and without this the only signal is a local log line.
                await ReportDeferralOnceAsync(currentVersion, info.LatestVersion,
                    $"machine busy for the whole {cfg.SwapGateTimeout} swap window", ct)
                    .ConfigureAwait(false);
            }
            else
            {
                logger.LogDebug("Skipping agent update swap — the agent is shutting down.");
            }
            return;
        }

        // Why the deferral reason is carried OUT of the lease rather than reported inside
        // it: the report is an HTTP round trip to a server that, on this branch, has just
        // proven slow or unhealthy — and whatever it costs would be whole-machine blocking
        // time, which is the very cost TaskInFlightCheckTimeout exists to bound. So the
        // lease is released first and the report goes out after it.
        string? deferralReason;
        using (gate)
        {
            // 7. Re-check the window we may have queued out of. C6's invariant is that a
            //    swap only happens inside the operator-approved window; step 4 checked it
            //    up to SwapGateTimeout ago, and an operator with a narrow window (02:00–
            //    02:05 is legal) would otherwise get the swap, the restart and the whole
            //    health-probation cycle outside their change window.
            //    The contract-refusal bypass has to apply HERE too, not only at step 4:
            //    re-checking the window unconditionally would put the deadlock straight
            //    back, just one gate acquisition later.
            if (!contractRefused && !InMaintenanceWindow(cfg))
            {
                logger.LogInformation(
                    "Skipping agent update swap — the maintenance window " +
                    "({Start:HH\\:mm}–{End:HH\\:mm}) closed while waiting for the machine.",
                    cfg.MaintenanceWindowStart, cfg.MaintenanceWindowEnd);
                return;
            }

            // 8. Ask the SERVER whether it still has work for us. Holding the gate is
            //    not enough: its unit is one WAVE, so between two waves of a live
            //    multi-wave deployment the gate is free and _running is empty, and a
            //    server wave (manual intervention, DeployRelease cascade) can sit in
            //    that gap for minutes or hours. Only the server sees whole plans.
            //    Fail-closed — anything short of a clear "idle" defers the swap.
            deferralReason = await ServerBusyReasonAsync(ct).ConfigureAwait(false);
            if (deferralReason is null)
            {
                await ApplyUpdateAsync(downloadPath, installDir, ext, versionDir,
                    markerPath, cfg, currentVersion, info, ct).ConfigureAwait(false);
                return;
            }
        }

        // Reported, not just logged. This is the refusal that can be PERMANENT — a task
        // parked Queued (scheduled for later, held by maintenance mode, or deferred by F1
        // serialization) or parked at PendingOfflineResult keeps the answer at "in flight"
        // indefinitely — so it is the one an operator most needs to see. Agent logs are
        // local-only. The reason comes FROM the check rather than being assumed: it fails
        // closed on a 5xx, an unparseable body, a transport error and its own timeout, and
        // an audit row claiming "a task is assigned" when the truth was "the server did
        // not answer" is worse than no row.
        await ReportDeferralOnceAsync(currentVersion, info.LatestVersion, deferralReason, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// F5 — files a <c>SwapDeferred</c> report at most once per staged version. See
    /// <see cref="_deferralReportedFor"/> for why the per-tick report had to go: the
    /// deferral causes are indefinite, so an unsuppressed report is an unbounded audit and
    /// notification stream rather than a signal.
    /// </summary>
    private async Task ReportDeferralOnceAsync(
        string currentVersion, string? latestVersion, string reason, CancellationToken ct)
    {
        if (_deferralReportedFor == latestVersion)
        {
            logger.LogDebug(
                "Swap still deferred for {Version} ({Reason}) — already reported.",
                latestVersion, reason);
            return;
        }

        // Stamped BEFORE the call: ReportAsync is best-effort and swallows its failures,
        // so retrying it every 5 minutes for an indefinite deferral would reproduce the
        // stream this suppressor exists to stop. One attempt per staged version.
        _deferralReportedFor = latestVersion;
        await ReportAsync(AgentUpdateOutcome.SwapDeferred, currentVersion, latestVersion,
            reason, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// C6 — the agent's own install directory, or <c>null</c> when a whole-directory
    /// swap would target the wrong files. Hoisted out of <c>ApplyUpdateAsync</c> by F5
    /// so this permanent refusal is decided BEFORE the machine gate is taken.
    /// <para>
    /// Refuses unless THIS process is the agent's own apphost: launched
    /// framework-dependent (<c>dotnet KrakenDeploy.Agent.dll</c>),
    /// <see cref="Environment.ProcessPath"/> is the shared dotnet muxer and the install
    /// directory is the shared .NET runtime, so swapping it would clobber the runtime.
    /// </para>
    /// </summary>
    private string? ResolveInstallDir()
    {
        var currentExe = Environment.ProcessPath;
        var installDir = string.IsNullOrEmpty(currentExe) ? null : Path.GetDirectoryName(currentExe);
        if (string.IsNullOrEmpty(installDir))
        {
            logger.LogError("Cannot determine the agent install directory for auto-update.");
            return null;
        }

        if (!IsAgentApphost(currentExe))
        {
            logger.LogError(
                "Refusing self-upgrade: the running process '{Exe}' is not the agent apphost " +
                "(framework-dependent / muxer launch?). Swapping '{Dir}' would target the wrong files.",
                currentExe, installDir);
            return null;
        }

        return installDir;
    }

    /// <summary>
    /// F5 — asks the server whether any non-terminal task is still assigned to this
    /// target. Returns <c>null</c> when the server gave a clear "idle", otherwise the
    /// reason to defer. FAIL-CLOSED by contract: any failure to get a clear "idle"
    /// (transport error, non-success status, unparseable body, identity not ready, or this
    /// call's own timeout) defers the swap. A deferred upgrade costs one check interval; a
    /// swap that <c>Environment.Exit</c>s into the gap between two waves kills a live
    /// deployment.
    /// <para>
    /// Returning the REASON rather than a bool is what lets the caller's audit row say
    /// what actually happened. Four distinct causes reach the same "defer" decision, and
    /// a row that names the wrong one sends an operator looking for a task that does not
    /// exist.
    /// </para>
    /// </summary>
    private async Task<string?> ServerBusyReasonAsync(CancellationToken ct)
    {
        var identity = context.Identity;
        if (identity is null || string.IsNullOrEmpty(identity.ServerUrl))
        {
            logger.LogDebug("Deferring agent update swap — identity is not resolved yet.");
            return "the agent's server identity was not resolved";
        }

        // BOUNDED, and tightly. This runs while the EXCLUSIVE machine lease is HELD, so
        // every second here is a second in which no deployment, runbook wave or ad-hoc
        // script can start on this box. The default HttpClient timeout is 100 s, which a
        // server that accepts the connection and then stalls (overloaded, mid-restart, or
        // a proxy rule that passes /agenthub but not /api/agents/*) would spend blocking
        // the machine on every tick — a cost none of the validated knobs bound, since
        // SwapGateTimeout bounds only the WAIT for the gate. The check cannot move before
        // the gate without reopening the wave-boundary race it exists to close, so it is
        // bounded instead.
        using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callTimeout.CancelAfter(TaskInFlightCheckTimeout);

        try
        {
            var url = $"{identity.ServerUrl.TrimEnd('/')}/api/agents/task-in-flight";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AgentToken);

            using var resp = await _http.SendAsync(req, callTimeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Deferring agent update swap — the server returned {Status} for the " +
                    "task-in-flight check.", (int)resp.StatusCode);
                return $"the server returned {(int)resp.StatusCode} for the task-in-flight check";
            }

            var answer = await resp.Content
                .ReadFromJsonAsync<AgentTaskInFlightResponse>(callTimeout.Token)
                .ConfigureAwait(false);

            // A missing or null InFlight is NOT "idle". A positional record with a
            // non-nullable bool bound an absent property to false, so a 200 from a proxy
            // or gateway carrying {} or a JSON error envelope read as a clear "idle" and
            // this fail-closed check failed OPEN — swapping and exiting mid-plan.
            if (answer?.InFlight is not { } inFlight)
            {
                logger.LogInformation(
                    "Deferring agent update swap — the task-in-flight check returned no " +
                    "usable answer (empty body, or a response that did not come from this " +
                    "server). Treating that as work in flight.");
                return "the task-in-flight check returned no usable answer";
            }
            if (inFlight)
            {
                // Detail is REMOTE text and it does not stop here: the deferral reason is
                // POSTed back to /api/agents/update-status and written to the audit log, which
                // the subscription poller forwards to the webhook and e-mail transports and the
                // AI-inspect transport interpolates into an LLM prompt. The audit column caps
                // it too, but bounding it at the point it enters the agent keeps the local log
                // and the request body bounded as well.
                var detail = Truncate(answer.Detail, MaxRemoteDetailLength);
                logger.LogInformation(
                    "Deferring agent update swap — the server still has work for this " +
                    "target ({Detail}). The machine gate is per WAVE, so an idle gate " +
                    "does not mean an idle plan.", detail ?? "no detail");
                return detail is { Length: > 0 }
                    ? $"the server still has work for this target: {detail}"
                    : "the server still has a non-terminal task assigned to this target";
            }

            return null;
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested && callTimeout.IsCancellationRequested)
        {
            logger.LogInformation(
                "Deferring agent update swap — the task-in-flight check did not answer " +
                "within {Timeout}. Refusing to swap without a clear answer.",
                TaskInFlightCheckTimeout);
            return $"the task-in-flight check did not answer within {TaskInFlightCheckTimeout}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Deferring agent update swap — the task-in-flight check failed. " +
                "Refusing to swap without a clear answer.");
            // Type name only: the exception message on this path can carry the server URL
            // and, for a DNS/socket failure, the resolved address.
            return $"the task-in-flight check failed ({ex.GetType().Name})";
        }
    }

    /// <summary>Outcome of the F5 swap-gate acquisition.</summary>
    internal enum SwapGate
    {
        /// <summary>The EXCLUSIVE side is held; the swap may proceed.</summary>
        Acquired,

        /// <summary>Other work outlasted the bounded wait — swap nothing, retry next tick.</summary>
        Busy,

        /// <summary>The gate was disposed under us: the agent is shutting down.</summary>
        Stopping,
    }

    /// <summary>
    /// F5 (locked decision P8) — takes the machine gate's EXCLUSIVE side for the swap
    /// window. <c>internal static</c> for the same reason as <see cref="CanSwapNow"/>
    /// and <see cref="EvaluateOffer"/>: the interesting behaviour is testable without
    /// standing up the whole hosted service.
    /// <para>
    /// Bounded on purpose. Because the gate is writer-fair, a queued writer also stops
    /// NEW work from starting — which is exactly the guarantee wanted while swapping,
    /// and exactly why the wait must not be unbounded: a wedged holder would otherwise
    /// keep the agent from accepting work for the rest of the process's life.
    /// </para>
    /// </summary>
    internal static async Task<(MachineExecutionGate.Releaser? Lease, SwapGate Outcome)>
        AcquireSwapGateAsync(MachineExecutionGate gate, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var lease = await gate
                .AcquireAsync(MachineExecutionGate.Mode.Exclusive, timeout, ct)
                .ConfigureAwait(false);
            return lease is null ? (null, SwapGate.Busy) : (lease, SwapGate.Acquired);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // Both mean "no lease, and not because the machine is busy". OCE is the
            // REACHABLE one: AcquireCoreAsync checks the token before it enqueues, so a
            // host that is already stopping throws immediately. Returning it as a distinct
            // outcome rather than letting it escape is what lets each caller decide: the
            // forward swap skips this tick, and RollBackAsync defers the restore to the
            // next boot instead of replacing the install directory with no lease. Both
            // callers MUST branch on Stopping — treating it as "not Busy, carry on" is how
            // an ungated restore and an Exit(70) during a graceful stop got in.
            return (null, SwapGate.Stopping);
        }
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
    /// C6 — a swap may proceed only inside the maintenance window, with no deployment in
    /// flight, and while connected to the server. Pure so it is unit-testable.
    /// <para>
    /// F5: <paramref name="deploymentInFlight"/> is a cheap EARLY-OUT, not the guarantee. It
    /// stays because queueing an exclusive waiter on the machine gate blocks new work while it
    /// waits, so there is no point paying that cost when we already know a deployment is
    /// running. The actual mutual exclusion — over ad-hoc scripts too, and without a
    /// check-to-swap gap — is the gate acquisition in
    /// <see cref="CheckAndApplyUpdateAsync"/>.
    /// </para>
    /// <para>
    /// <paramref name="contractRefused"/> overrides the window and the connected term, and
    /// nothing else. It is the escape hatch from a wire-contract refusal, which is otherwise a
    /// DEADLOCK rather than a delay: the server answers 426 on the handshake, so
    /// <c>IServerLink.IsConnected</c> can never become true, so a swap gated on it can never
    /// happen, so the binary that would fix the refusal is never installed. Bumping the
    /// contract on a fleet meant touching every target by hand.
    /// </para>
    /// <para>
    /// Overriding both terms is deliberate, and each has a reason it does not apply here. The
    /// connected term exists so a swap does not strand an agent mid-conversation — a refused
    /// agent has no conversation to strand. The window exists so a restart does not disrupt
    /// work — a refused agent cannot be sent work at all, and honouring the window would leave
    /// it dark until 02:00–04:00 local, up to ~22 h after a server upgrade, for no protection
    /// gained. What is NOT overridden is everything that actually protects running work:
    /// <paramref name="deploymentInFlight"/> (a deployment that started before the server was
    /// upgraded keeps running locally), the server-side <c>task-in-flight</c> probe, which
    /// answers over REST independently of the contract and fails closed, and the machine gate's
    /// EXCLUSIVE side.
    /// </para>
    /// </summary>
    internal static bool CanSwapNow(
        bool inMaintenanceWindow, bool deploymentInFlight, bool connected, bool contractRefused)
        => !deploymentInFlight && (contractRefused || (inMaintenanceWindow && connected));

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

        // Match the file name honouring BOTH separators regardless of host OS:
        // Path.GetFileName only splits on the running OS's separator, so a
        // Windows-style path evaluated on Linux (or vice-versa) comes back whole
        // and misclassifies the apphost. (-1 when no separator) + 1 => whole string.
        var cut = processPath.AsSpan().LastIndexOfAny('/', '\\');
        var name = processPath[(cut + 1)..];
        return SelfUpdateFileOps.AgentExeNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts, verifies, and atomically swaps the new publish directory in, then
    /// writes the probation marker and exits for the supervisor to restart. Any
    /// pre-exit failure leaves the current binary running (no exit).
    /// </summary>
    private async Task ApplyUpdateAsync(
        string downloadPath, string installDir, string archiveExt, string versionDir,
        string markerPath, AgentUpdateConfig cfg, string currentVersion,
        AgentUpdateInfo info, CancellationToken ct)
    {
        // installDir was resolved and validated by ResolveInstallDir BEFORE the machine
        // gate was taken (F5) — a permanent refusal must not first freeze the box.

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
    /// <para>
    /// F5 — this is the SECOND swap window, and it runs under the machine gate's
    /// EXCLUSIVE side for the same reason as the forward swap: it replaces the whole
    /// install directory and ends in <c>Environment.Exit</c>. Hardening only the
    /// forward path would have left the rollback able to pull the directory out from
    /// under a script that started in the window between a failed health check and this
    /// call. Best-effort by design: the gate wait is short and a timeout does NOT
    /// abandon the rollback — an unhealthy binary must be restored even if the box is
    /// busy, so we proceed ungated rather than leave the agent running a bad build.
    /// A SHUTTING-DOWN host is the one exception, and it defers instead: there the lease
    /// is guaranteed absent rather than merely contended, and the marker survives to make
    /// the next boot retry.
    /// </para>
    /// </summary>
    private async Task RollBackAsync(
        AgentUpgradeMarker marker, string markerPath, string reason, CancellationToken ct)
    {
        logger.LogError(
            "Rolling back self-upgrade to {To}: {Reason}. Restoring {From}.",
            marker.ToVersion, reason, marker.FromVersion);

        // The lease is discarded, not bound: nothing here ever releases it (see below), and
        // a named local would only imply otherwise.
        var (_, gateOutcome) = await AcquireSwapGateAsync(
            executionGate, RollbackGateTimeout, ct).ConfigureAwait(false);

        if (gateOutcome == SwapGate.Stopping)
        {
            // The host is shutting down. Do NOT restore here, and do NOT exit: the marker
            // is retained, so the next boot runs the whole probation again with a live
            // gate. Both halves matter. A restore now would replace the install directory
            // with NO lease — a shutdown deliberately does not abort a running step, so
            // one may still be extracting or holding an app pool — and it is the only path
            // where the lease is guaranteed absent. And Exit(70) during an intentional
            // stop reports failure to the supervisor, so a Windows service with
            // FailureActions, a systemd unit with Restart=on-failure or a container with
            // restart=on-failure relaunches the agent the operator just stopped.
            logger.LogWarning(
                "Deferring rollback of {To} to the next boot — the agent is shutting down. " +
                "The upgrade marker is retained.", marker.ToVersion);
            return;
        }

        if (gateOutcome == SwapGate.Busy)
        {
            logger.LogWarning(
                "Rolling back WITHOUT the machine execution gate — work on this machine " +
                "did not finish within {Timeout}. Restoring an unhealthy binary takes " +
                "precedence over waiting.", RollbackGateTimeout);
        }

        // The lease is deliberately NOT scoped to the restore alone: it is held to the
        // process exit, exactly as the forward swap holds it. Releasing after
        // RestoreFromBackup would hand the machine to a queued deployment or ad-hoc
        // script that then starts extracting, stopping app pools and spawning pwsh
        // against a directory whose assemblies have just been replaced — and be
        // hard-killed mid-step (and its process tree orphaned) by the Exit below.
        //
        // That makes the exit the ONLY thing that ends the lease, so it is in a `finally`.
        // It was not, and the lease leaked for the life of the process: ReportAsync's old
        // catch filter let an HttpClient timeout (a TaskCanceledException, raised even on
        // CancellationToken.None) escape past the exit, where ExecuteAsync's probation
        // handler swallowed it as "shutting down". The gate then had a writer held by
        // nobody — every later wave parked on it forever while the target heartbeated
        // Online. ReportAsync is fixed at the source too; this is the structural guarantee
        // that no future statement added here can reopen the same hole.
        try
        {
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

            // CancellationToken.None: the report must outlive a stopping host — a cancelled
            // report would leave the server with no record of the rollback at all.
            await ReportAsync(AgentUpdateOutcome.RolledBack,
                marker.FromVersion, marker.ToVersion,
                rollbackError is null ? reason : $"rollback FAILED: {rollbackError}",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // Exit so the supervisor relaunches the (restored) previous binary. The lease
            // (when we got one) dies with the process, undisposed by design — see above.
            // No `_ = gate` pin: a discard of a local read emits no IL, so it pinned
            // nothing; the lease survives because Releaser has no finalizer and
            // Environment.Exit runs none anyway.
            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            Environment.Exit(70); // non-zero: this run failed its health gate
        }
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
    /// visible as an audit entry on the target.
    /// <para>
    /// NEVER THROWS unless <paramref name="ct"/> itself is cancelled, and callers rely on
    /// that literally: <see cref="RollBackAsync"/> reports on the way to
    /// <c>Environment.Exit</c> while holding the machine lease. The filter used to be
    /// <c>when (ex is not OperationCanceledException)</c>, which looked equivalent but was
    /// not — <see cref="HttpClient"/> raises its OWN <see cref="TaskCanceledException"/>
    /// (an OCE) when <see cref="HttpClient.Timeout"/> elapses, regardless of the token
    /// passed, so a server that accepted the connection and then stalled made this method
    /// throw even on <see cref="CancellationToken.None"/>. Discriminating on the CALLER's
    /// token instead is what makes the contract true: a genuine host shutdown still
    /// propagates, a stalled server never does.
    /// </para>
    /// </summary>
    private async Task ReportAsync(
        string outcome, string? from, string? to, string? detail, CancellationToken ct)
    {
        // Bounded independently of HttpClient.Timeout (100 s). A report is best-effort
        // telemetry, and every caller is either on the machine-lease path or on the way to
        // process exit, so a stalled server must cost seconds, not a minute and a half.
        using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callTimeout.CancelAfter(ReportTimeout);

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

            using var resp = await _http.SendAsync(req, callTimeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Update-status report ({Outcome}) returned {Status}.",
                    outcome, (int)resp.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER asked to stop. Only this may escape, and only for callers that
            // passed a real token — RollBackAsync deliberately passes None.
            throw;
        }
        catch (Exception ex)
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
