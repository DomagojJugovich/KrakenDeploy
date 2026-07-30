using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Logging;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octostache;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Background service that reads deployment IDs from the in-process channel,
/// resolves the target agent's SignalR connection, and sends the
/// <see cref="DeploymentPlan"/> to the agent.
/// <para>
/// Before building the plan the worker:
/// <list type="number">
///   <item>Resolves project variables for the specific environment / target / roles.</item>
///   <item>Applies Octostache <c>#{VarName}</c> substitution to step Config values.</item>
///   <item>Splits <see cref="VariableService"/> StringArray values into the
///         <see cref="DeploymentPlan.ArrayVariables"/> dictionary for the agent's
///         <c>$OctopusArrays</c> PowerShell exposure.</item>
/// </list>
/// </para>
/// <para>
/// The agent executes the plan autonomously and reports back via
/// <see cref="AgentHub.AppendLogAsync"/> and
/// <see cref="AgentHub.CompleteDeploymentAsync"/>.
/// </para>
/// </summary>
public sealed class DeploymentWorker(
    Channel<TenantWorkItem> queue,
    IAgentConnectionRegistry registry,
    IHubContext<AgentHub, IAgentHubClient> agentHub,
    ServerScriptStepRunner serverRunner,
    DeployReleaseStepRunner deployReleaseRunner,
    OfflineDropBundleBuilder offlineBundleBuilder,
    IPendingSubPlanRegistry subPlans,
    IServiceScopeFactory scopeFactory,
    DeploymentDiagnosisChannel diagnosisChannel,
    InFlightWorkGauge inFlightGauge,
    TimeProvider timeProvider,
    IOptions<EngineOptions> engineOptions,
    ILogger<DeploymentWorker> logger)
    : BackgroundService
{
    // Account id of the dispatch currently on this async flow. AsyncLocal so
    // concurrent fire-and-forget dispatches don't clobber each other; read by
    // FailAsync to tag the AI-diagnosis work item with the right account.
    private readonly AsyncLocal<Guid> _dispatchAccountId = new();

    // B7 — the node task cap (Engine:MaxConcurrentTasks, default 5). Excess
    // deployments wait FIFO inside their fire-and-forget task.
    private readonly NodeTaskGate _taskGate = new(engineOptions.Value.MaxConcurrentTasks);

    // Test seam (E2): how often the in-flight dispatch lease is renewed.
    // Production uses ServerTaskLease.RenewInterval (1 min); the orchestrator
    // harness shortens it so a lease-loss teardown test runs in milliseconds
    // rather than a minute. Null → production default.
    internal TimeSpan? LeaseRenewIntervalOverride { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Process fire-and-forget; don't block the reader loop. Each dispatch
            // is tracked by the in-flight gauge so a Draining blue-green slot can
            // report when this instance's orchestration work hits zero (§5/§9).
            _ = TrackedDispatchAsync(item, stoppingToken);
        }
    }

    private async Task TrackedDispatchAsync(TenantWorkItem item, CancellationToken ct)
    {
        try
        {
            // The gate slot + in-flight gauge are acquired inside DispatchAsync,
            // AFTER the (multi-account) account context is established, so the
            // E3 child-bypass parentage read hits the right tenant DB.
            await DispatchAsync(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown while waiting for a slot — the item stays Queued in the
            // DB; the boot reconciler re-enqueues it on the next start.
        }
    }

    /// <summary>
    /// Test-only entry point exposing the dispatch loop so the orchestrator
    /// can be driven from <c>OrchestratorTestHarness</c> without spinning
    /// up the BackgroundService + Channel<Guid> queue. Production code
    /// reaches this method through <see cref="ExecuteAsync"/>'s
    /// fire-and-forget — DO NOT call directly from production paths.
    /// </summary>
    internal Task DispatchForTestAsync(Guid deploymentId, CancellationToken ct)
        => DispatchCoreAsync(Guid.Empty, deploymentId, ct);

    // Resolve the work item's account (multi-account) and run the dispatch under it;
    // the account flows via AsyncLocal into DispatchCoreAsync's scope AND the
    // server-side step runners it opens. Guid.Empty (single-instance) uses the fixed
    // connection.
    private async Task DispatchAsync(TenantWorkItem item, CancellationToken ct)
    {
        if (item.AccountId == Guid.Empty)
        {
            await GateThenDispatchCoreAsync(item.AccountId, item.Id, ct).ConfigureAwait(false);
            return;
        }

        await using var accountScope = scopeFactory.CreateAsyncScope();
        var account = await accountScope.ServiceProvider
            .GetRequiredService<IAccountResolver>()
            .ResolveByIdAsync(item.AccountId, ct)
            .ConfigureAwait(false);
        if (account is null)
        {
            logger.LogError(
                "DeploymentWorker: account {AccountId} not found for deployment {DeploymentId}.",
                item.AccountId, item.Id);
            return;
        }

        using (accountScope.ServiceProvider.GetRequiredService<IAccountContext>().WithAccount(account))
        {
            await GateThenDispatchCoreAsync(item.AccountId, item.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// B7 + E3 — acquires the node task-cap slot for a TOP-LEVEL orchestration,
    /// then runs the dispatch. A CHILD deployment (spawned by an
    /// <c>Octopus.DeployRelease</c> step; <c>ParentTaskId != null</c>) BYPASSES
    /// the gate: it is accounted for by its parent's slot, which the parent holds
    /// for the whole <c>WaitForChildAsync</c>. Without the bypass,
    /// capacity-many parents each waiting on a gate-starved child would deadlock
    /// the node permanently (E3) — recovery today would be a restart.
    /// <para>
    /// Parentage is read HERE, inside the resolved account context (multi-account
    /// tenant DB correct), and filter-free (the worker background scope has no
    /// ambient Space). The read is reliable because <c>DeploymentService.CreateAsync</c>
    /// stamps <c>ParentTaskId</c> before enqueuing the child's dispatch wake-up.
    /// </para>
    /// </summary>
    private async Task GateThenDispatchCoreAsync(Guid accountId, Guid deploymentId, CancellationToken ct)
    {
        var probe = await ProbeGateAsync(deploymentId, ct).ConfigureAwait(false);

        if (probe is { ParentTaskId: not null })
        {
            // No gate slot — covered by the parent's. Still tracked for
            // blue-green drain: a child is real in-flight orchestration work.
            // (Still serialized at claim time — a child Deployment goes through
            // the same F1 (project,env,tenant) predicate in TryClaimAsync.)
            using var childTracking = inFlightGauge.Track();
            await DispatchCoreAsync(accountId, deploymentId, ct).ConfigureAwait(false);
            return;
        }

        // F1 — (project, env, tenant) serialization, evaluated BEFORE gate
        // acquisition. If another deployment of the same key is already Running,
        // leave this TOP-LEVEL deployment Queued WITHOUT taking a NodeTaskGate
        // slot, so a blocked task never starves the node's capacity for other
        // deployments (the minutely stale-Queued re-signal retries it — no new
        // poller). Racy by design: the authoritative guard is the advisory-locked
        // claim in ServerTaskLease.TryClaimAsync; this only avoids burning a slot
        // + the prep I/O in the common blocked case. RunbookRun is exempt.
        if (probe is { Kind: ServerTaskKind.Deployment, IsSerializationBlocked: true })
        {
            logger.LogDebug(
                "DeploymentWorker: deployment {Id} waiting — another deployment of project " +
                "{ProjectId} to environment {EnvironmentId} is already Running; staying Queued " +
                "(no gate slot taken).",
                deploymentId, probe.ProjectId, probe.EnvironmentId);
            return;
        }

        // B7: the task-cap slot is taken BEFORE the in-flight gauge — a
        // queued-but-unstarted deployment must not block blue-green drain (it is
        // still Queued in the DB; the B1 claim + reconciler hand it to the
        // surviving slot if this node retires first).
        using var slot = await _taskGate.AcquireAsync(ct).ConfigureAwait(false);
        using var tracking = inFlightGauge.Track();
        await DispatchCoreAsync(accountId, deploymentId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// E3 + F1 — the pre-gate probe. One filter-free read (the worker background
    /// scope has no ambient Space) of the row's gate-relevant shape:
    /// <list type="bullet">
    ///   <item><c>ParentTaskId</c> — a non-null value is a child of an
    ///   <c>Octopus.DeployRelease</c> step, which bypasses the NodeTaskGate (its
    ///   slot is held by the parent; E3).</item>
    ///   <item><c>Kind</c> + a same-key deferral flag — a top-level
    ///   <c>Deployment</c> that must defer to another same-key deployment (an
    ///   in-flight peer OR an earlier-queued due sibling — the exact claim-time
    ///   gate, <see cref="ServerTaskLease.ClaimDeferralPredicate"/>) is left Queued
    ///   without taking a slot (F1). The read is issued only for a top-level
    ///   deployment; a child, a runbook run, or a missing row skip it.</item>
    /// </list>
    /// Returns <c>null</c> for a missing row (dispatch proceeds, loads, and no-ops
    /// on the absent task — parity with the pre-F1 parentage probe).
    /// </summary>
    private async Task<GateProbe?> ProbeGateAsync(Guid deploymentId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

        var row = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Id == deploymentId)
            .Select(t => new
            {
                t.ParentTaskId, t.Kind, t.ProjectId, t.EnvironmentId, t.TenantId, t.CreatedUtc,
                t.Status,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        // Same racy-but-cheap pre-gate skip; the authoritative gate is the
        // advisory-locked claim. Uses the SAME deferral predicate as the claim so
        // an earlier-queued (FIFO) sibling also skips the slot, not only in-flight
        // peers.
        //
        // A RESUME is exempt (WP3). A Paused task is already in
        // InFlightAfterClaim — it OWNS the (project, environment, tenant) key and has
        // never released it — so deferring it to a queued sibling is a category error,
        // and a deadlocking one: an earlier-created, now-due scheduled sibling matches
        // the FIFO arm, so the resume returned here without dispatching, while the
        // sibling could never claim because the paused task holds the key. Reconciler
        // arm 3 then re-signalled it every minute forever. TryResumeAsync skips the F1
        // re-check for exactly this reason; this is the other half of that decision.
        var blocked = row.ParentTaskId is null
            && row.Kind == ServerTaskKind.Deployment
            && row.Status != DeploymentStatus.Paused
            && await db.ServerTasks
                .IgnoreQueryFilters()
                .AnyAsync(
                    ServerTaskLease.ClaimDeferralPredicate(
                        deploymentId, row.ProjectId, row.EnvironmentId, row.TenantId,
                        row.CreatedUtc, timeProvider.GetUtcNow()),
                    ct)
                .ConfigureAwait(false);

        return new GateProbe(
            row.ParentTaskId, row.Kind, row.ProjectId, row.EnvironmentId, blocked);
    }

    /// <summary>Pre-gate probe shape (see <see cref="ProbeGateAsync"/>).</summary>
    private sealed record GateProbe(
        Guid? ParentTaskId,
        ServerTaskKind Kind,
        Guid ProjectId,
        Guid EnvironmentId,
        bool IsSerializationBlocked);

    private async Task DispatchCoreAsync(Guid accountId, Guid deploymentId, CancellationToken ct)
    {
        _dispatchAccountId.Value = accountId;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var spaceContext = scope.ServiceProvider.GetRequiredService<ISpaceContext>();
        var variableService = scope.ServiceProvider.GetRequiredService<VariableService>();
        // B4: server-side captures persist through the shared output store,
        // which encrypts sensitive values at rest (T0-6).
        var encryption = scope.ServiceProvider
            .GetRequiredService<KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService>();
        var serverBaseUrl = scope.ServiceProvider
            .GetRequiredService<IConfiguration>()["Server:BaseUrl"];

        // E2: assigned once the lease renewal is created (below). Declared out
        // here so the lease-loss teardown catch on this try can read it — a
        // captured token value is safe to inspect even after the renewal (and its
        // source) has been disposed by the try body's unwinding.
        CancellationToken leaseLostToken = CancellationToken.None;

        try
        {
            // ── Kind-aware load (D1 engine merge) ───────────────────────────
            // Both kinds live in server_tasks; a runbook run is invisible to
            // db.Deployments. The worker scope has no active Space (no HttpContext
            // → DefaultSpaceId), so the global filter would hide a task created in
            // a non-Default Space (the load returns null → it sits Queued forever).
            // Probe the row filter-free, scope the whole unit of work to its real
            // Space, then load the kind-correct subtype with its owner navigation
            // and wrap it in a kind-branched dispatch source. `deployment` is a
            // ServerTask of EITHER kind from here on — the surface rename is D2.
            // Server-side step runners open their own scopes (see
            // ExecuteServerStepAsync); LogSequencer.AppendAsync defends its own
            // short-lived scope via IgnoreQueryFilters.
            var probe = await db.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == deploymentId)
                .Select(t => new { t.SpaceId, t.Kind })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (probe is null)
            {
                logger.LogWarning("DeploymentWorker: task {Id} not found.", deploymentId);
                return;
            }
            using var spaceScope = spaceContext.WithSpace(probe.SpaceId);

            ServerTask deployment;
            ITaskDispatchSource source;
            if (probe.Kind == ServerTaskKind.RunbookRun)
            {
                var run = await db.RunbookRuns
                    .Include(r => r.Runbook)
                        .ThenInclude(rb => rb.Project)
                    .Include(r => r.Environment)
                    .Include(r => r.Targets)
                        .ThenInclude(a => a.Target!)
                    .Include(r => r.Tenant)
                    .FirstOrDefaultAsync(r => r.Id == deploymentId, ct)
                    .ConfigureAwait(false);
                if (run is null)
                {
                    logger.LogWarning("DeploymentWorker: runbook run {Id} not found.", deploymentId);
                    return;
                }
                deployment = run;
                source = new RunbookRunDispatchSource(run);
            }
            else
            {
                var dep = await db.Deployments
                    .Include(d => d.Release)
                        .ThenInclude(r => r.Project)
                    .Include(d => d.Environment)
                    .Include(d => d.Targets)
                        .ThenInclude(a => a.Target!)
                    .Include(d => d.Tenant)
                    .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
                    .ConfigureAwait(false);
                if (dep is null)
                {
                    logger.LogWarning("DeploymentWorker: deployment {Id} not found.", deploymentId);
                    return;
                }
                deployment = dep;
                source = new DeploymentDispatchSource(dep);
            }

            // ── Cancellation: dequeue-skip ──────────────────────────────────
            // A deployment can be cancelled (DeploymentService.CancelAsync →
            // Status=Cancelled) while it sits Queued in the channel or waits for
            // a scheduled start. Never transition a cancelled deployment to
            // Running or dispatch any work — bail before the Running transition.
            // This is the "cancelling a pending deployment prevents dispatch"
            // guarantee. CancelAsync already stamped CompletedUtc, so there is
            // nothing further to finalise here.
            if (deployment.Status == DeploymentStatus.Cancelled)
            {
                logger.LogInformation(
                    "DeploymentWorker: deployment {Id} was cancelled before dispatch; skipping.",
                    deploymentId);
                return;
            }

            // M14.3.1 — serialise log-sequence allocation. Single-threaded
            // until M14.4 introduced wave-parallel step execution; M-RollingDeployments
            // Phase 1b adds per-target parallel fan-out on top, so multiple
            // target waves write log entries concurrently through the same
            // sequencer.
            var logSeq = new LogSequencer(scopeFactory, timeProvider, deployment.Id);

            // ── Resolve the target SET ──────────────────────────────────
            // The assignments join is the single authority (the transitional
            // deployments.target_id column is gone). First-assigned first —
            // targets[0] is the canonical target for server-wave machine
            // variables.
            var targets = deployment.ResolvedTargets();

            if (targets.Count == 0)
            {
                await FailAsync(db, deployment, "No target assigned to task.", ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── Freeze gate (M13.F.2) ───────────────────────────────────────
            // Deployment-only: runbook runs SKIP the freeze gate (Octopus parity
            // — runbooks are operational tooling that must run during a freeze
            // window; locked decision 5). Consulted before EVERY deployment
            // dispatch path (online + offline) so an operator can't sneak past
            // the gate by configuring a target as OfflineDrop. Cheap (30 s cache).
            // Override is gated at the deployment-CREATE endpoint via
            // DeploymentFreezeOverride permission — by the time we get here the
            // deployment has already been authorised to run, so we block on raw
            // freeze match. Uses the denormalized ServerTask.SpaceId/ProjectId
            // (== Release.Project.SpaceId / Release.ProjectId) so no Release
            // dereference is needed.
            //
            // WP3 — a RESUME skips this pre-dispatch arm and is re-checked further down,
            // after the checkpoint is read, because the correct answer depends on whether
            // the task has actually executed anything (WP3-b, decision 2026-07-30):
            //   • nothing executed yet  → the freeze BLOCKS it. A gate authored as an
            //     early step parks a deployment that has touched no target, so approving
            //     it three days into a window is new work entering the window, not a
            //     running deployment finishing. Blanket-exempting a resume let an
            //     operator without DeploymentFreezeOverride park a deployment before a
            //     window and walk it straight through the middle of one.
            //   • already part-deployed → it CONTINUES. Its remaining waves are what
            //     bring the targets to a consistent version, and failing mid-way leaves
            //     production split-version. That is the same "let running deployments
            //     finish" policy this gate has always had.
            // Re-firing it HERE (before the checkpoint is even read) cannot make that
            // distinction, and failed the task from Paused without running any
            // Failure/Always cleanup wave.
            if (source.AppliesFreezeGate && deployment.Status != DeploymentStatus.Paused)
            {
                var freezeService = scope.ServiceProvider.GetRequiredService<DeploymentFreezeService>();
                var blockingFreeze = await freezeService.FindBlockingFreezeAsync(
                    spaceId:       deployment.SpaceId,
                    projectId:     deployment.ProjectId,
                    environmentId: deployment.EnvironmentId,
                    ct:            ct).ConfigureAwait(false);
                if (blockingFreeze is not null)
                {
                    var msg =
                        $"Blocked by freeze '{blockingFreeze.Name}' until " +
                        $"{blockingFreeze.EndUtc:O}. Either wait until the window " +
                        $"ends or have an operator with DeploymentFreezeOverride " +
                        $"re-issue the deployment.";
                    logger.LogWarning(
                        "Deployment {DeploymentId} blocked by freeze {FreezeId} ({FreezeName}); " +
                        "window ends {EndUtc}.",
                        deployment.Id, blockingFreeze.Id, blockingFreeze.Name, blockingFreeze.EndUtc);
                    await logSeq.AppendAsync(-1, null, "error", msg, ct).ConfigureAwait(false);
                    // The IAuditLog event tags the deployment + freeze so a
                    // forensic review of "why did Friday's release not ship"
                    // points straight at the freeze + window.
                    var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
                    await audit.RecordAsync(
                        KrakenDeploy.Server.Core.Domain.Audit.AuditEventType.DeploymentBlockedByFreeze,
                        subjectType: "Deployment",
                        subjectId:   deployment.Id.ToString(),
                        details:     $"FreezeId={blockingFreeze.Id}, " +
                                     $"Freeze={blockingFreeze.Name}, " +
                                     $"EndUtc={blockingFreeze.EndUtc:O}",
                        ct: ct).ConfigureAwait(false);
                    await FailAsync(db, deployment, msg, ct).ConfigureAwait(false);
                    return;
                }
            }

            // ── Offline drop path ───────────────────────────────────────────
            // Single-target by design — the bundle is a physical artifact
            // for a specific machine. Multi-target offline drops are refused;
            // the per-machine bundle multiplication is a polish item (no
            // operator demand surfaced yet, and the offline-drop workflow's
            // manual delivery channel makes it an odd fit for fan-out
            // semantics anyway). Checked against ANY target in the set — a
            // mixed set (online + offline-drop) can't dispatch sensibly
            // either way, so it fails with the same message instead of
            // silently treating the offline machine as an online agent.
            // Offline-drop is a DEPLOYMENT-only delivery mode (the bundle is a
            // physical artifact for a specific machine). A runbook run targeting an
            // offline-drop machine has no bundle path; it skips this branch and the
            // online dispatch below drops the (connection-less) target as offline.
            if (source.SupportsOfflineDrop
                && targets.Any(t => t.TransportMode == TransportMode.OfflineDrop))
            {
                var offlineDeployment = (Deployment)deployment;
                if (targets.Count > 1)
                {
                    // Static config refusal — deliberately BEFORE the claim so
                    // StartedUtc stays null and the AI-diagnosis gate in
                    // FailAsync skips this never-ran deployment.
                    await FailAsync(db, deployment,
                        "Offline-drop deployments must target a single machine. " +
                        "This deployment has multiple targets in its assignment set; " +
                        "either remove the extra targets or switch the offline-drop " +
                        "target's TransportMode away from OfflineDrop.", ct)
                        .ConfigureAwait(false);
                    return;
                }

                // B1: the offline path claims too — a duplicate wake-up must not
                // build (and deliver) the same bundle twice concurrently. Flow:
                // Queued→Running (claimed, leased) → bundle build →
                // PendingOfflineResult (lease released in the transition).
                // Pass the loaded entity (not just the id) so the claim reads the
                // serialization key off it — no redundant meta re-read.
                var offlineClaim = await ServerTaskLease.TryClaimAsync(db, deployment, timeProvider, ct)
                    .ConfigureAwait(false);
                if (offlineClaim != ServerTaskClaimResult.Claimed)
                {
                    // Two calls (not a ternary template) — the log message template
                    // must be a compile-time constant (CA2254).
                    if (offlineClaim == ServerTaskClaimResult.SerializationBlocked)
                    {
                        logger.LogInformation(
                            "DeploymentWorker: offline deployment {Id} lost the (project,env,tenant) " +
                            "serialization race — another deployment of the same key started first; " +
                            "staying Queued for the minutely re-signal to retry.",
                            deploymentId);
                    }
                    else if (offlineClaim == ServerTaskClaimResult.MaintenanceBlocked)
                    {
                        logger.LogInformation(
                            "DeploymentWorker: offline deployment {Id} not started — the instance is " +
                            "in maintenance mode; staying Queued until maintenance is disabled.",
                            deploymentId);
                    }
                    else
                    {
                        logger.LogInformation(
                            "DeploymentWorker: offline deployment {Id} was not claimable (cancelled " +
                            "or already claimed by another wake-up); skipping dispatch.",
                            deploymentId);
                    }
                    return;
                }

                ServerTaskLease.MirrorClaim(db, deployment, timeProvider);

                await using var offlineLease = new ServerTaskLeaseRenewal(
                    scopeFactory, deployment.Id, timeProvider, logger);

                await DispatchOfflineDropAsync(scope.ServiceProvider, db, offlineDeployment, targets[0], ct)
                    .ConfigureAwait(false);
                return;
            }

            // Offline drop is deployment-only. A runbook run assigned an
            // offline-drop target has no bundle path; refuse explicitly BEFORE the
            // claim (StartedUtc stays null) with a clear message rather than letting
            // the online path drop the connection-less target as "agent offline",
            // which would send the operator debugging agent connectivity.
            if (!source.SupportsOfflineDrop
                && targets.Any(t => t.TransportMode == TransportMode.OfflineDrop))
            {
                await FailAsync(db, deployment,
                    "Runbook runs cannot target an offline-drop machine — offline drop is a " +
                    "deployment-only delivery mode. Remove the offline-drop target from the run " +
                    "(or switch its TransportMode away from OfflineDrop).", ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── 1. Variable-source pre-flight ────────────────────────────────
            // Deployments execute the release's FROZEN variable snapshot; a null
            // VariableSnapshotUpdatedUtc means the row predates the feature and the
            // deployment refuses (pre-production policy: no soft-fallback to live
            // project variables — reproducibility is the whole point). Runbook runs
            // resolve variables LIVE and have no snapshot to be missing, so the
            // accessor returns null and this refusal is skipped for them.
            //
            // (Agent-connection check is deferred until after we know whether any
            // target-side steps need dispatching — fully-server-side tasks don't
            // require an online agent.)
            var snapshotRefusal = source.VariableSnapshotRefusal();
            if (snapshotRefusal is not null)
            {
                logger.LogError(
                    "Deployment {DeploymentId}: refusing to dispatch — no variable snapshot " +
                    "(pre-feature row).",
                    deployment.Id);
                await logSeq.AppendAsync(-1, null, "error", snapshotRefusal, ct).ConfigureAwait(false);
                await FailAsync(db, deployment, snapshotRefusal, ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── 1. Build per-target dispatch contexts ───────────────────────
            // M-RollingDeployments Phase 1b: variable resolution + Octostache
            // dictionary + system-variables + flatten + plan all live INSIDE
            // a per-target loop because:
            //   * Octopus.Machine.* keys are target-specific (id, name, roles)
            //   * variable resolution scopes on (env, target, roles, tenant)
            //   * Octostache substitution inside a step's Config (e.g.
            //     `#{Octopus.Machine.Name}` baked into a script body) must
            //     resolve to the right target's value at dispatch time
            // The structural wave layout is identical across targets (same
            // snapshot tree, same partition keys) so the orchestrator walks
            // ONE canonical wave list and indexes per-target contexts by
            // target id for the actual dispatch.
            var dbFactory = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>();
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLog>();

            var snapshotSteps = source.ProcessSnapshot
                .OrderBy(s => s.SortOrder)
                .ToArray();

            // M-RollingDeployments Phase 2 — index the FULL snapshot (not
            // just emitted plans) so RollingWindowResolver can walk parent
            // step chains. Container Kraken.StepGroup rows aren't in the
            // flat plan list, but they ARE in the snapshot.
            var snapshotById = snapshotSteps
                .Where(s => s.Id != Guid.Empty)
                .ToDictionary(s => s.Id);

            var contexts = new Dictionary<Guid, TargetDispatchContext>(targets.Count);
            TargetDispatchContext? canonical = null;
            foreach (var target in targets)
            {
                var ctx = await BuildTargetDispatchContextAsync(
                    logger, deployment, source, target, snapshotSteps, variableService,
                    serverBaseUrl, dbFactory, ct).ConfigureAwait(false);
                contexts[target.Id] = ctx;
                canonical ??= ctx;
            }

            // Defensive: targets.Count > 0 was checked above (FailAsync
            // returns earlier) so canonical is always set. Null-forgive
            // operator is safe.
            var canonicalCtx = canonical!;

            // ── 2. Process flatten warnings (M15.2) ─────────────────────────
            // Warnings are snapshot-driven (ForEach collection resolution +
            // Required check). The collection-resolution input is a snapshot
            // array variable, not a machine variable, so warnings are
            // identical across all per-target flatten results — emit once
            // from the canonical context.
            //
            // WP3-b — and once per TASK, not once per dispatch. A pause/resume cycle is by
            // construction a second dispatch of the same task, so a process with one
            // unresolved ForEach group plus N gates emitted its ForEachEmpty audit event
            // N+1 times and an M13.B subscription notified that many times for a single
            // deployment (plus a duplicate banner in the task log). Pre-WP3 this needed a
            // duplicate Queued wake-up; gates make it deterministic.
            var isResumeDispatch = deployment.Status == DeploymentStatus.Paused;
            foreach (var w in isResumeDispatch ? [] : canonicalCtx.Flatten.Warnings)
            {
                var eventType = w.Kind switch
                {
                    DeploymentPlanFlattener.WarningKind.ForEachEmpty
                        => source.Audit.ForEachEmpty,
                    DeploymentPlanFlattener.WarningKind.ForEachUnresolved
                        => source.Audit.ForEachUnresolved,
                    _ => source.Audit.ForEachEmpty,
                };
                await logSeq.AppendAsync(-1, null,
                    w.Kind == DeploymentPlanFlattener.WarningKind.ForEachEmpty ? "info" : "error",
                    $"--- {w.Source.Name}: {w.Detail} ---", ct).ConfigureAwait(false);
                await auditLog.RecordAsync(
                    eventType,
                    subjectType: source.Audit.SubjectType,
                    subjectId:   deployment.Id.ToString(),
                    details:     $"Step={w.Source.Name}, " +
                                 $"Collection={w.CollectionExpression}, " +
                                 $"Detail={w.Detail}",
                    ct: ct).ConfigureAwait(false);

                // Unresolved + Required → abort here. Empty / non-required
                // Unresolved → continue (group is a no-op).
                if (w.Kind == DeploymentPlanFlattener.WarningKind.ForEachUnresolved
                    && w.Source.Required)
                {
                    await FailAsync(db, deployment,
                        $"Required ForEach step '{w.Source.Name}' could not " +
                        $"resolve its collection: {w.Detail}", ct)
                        .ConfigureAwait(false);
                    return;
                }
            }

            // ── 3. Partition canonical steps into waves (M14.4) ─────────────
            // The wave structure is purely a function of the (snapshot,
            // StartTrigger) tuple — neither input varies across targets —
            // so partitioning the canonical step list is enough. The
            // per-target step lists are structurally identical; only the
            // Config substitutions inside the steps differ.
            List<WavePartitioner.Wave> waves;
            try
            {
                waves = WavePartitioner.Partition(
                    canonicalCtx.Steps,
                    triggerByIndex: idx => canonicalCtx.SnapshotByPlanIndex[idx].StartTrigger);
            }
            catch (WavePartitioner.InvalidWaveException ex)
            {
                await auditLog.RecordAsync(
                    source.Audit.MixedWaveRefused,
                    subjectType: source.Audit.SubjectType,
                    subjectId:   deployment.Id.ToString(),
                    details:     $"Wave=[{string.Join(", ", ex.WaveSteps.Select(s => s.Name))}], " +
                                 $"ServerSteps=[{string.Join(", ", ex.ServerStepNames)}], " +
                                 $"TargetSteps=[{string.Join(", ", ex.TargetStepNames)}]",
                    ct: ct).ConfigureAwait(false);
                await logSeq.AppendAsync(-1, null, "error", ex.Message, ct).ConfigureAwait(false);
                // M14.5 — record Skipped outcomes for the refused wave's
                // steps so the Steps tab shows "Skipped: mixed wave"
                // instead of an empty section. Per-step IsServerSide
                // mirrors the classifier so the tab's Side chip is
                // still accurate. Multi-target: outcomes are keyed by
                // (deployment, step, target=null) — the refusal is
                // deployment-scoped, not per-target.
                var refusedAt = DateTimeOffset.UtcNow;
                foreach (var refused in ex.WaveSteps)
                {
                    var snap = canonicalCtx.SnapshotByPlanIndex[refused.Index];
                    await UpsertStepOutcomeAsync(
                        db, deployment.Id, refused.Index, snap.Name,
                        StepOutcomeKind.Skipped, attemptCount: 0,
                        errorMessage: "Wave refused as server+target mixed; " +
                                      "split into two single-side waves.",
                        startedUtc:   null,
                        completedUtc: refusedAt,
                        isServerSide: WavePartitioner.IsServerStep(refused),
                        required:     snap.Required, ct).ConfigureAwait(false);
                }
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await FailAsync(db, deployment, ex.Message, ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── WP3: resume a task parked at a manual-intervention gate ─────
            // A Paused task has already been claimed once and never released its F1
            // key, so it takes TryResumeAsync (Paused→Running, no serialization
            // re-check) instead of the Queued claim below. The checkpoint written at
            // pause time is read straight off the loaded entity.
            var resumeCheckpoint = (TaskPauseCheckpoint?)null;
            if (deployment.Status == DeploymentStatus.Paused)
            {
                var resume = await ServerTaskLease
                    .TryResumeAsync(db, deployment.Id, timeProvider, ct).ConfigureAwait(false);
                if (resume != ServerTaskClaimResult.Claimed)
                {
                    if (resume == ServerTaskClaimResult.MaintenanceBlocked)
                    {
                        logger.LogInformation(
                            "DeploymentWorker: task {Id} not resumed — the instance is in " +
                            "maintenance mode; staying Paused until maintenance is disabled.",
                            deploymentId);
                    }
                    else
                    {
                        logger.LogInformation(
                            "DeploymentWorker: task {Id} was not resumable (cancelled, or " +
                            "resumed by another wake-up); skipping dispatch.",
                            deploymentId);
                    }
                    return;
                }

                // A Paused task WITHOUT a checkpoint cannot be resumed correctly: its
                // failure/alive/output state is gone, and continuing would silently run
                // cleanup steps as if nothing had failed and re-run nothing it already
                // did. Fail loudly (pre-production policy) rather than guess.
                if (string.IsNullOrEmpty(deployment.PauseCheckpointEncrypted))
                {
                    ServerTaskLease.MirrorResume(db, deployment, timeProvider);
                    await FailAsync(db, deployment,
                        "Task was Paused at a manual-intervention gate but carries no resume " +
                        "checkpoint, so its orchestration state (which waves ran, which " +
                        "targets survived, captured output variables) is unrecoverable. " +
                        "Re-deploy the release instead of resuming this task.", ct)
                        .ConfigureAwait(false);
                    return;
                }

                try
                {
                    resumeCheckpoint = TaskPauseCheckpointCodec.Read(
                        deployment.PauseCheckpointEncrypted, encryption);
                }
                // FormatException matters: AesGcmCipher.Decrypt starts with
                // Convert.FromBase64String, so a truncated or garbled column throws it
                // rather than CryptographicException, and it used to escape to the
                // generic dispatch catch with a raw ".NET: not a valid Base-64 string"
                // instead of the operator-readable diagnosis below.
                catch (Exception ex) when (ex is CryptographicException or JsonException
                                               or FormatException
                                               or InvalidOperationException)
                {
                    ServerTaskLease.MirrorResume(db, deployment, timeProvider);
                    await FailAsync(db, deployment,
                        "Task was Paused at a manual-intervention gate but its resume " +
                        $"checkpoint could not be read ({ex.GetType().Name}). The payload is " +
                        "encrypted with the current DEK — if the key was rotated by a build " +
                        "that predates WP3's rotation step, the checkpoint is unrecoverable. " +
                        "Re-deploy the release instead of resuming this task.", ct)
                        .ConfigureAwait(false);
                    return;
                }

                ServerTaskLease.MirrorResume(db, deployment, timeProvider);

                // WP3-b — freeze re-check, deferred to here so it can tell "not yet
                // started" from "mid work". A gate can sit anywhere in a process: as an
                // early step it parks a deployment that has touched NOTHING, and letting
                // an approval three days into a freeze window walk it through is exactly
                // the bypass this gate exists to prevent. Once any step has actually
                // executed, the opposite is true — the remaining waves are what bring
                // targets to a consistent version, so it continues, matching the
                // long-standing "let running deployments finish" policy.
                //
                // StartedUtc is the discriminator rather than the wave index: a skipped
                // step records a NULL StartedUtc (see RecordSkippedStepsAsync), so this
                // is "did anything run", not "how far did we get" — a run whose earlier
                // waves were all condition-skipped correctly counts as not started.
                if (source.AppliesFreezeGate)
                {
                    var executedAnything = await db.TaskStepOutcomes
                        .IgnoreQueryFilters()
                        .AnyAsync(o => o.TaskId == deployment.Id && o.StartedUtc != null, ct)
                        .ConfigureAwait(false);
                    if (!executedAnything)
                    {
                        var freezeService = scope.ServiceProvider
                            .GetRequiredService<DeploymentFreezeService>();
                        var resumeFreeze = await freezeService.FindBlockingFreezeAsync(
                            spaceId:       deployment.SpaceId,
                            projectId:     deployment.ProjectId,
                            environmentId: deployment.EnvironmentId,
                            ct:            ct).ConfigureAwait(false);
                        if (resumeFreeze is not null)
                        {
                            var frozenMsg =
                                $"Approved, but blocked by freeze '{resumeFreeze.Name}' until " +
                                $"{resumeFreeze.EndUtc:O}. This deployment had not started " +
                                "executing when it paused, so resuming it now would be new " +
                                "work entering the freeze window rather than a running " +
                                "deployment finishing. No target was touched. Wait for the " +
                                "window to end, or have an operator with " +
                                "DeploymentFreezeOverride re-issue the deployment.";
                            logger.LogWarning(
                                "Deployment {DeploymentId} approved but blocked on resume by " +
                                "freeze {FreezeId} ({FreezeName}); window ends {EndUtc}.",
                                deployment.Id, resumeFreeze.Id, resumeFreeze.Name,
                                resumeFreeze.EndUtc);
                            await logSeq.AppendAsync(-1, null, "error", frozenMsg, ct)
                                .ConfigureAwait(false);
                            var freezeAudit = scope.ServiceProvider
                                .GetRequiredService<IAuditLog>();
                            await freezeAudit.RecordAsync(
                                KrakenDeploy.Server.Core.Domain.Audit.AuditEventType
                                    .DeploymentBlockedByFreeze,
                                subjectType: "Deployment",
                                subjectId:   deployment.Id.ToString(),
                                details:     $"FreezeId={resumeFreeze.Id}, " +
                                             $"Freeze={resumeFreeze.Name}, " +
                                             $"EndUtc={resumeFreeze.EndUtc:O}, " +
                                             "BlockedAt=ResumeAfterApproval",
                                ct: ct).ConfigureAwait(false);
                            await FailAsync(db, deployment, frozenMsg, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                }

                logger.LogInformation(
                    "DeploymentWorker: resuming task {Id} from wave {Wave} after a manual " +
                    "intervention.", deploymentId, resumeCheckpoint.ResumeWaveIndex);
            }

            // ── B1: atomic claim (Queued→Running) ──────────────────────────
            // One conditional UPDATE replaces the old cancel-re-read + blind
            // Running write. Exactly one wake-up wins the row: a duplicate
            // enqueue (create + minutely job + reconciler are at-least-once),
            // a cancel that landed during the prep I/O above, or a row already
            // running elsewhere all fail the WHERE status=Queued and bail here.
            // The claim also stamps the dispatch lease (renewed below) and
            // clears ScheduledFor so the scheduled job never re-matches it.
            // Pass the loaded entity (not just the id) so the claim reads the
            // serialization key off it — no redundant meta re-read.
            var claim = resumeCheckpoint is not null
                ? ServerTaskClaimResult.Claimed // already transitioned by TryResumeAsync
                : await ServerTaskLease.TryClaimAsync(db, deployment, timeProvider, ct)
                    .ConfigureAwait(false);
            if (claim != ServerTaskClaimResult.Claimed)
            {
                // Two calls (not a ternary template) — the log message template
                // must be a compile-time constant (CA2254).
                if (claim == ServerTaskClaimResult.SerializationBlocked)
                {
                    logger.LogInformation(
                        "DeploymentWorker: deployment {Id} lost the (project,env,tenant) " +
                        "serialization race — another deployment of the same key started first; " +
                        "staying Queued for the minutely re-signal to retry.",
                        deploymentId);
                }
                else if (claim == ServerTaskClaimResult.MaintenanceBlocked)
                {
                    logger.LogInformation(
                        "DeploymentWorker: task {Id} not started — the instance is in maintenance " +
                        "mode; staying Queued until maintenance is disabled.",
                        deploymentId);
                }
                else
                {
                    logger.LogInformation(
                        "DeploymentWorker: deployment {Id} was not claimable (cancelled or " +
                        "already claimed by another wake-up); skipping dispatch.",
                        deploymentId);
                }
                return;
            }

            // Mirror the claim onto the tracked entity: ExecuteUpdate bypasses the
            // change tracker, and downstream logic reads these (FailAsync gates the
            // AI diagnosis on StartedUtc; finalisation clears the lease fields).
            // CRITICAL: mark the mirrored properties NOT-modified — the DB already
            // holds these values, and leaving them dirty would make any later
            // SaveChanges (step outcomes, etc.) blindly re-assert Running over a
            // Cancelled that landed in between. A resume already mirrored its own
            // transition (MirrorResume) and must NOT restamp StartedUtc.
            if (resumeCheckpoint is null)
            {
                ServerTaskLease.MirrorClaim(db, deployment, timeProvider);
            }

            // Renew the lease for as long as this dispatch is in flight — stops on
            // dispose at every exit path of this method. While the process is
            // alive (even parked on a long step or a future approval gate) the
            // reconciler sees a live lease and leaves the run alone.
            await using var leaseRenewal = new ServerTaskLeaseRenewal(
                scopeFactory, deployment.Id, timeProvider, logger, LeaseRenewIntervalOverride);

            // E2: if the lease is lost mid-orchestration — the reconciler failed
            // this run as orphaned (multi-minute stall), or it went terminal on
            // another connection — tear the orchestration down instead of
            // dispatching further waves LEASELESS. orchestrationCt links the
            // stopping token with the lease-lost signal; the wave loop below runs
            // on it, so an in-flight wave await is cancelled the moment the lease
            // goes. Capture the token up front so the teardown catch's `when`
            // filter never dereferences the renewal after it is disposed. Shutdown
            // (the stopping token) still propagates exactly as before.
            leaseLostToken = leaseRenewal.LeaseLost;
            using var orchestrationCts = CancellationTokenSource.CreateLinkedTokenSource(ct, leaseLostToken);
            var orchestrationCt = orchestrationCts.Token;

            var serverStepCount = waves
                .Where(w => w.Kind == WavePartitioner.WaveKind.Server)
                .Sum(w => w.Steps.Count);
            var targetStepCount = waves
                .Where(w => w.Kind == WavePartitioner.WaveKind.Target)
                .Sum(w => w.Steps.Count);
            logger.LogInformation(
                "Deployment {DeploymentId}: {Targets} target(s), {Waves} wave(s), " +
                "{ServerSteps} server step(s), {TargetSteps} target step(s), " +
                "{VarCount} variables (canonical bag).",
                deploymentId, targets.Count, waves.Count, serverStepCount, targetStepCount,
                canonicalCtx.FlatVars.Count);

            // M14.2 — orchestrator tracks `hasFailed` instead of returning on
            // first failure. Required steps still short-circuit; non-required
            // failures flip the flag and the loop continues so Failure / Always-
            // conditioned cleanup + finalisation steps still run. The
            // deployment's terminal status reflects the final state:
            // hasFailed → SucceededWithWarnings.
            //
            // M-RollingDeployments Phase 3 — `aliveTargets` tracks which
            // targets are still eligible for the next wave. A Required
            // failure OR an offline drop on target X removes X from this
            // list; subsequent waves run only against the survivors. When
            // every target has dropped, the deployment fails ("no progress
            // possible"); when some survived, it terminates as
            // SucceededWithWarnings even if all waves' agent-side calls
            // returned cleanly for the survivors.
            var failureMode = deployment.FailureMode;
            var hasFailed = false;
            var aliveTargets = new List<DeploymentTarget>(targets);
            var droppedTargets = new List<DroppedTargetInfo>();
            // BestEffort per-target soft-failure tracking: a non-required failure
            // on target X skips only X's later Condition=Success steps, never a
            // sibling's. (Atomic mode uses the global hasFailed flag instead.)
            var softFailedTargets = new HashSet<Guid>();
            // WP3 — a rejected/expired approval gate. Distinct from hasFailed: the run
            // continues past the gate so cleanup steps execute, but the verdict is a
            // hard Failed whatever the failure mode (see
            // DeploymentTerminalStatusResolver).
            var interventionRejected = false;
            // WP3 — the wave to start at. 0 for a fresh dispatch; the checkpoint's
            // resume point when continuing past an approved gate.
            var startWaveIndex = 0;

            // T0-6: value-based log redactor for server-side steps. Built once
            // from the canonical plan's sensitive values (identical across
            // targets — only substituted Configs differ, not which variables are
            // sensitive). B4: sensitive CAPTURED outputs are folded in live by
            // the output accumulator below, mirroring the agent's live fold.
            // Threaded into RunServerWaveAsync → ServerScriptStepRunner.
            var serverRedactor = SecretRedactor.ForPlan(canonicalCtx.Plan);

            // B4 (T0-4) — online cross-wave output propagation. Captured
            // outputs from each wave fold in here and augment every subsequent
            // dispatch (per-target sub-plan Variables + SensitiveVariableNames,
            // server-wave env view, Variable run-condition bags). Mirrors the
            // agent's within-dispatch accumulator so online == offline.
            var outputAccumulator = new DeploymentOutputAccumulator(
                contexts, canonicalCtx.VarDict, serverRedactor);

            // ── WP3: rehydrate the orchestration state a pause checkpointed ──
            // Everything above was REBUILT from the frozen snapshot (targets,
            // contexts, flatten, waves) — deterministic for a deployment. What
            // follows cannot be rebuilt and comes from the checkpoint.
            if (resumeCheckpoint is { } cp)
            {
                var restoreFailure = RestoreFromCheckpoint(
                    cp, waves, targets, aliveTargets, droppedTargets, softFailedTargets,
                    outputAccumulator);
                if (restoreFailure is not null)
                {
                    await logSeq.AppendAsync(-1, null, "error", restoreFailure, ct)
                        .ConfigureAwait(false);
                    await FailAsync(db, deployment, restoreFailure, ct).ConfigureAwait(false);
                    return;
                }
                hasFailed            = cp.HasFailed;
                interventionRejected = cp.InterventionRejected;
                startWaveIndex       = cp.ResumeWaveIndex;
                // NOTE: the checkpoint column was already cleared by TryResumeAsync's
                // conditional UPDATE (and mirrored onto the tracked entity). Clearing it
                // HERE instead would leave the tracked entity dirty with a stale xmin —
                // the resume's ExecuteUpdate bumped the row — and the wave loop's next
                // SaveChanges would throw DbUpdateConcurrencyException.
            }

            for (var waveIndex = startWaveIndex; waveIndex < waves.Count; waveIndex++)
            {
                var wave = waves[waveIndex];
                // ── Ownership boundary: stop unless still Running in the DB ──
                // E2 — ONE ownership predicate evaluated at every wave boundary:
                // the task must still be Running. This catches an operator cancel
                // (Status=Cancelled) AND a reconciler interrupt (Status=Failed,
                // flipped when the lease expired) — the pre-E2 check tested only
                // == Cancelled and let a reconciler-failed zombie keep dispatching.
                // B6's cooperative in-flight abort push is still the fast path;
                // this boundary check REMAINS the authoritative fallback (the push
                // is best-effort — agent offline, push lost — and even a killed
                // wave resolves its TCS through the normal failure path). Whatever
                // terminal verdict was recorded stands; the finalisation + FailAsync
                // writes below are guarded to never overwrite it. Runs on
                // orchestrationCt so a lost lease throws OCE here and the teardown
                // catch on this try stops the run cleanly.
                if (!await IsTaskStillRunningAsync(db, deployment.Id, orchestrationCt).ConfigureAwait(false))
                {
                    await logSeq.AppendAsync(-1, null, "warning",
                        "--- Deployment no longer Running (cancelled or interrupted) — stopping at " +
                        "the wave boundary. Any step already dispatched to an agent ran to " +
                        "completion; no further steps were started. ---",
                        ct).ConfigureAwait(false);
                    logger.LogInformation(
                        "Deployment {Id} no longer Running — halting before the remaining wave(s).",
                        deployment.Id);
                    return;
                }

                // ── WP3: manual-intervention gate ───────────────────────────
                // Evaluated BEFORE the wave dispatches, so the pause is task-global:
                // nothing in this wave has run yet and no target has been touched.
                var gateSteps = ManualInterventionGate.GateStepsIn(wave);
                if (gateSteps.Count > 0)
                {
                    var decision = await ManualInterventionGate.EvaluateAsync(
                        db, deployment, gateSteps, canonicalCtx.SnapshotByPlanIndex,
                        outputAccumulator.ServerConditionVarDict,
                        // Instructions render against a bag whose SENSITIVE VALUES are
                        // masked. They are persisted in cleartext and shown to holders of
                        // InterruptionView, who do not need VariableView — and redacting
                        // the rendered output cannot help, because any Octostache filter
                        // (| ToBase64, | Md5, …) yields a string the substring redactor
                        // no longer recognises. A filter cannot launder what was never in
                        // the bag. Conditions still see REAL values, on the line above.
                        outputAccumulator.MaskedServerConditionVarDict(
                            canonicalCtx.SensitiveVariableNames),
                        hasFailedAtWaveStart: hasFailed,
                        // Same role filter every other server step gets. A gate is a
                        // step, so a role-filtered one must skip, not pause.
                        appliesToTask: step => StepAppliesToTarget(deployment, step),
                        redactor: serverRedactor,
                        engineOptions.Value.DefaultInterventionTimeout,
                        timeProvider, orchestrationCt).ConfigureAwait(false);

                    // Gates their own run condition or role filter excluded, recorded on
                    // EVERY branch below — the whole gate set leaves the wave whichever
                    // branch fires, so an excluded gate sharing a wave with an applicable
                    // one used to vanish: no log line, no TaskStepOutcome, absent from
                    // the Steps tab, unlike every other skipped step type.
                    if (decision.SkippedSteps is { Count: > 0 } excludedGates)
                    {
                        await RecordSkippedStepsAsync(
                            db, logSeq, deployment, excludedGates,
                            canonicalCtx.SnapshotByPlanIndex,
                            what: "manual intervention",
                            decision.SkipReason ?? "Run condition excluded this gate.",
                            timeProvider, ct).ConfigureAwait(false);
                    }

                    if (decision.Action == ManualGateAction.Fail)
                    {
                        await logSeq.AppendAsync(-1, null, "error", decision.FailureReason!, ct)
                            .ConfigureAwait(false);
                        await FailAsync(db, deployment, decision.FailureReason!, ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (decision.Action == ManualGateAction.Pause)
                    {
                        await PauseForInterventionAsync(
                            db, deployment, source.Audit, auditLog, logSeq,
                            decision.Pending!, decision.ResponsibleTeamNames,
                            waveIndex, hasFailed, interventionRejected, aliveTargets,
                            droppedTargets, softFailedTargets, outputAccumulator,
                            encryption, ct).ConfigureAwait(false);
                        return;
                    }

                    // Resolved gates: log line + step outcome from the rows EvaluateAsync
                    // already loaded (no second query). The RESOLUTION audit is not
                    // written here — InterruptionService / the timeout sweeper emit it at
                    // decision time, which is when the change-control event actually
                    // happened and what subscriptions notify on; auditing again here
                    // would double-notify.
                    if (decision.Resolved is { Count: > 0 } answeredGates)
                    {
                        await RecordResolvedGatesAsync(
                            db, deployment, logSeq, answeredGates,
                            canonicalCtx.SnapshotByPlanIndex, timeProvider, ct)
                            .ConfigureAwait(false);
                    }

                    // Drop the whole gate set and run whatever else shared the wave.
                    // Because Octopus.Manual is server-only and a mixed server+target
                    // wave is refused upstream, the only possible companions are other
                    // SERVER steps (e.g. a RunOnServer script marked StartWithPrevious).
                    // The gate itself executes nothing.
                    var remainingSteps = wave.Steps.Except(gateSteps).ToList();

                    if (decision.Action == ManualGateAction.Rejected)
                    {
                        // Do NOT run this wave — the gate refused the work behind it.
                        // hasFailed makes later waves' Condition=Failure/Always cleanup
                        // steps run (and their Condition=Success steps skip);
                        // interventionRejected makes the terminal verdict Failed rather
                        // than SucceededWithWarnings. Deliberately NOT FailAsync here:
                        // that would return immediately and skip the cleanup.
                        //
                        // The companions are abandoned with the wave, so they need their
                        // Skipped outcome recorded explicitly — jumping straight to the
                        // next wave left them with no outcome row at all, while the same
                        // step one wave later would have run under Condition=Always.
                        if (remainingSteps.Count > 0)
                        {
                            await RecordSkippedStepsAsync(
                                db, logSeq, deployment, remainingSteps,
                                canonicalCtx.SnapshotByPlanIndex,
                                what: "step",
                                "Manual intervention was refused, so this wave did not run.",
                                timeProvider, ct).ConfigureAwait(false);
                        }
                        hasFailed = true;
                        interventionRejected = true;
                        continue;
                    }

                    if (remainingSteps.Count == 0)
                    {
                        continue;
                    }
                    wave = new WavePartitioner.Wave(wave.Kind, remainingSteps);
                }

                if (wave.Kind == WavePartitioner.WaveKind.Server)
                {
                    // ── Server wave ─────────────────────────────────────
                    // Server waves run ONCE, using the canonical (== first-
                    // assigned) target's variable bag for system + machine
                    // vars; the role filter (StepAppliesToTarget) passes when
                    // ANY assigned target matches. Server steps are
                    // deployment-scoped — DeployRelease cascade, manual
                    // interventions, … — so we deliberately preserve the
                    // single-execution semantic. Operators authoring server
                    // steps in a multi-target deployment see the canonical
                    // target's machine context (same as single-target).
                    // B4: server waves evaluate conditions against the
                    // accumulator's server bag (canonical clone + output keys)
                    // and receive an env view with prior outputs merged in.
                    // orchestrationCt: a lost lease cancels an in-flight server
                    // step (e.g. a DeployRelease child wait) so the run tears down
                    // instead of dispatching leaseless.
                    var serverOutcomes = await RunServerWaveAsync(
                        wave, canonicalCtx.SnapshotByPlanIndex, hasFailed,
                        outputAccumulator.ServerConditionVarDict, deployment, source.Audit, db, auditLog, logSeq,
                        outputAccumulator.AugmentServerVariables(canonicalCtx.FlatVars),
                        serverRedactor, orchestrationCt).ConfigureAwait(false);

                    // B4 (T1-6): fold server-step captures so later waves (agent
                    // AND server) see them, and persist through the same store —
                    // and encryption rules — the agent-report path uses.
                    foreach (var o in serverOutcomes)
                    {
                        if (o.CapturedOutputs is not { Count: > 0 } capturedOutputs)
                        {
                            continue;
                        }
                        var stepKey = o.Step.AccumulatorKey ?? o.Step.Name;
                        outputAccumulator.RecordServerStep(
                            stepKey, capturedOutputs, o.SensitiveOutputNames);
                        await TaskOutputVariableStore.UpsertAsync(
                            db, deployment.Id, deployment.SpaceId, stepKey,
                            capturedOutputs, o.SensitiveOutputNames,
                            DateTimeOffset.UtcNow, encryption, ct).ConfigureAwait(false);
                    }

                    var firstRequiredFailure = serverOutcomes.FirstOrDefault(o =>
                        !o.Skipped && !o.Ok && canonicalCtx.SnapshotByPlanIndex[o.Step.Index].Required);
                    if (firstRequiredFailure is not null)
                    {
                        await auditLog.RecordAsync(
                            source.Audit.RequiredStepFailed,
                            subjectType: source.Audit.SubjectType,
                            subjectId:   deployment.Id.ToString(),
                            details:     $"Step={firstRequiredFailure.Step.Name}",
                            ct: ct).ConfigureAwait(false);
                        await FailAsync(db, deployment,
                            $"Required step '{firstRequiredFailure.Step.Name}' failed.", ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    foreach (var nonReq in serverOutcomes.Where(o =>
                        !o.Skipped && !o.Ok && !canonicalCtx.SnapshotByPlanIndex[o.Step.Index].Required))
                    {
                        await LogAndAuditStepFailedNonRequiredAsync(
                            db, auditLog, logSeq, deployment, source.Audit,
                            canonicalCtx.SnapshotByPlanIndex[nonReq.Step.Index], ct).ConfigureAwait(false);
                        hasFailed = true;
                    }
                }
                else
                {
                    // ── Target wave: fan out per target ─────────────────
                    // Every target dispatches its own sub-plan in parallel,
                    // each with its per-target variable bag. The agent runs
                    // the wave's steps in parallel (M14.4 per-step boundary
                    // reports) and reports back to its target-specific
                    // registry slot. After Task.WhenAll: persist per-(target,
                    // step) outcomes, emit per-target collision audits, then
                    // apply the cross-target Required gate (first Required
                    // failure on ANY target aborts the deployment —
                    // conservative Phase 1b; per-target drop-out is Phase 3).
                    // Phase 3 — dispatch the wave against the CURRENTLY-alive
                    // targets. Returns drop-outs (per-target Required failures
                    // + agent-offline at dispatch time); the caller removes
                    // them from aliveTargets and continues.
                    // orchestrationCt: a lost lease cancels an in-flight target
                    // wave await AND the between-batch ownership check, tearing the
                    // run down instead of dispatching further batches leaseless.
                    var targetWaveResult = await DispatchTargetWaveAcrossTargetsAsync(
                        wave, aliveTargets, contexts, canonicalCtx.SnapshotByPlanIndex,
                        snapshotById, failureMode, hasFailed, softFailedTargets, deployment, source.Audit,
                        db, auditLog, logSeq, outputAccumulator, orchestrationCt).ConfigureAwait(false);

                    foreach (var dropped in targetWaveResult.DroppedTargets)
                    {
                        await EmitTargetDroppedAsync(
                            db, auditLog, logSeq, deployment, source.Audit, dropped,
                            wave, ct).ConfigureAwait(false);
                        aliveTargets.Remove(dropped.Target);
                        droppedTargets.Add(dropped);
                    }

                    // Record per-target soft failures so each target's own later
                    // Condition=Success steps skip (BestEffort isolation).
                    foreach (var id in targetWaveResult.SoftFailedTargetIds)
                    {
                        softFailedTargets.Add(id);
                    }

                    // Promote to the deployment-global failing state — which skips
                    // Condition=Success and runs Condition=Failure/Always on EVERY
                    // surviving target in later waves — only in Atomic mode, and on
                    // ANY failure (a Required drop OR a soft failure): one target's
                    // failure fails the whole deployment so a half-applied change is
                    // cleaned up / rolled back farm-wide. In BestEffort mode a
                    // target failure stays local (survivors keep deploying) and the
                    // global flag is reserved for deployment-level/server failures.
                    if (failureMode == DeploymentFailureMode.Atomic
                        && (targetWaveResult.DroppedTargets.Count > 0
                            || targetWaveResult.SoftFailedTargetIds.Count > 0))
                    {
                        hasFailed = true;
                    }

                    if (aliveTargets.Count == 0)
                    {
                        // No survivors — the deployment can't progress.
                        // Audit the Required-step-failed signal at the
                        // deployment level too so the existing dashboards
                        // (which read DeploymentRequiredStepFailed) still
                        // light up; drop-out audits are richer but the
                        // legacy signal stays compatible.
                        var lastDrop = droppedTargets.LastOrDefault();
                        await auditLog.RecordAsync(
                            source.Audit.RequiredStepFailed,
                            subjectType: source.Audit.SubjectType,
                            subjectId:   deployment.Id.ToString(),
                            details:     $"AllTargetsDropped={droppedTargets.Count}, " +
                                         $"LastDrop=Target={lastDrop?.Target.Name}/" +
                                         $"Reason={lastDrop?.Reason}/" +
                                         $"Step={lastDrop?.StepName}",
                            ct: ct).ConfigureAwait(false);
                        await FailAsync(db, deployment,
                            $"All {droppedTargets.Count.ToString(CultureInfo.InvariantCulture)} target(s) " +
                            "dropped out (Required step failure or agent offline). " +
                            "Deployment cannot continue without any surviving targets.", ct)
                            .ConfigureAwait(false);
                        return;
                    }
                }
            }

            // ── M14.2 + Phase 3 finalisation ────────────────────────────
            // Reached only when at least one target survived (the all-dropped
            // case failed earlier). Terminal status is mode-aware: in Atomic mode
            // a Required-step failure on any target is a hard Failed; in BestEffort
            // a partial drop / soft failure with survivors completing is
            // SucceededWithWarnings (the yellow-badge state). See
            // DeploymentTerminalStatusResolver.
            var terminalStatus = DeploymentTerminalStatusResolver.Resolve(
                failureMode,
                hasFailed,
                requiredStepDropped: droppedTargets.Any(d => d.Reason == DropReason.RequiredStepFailed),
                droppedTargetCount:  droppedTargets.Count,
                softFailedCount:     softFailedTargets.Count,
                // WP3 — a rejected/expired gate is Failed in every mode, but only
                // AFTER the cleanup waves above have run.
                interventionRejected: interventionRejected);
            var didSucceed = false;
            DateTimeOffset finalCompletedUtc;
            await using (var finalDb = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
                .CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var d = await finalDb.ServerTasks.FindAsync([deployment.Id], ct).ConfigureAwait(false);
                finalCompletedUtc = DateTimeOffset.UtcNow;
                // Never overwrite a TERMINAL status. A concurrent CancelAsync may
                // have moved this deployment to Cancelled while the final wave
                // completed (the agent protocol can't abort in-flight work), and —
                // B1 — the dispatch reconciler may have failed it as interrupted
                // if this process stalled past the whole lease. The recorded
                // verdict wins; a resumed zombie dispatch must not report success.
                // B5: the guarded writer makes that check-then-save atomic — a
                // cancel landing inside the old window can no longer be clobbered.
                if (d is not null)
                {
                    var wrote = await ServerTaskStatusWriter.TryTransitionAsync(
                        finalDb, d, dep =>
                        {
                            dep.Status       = terminalStatus;
                            dep.CompletedUtc = finalCompletedUtc;
                            // B1: terminal — release the dispatch lease.
                            dep.ClaimedBy    = null;
                            dep.LeaseUntil   = null;
                            // WP3: a terminal task never carries a resume checkpoint.
                            dep.PauseCheckpointEncrypted = null;
                        }, ct: ct).ConfigureAwait(false);
                    didSucceed = wrote && terminalStatus is DeploymentStatus.Succeeded
                        or DeploymentStatus.SucceededWithWarnings;
                }
            }

            // ── Slow-deployment audit (M13.F.3) ──────────────────────────
            // Emit a Deployment.Slow audit event when the run exceeded
            // the configured threshold so M13.B.2/3 subscribers can route
            // a notification (webhook / email / runbook / AI inspection).
            // Threshold = 0 disables.
            await EmitSlowDeploymentAuditIfNeededAsync(
                scope.ServiceProvider, deployment, source.Audit, finalCompletedUtc, ct).ConfigureAwait(false);

            // ── Phase 3 — per-target slow audit ──────────────────────────
            // Each target's effective duration (max CompletedUtc − min
            // StartedUtc across its TaskStepOutcome rows) is
            // compared against the same threshold; one
            // Deployment.TargetSlow audit per slow target. Operators can
            // pinpoint which specific machine slowed a multi-target run,
            // even when the deployment as a whole stayed under threshold.
            await EmitTargetSlowAuditsIfNeededAsync(
                scope.ServiceProvider, deployment, source.Audit, ct).ConfigureAwait(false);

            // ── Per-step slow audit (M13.F.3) ────────────────────────────
            // Each TaskStepOutcome's own duration is compared against
            // SlowStepThresholdMinutes; one DeploymentStep.Slow audit per slow
            // step. Threshold = 0 disables. (Previously the knob was persisted
            // + editable but nothing ever emitted the event — now wired.)
            await EmitSlowStepAuditsIfNeededAsync(
                scope.ServiceProvider, deployment, source.Audit, ct).ConfigureAwait(false);

            // Terminal: fold any remaining live staging log lines (server-side
            // banners/steps, unreported steps) into per-step blobs. Agent per-step
            // compaction already handled target steps as they finished. Own scope
            // so it never touches the dispatch's main context.
            await using (var compactScope = scopeFactory.CreateAsyncScope())
            {
                var compactDb = compactScope.ServiceProvider.GetRequiredService<KrakenDbContext>();
                await TaskLogService.CompactRemainingAsync(
                    compactDb, deployment.Id, finalCompletedUtc, ct).ConfigureAwait(false);
            }

            // Retention pruning. The orchestrated task finalises HERE — it never
            // reaches any hub-side retention trigger, because each target's
            // completion resolves via the sub-plan registry and early-returns
            // in the hub. Fire only on a successful terminal status.
            // Fire-and-forget with its own scope + internal try/catch so a
            // retention error never fails the task. D1: KIND-BRANCHED keep
            // source — a deployment prunes by lifecycle phase, a runbook run by
            // its fixed keep per (runbook, environment). Both kinds finalise
            // through this orchestrator (the hub never finalizes — Phase 3), so
            // the worker owns retention for both — passing the wrong kind here
            // would silently kill runbook retention.
            if (didSucceed)
            {
                _ = PruneRetentionAsync(source.Kind, deployment.Id);
            }

            logger.LogInformation(
                "Deployment {Id} completed ({ServerSteps} server step(s), {TargetSteps} target step(s)).",
                deployment.Id, serverStepCount, targetStepCount);
        }
        catch (OperationCanceledException)
            when (leaseLostToken.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // E2 lease-loss teardown: the in-flight lease was lost (reconciler
            // orphan-fail or a terminal transition on another connection), which
            // cancelled orchestrationCt and unwound the wave loop. Stop WITHOUT
            // finalising — the reconciler owns the terminal verdict of a run whose
            // lease it reclaimed; writing one here would race that verdict (and the
            // status writer would refuse it anyway). Distinct from shutdown (the
            // stopping token), which is excluded by the `when` filter and
            // propagates to the host as before.
            logger.LogWarning(
                "Deployment {Id}: dispatch lease lost mid-orchestration; tearing down without " +
                "finalising (the reconciler owns the terminal verdict).",
                deploymentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Unhandled error dispatching task {DeploymentId}.", deploymentId);

            await using var errorScope = scopeFactory.CreateAsyncScope();
            var errorDb = errorScope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            // Fresh scope → DefaultSpaceId; load filter-free via the base set (the
            // TPH subtype materialises, so Kind is set) so a non-Default-Space task
            // of EITHER kind is still found, then scope FailAsync to its Space. AI
            // diagnosis is deployment-only.
            var errTask = await errorDb.ServerTasks.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == deploymentId, ct).ConfigureAwait(false);
            if (errTask is not null)
            {
                using var _ = errorScope.ServiceProvider
                    .GetRequiredService<ISpaceContext>().WithSpace(errTask.SpaceId);
                await FailAsync(errorDb, errTask, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Retention prune after a successful orchestrated task. D1: KIND-BRANCHED —
    /// a deployment prunes by its lifecycle phase's keep window, a runbook run by
    /// its fixed keep per (runbook, environment). Opens its own DI scope (the
    /// caller's dispatch scope may be torn down before this fire-and-forget
    /// completes) and swallows errors so retention never fails the task. Mirrors
    /// <c>AgentHub.PruneRetentionAsync</c>.
    /// </summary>
    private async Task PruneRetentionAsync(ServerTaskKind kind, Guid taskId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            if (kind == ServerTaskKind.RunbookRun)
            {
                await retention.PruneAfterRunbookRunAsync(taskId).ConfigureAwait(false);
            }
            else
            {
                await retention.PruneAfterDeploymentAsync(taskId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error running retention pruning for orchestrated task {Id}.", taskId);
        }
    }

    // ── Offline drop ─────────────────────────────────────────────────────

    private async Task DispatchOfflineDropAsync(
        IServiceProvider sp, KrakenDbContext db, Deployment deployment,
        DeploymentTarget target, CancellationToken ct)
    {
        string bundlePath;
        try
        {
            // Single source of truth for the plan build + pre-flight gates +
            // bundle write, shared with the UI/API regenerate path
            // (OfflineDropBundleBuilder). Precondition failures throw so the
            // dispatch path can map them to the terminal Failed status below.
            bundlePath = await offlineBundleBuilder
                .GenerateOfflineBundleAsync(sp, deployment, target, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Pre-flight refusals (no snapshot, missing bundle key,
            // server-orchestrated steps, unresolved required ForEach) are
            // terminal for a dispatch — mark Failed, mirroring the online path.
            // FailAsync derives AI diagnosis from the task kind (deployment here).
            await FailAsync(db, deployment, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        var dataPath = sp.GetRequiredService<IConfiguration>()["Server:DataPath"] ?? "data";

        // A cancel may have landed while the bundle was being built — don't
        // resurrect the deployment to PendingOfflineResult over a Cancelled
        // row. B5: the guarded writer checks and writes atomically (the old
        // read-then-save left a race window), and its default guard also
        // refuses a row the reconciler failed as interrupted meanwhile. On
        // refusal the built bundle is simply orphaned on disk (DropBundlePath
        // stays unset). The reload inside the writer is what makes this write
        // possible at all under the xmin token — the tracked entity's token
        // went stale the moment the B1 claim ran.
        var parked = await ServerTaskStatusWriter.TryTransitionAsync(
            db, deployment, d =>
            {
                d.DropBundlePath = bundlePath;
                d.Status = DeploymentStatus.PendingOfflineResult;
                d.StartedUtc = DateTimeOffset.UtcNow;
                // B1: the dispatch parks here awaiting an out-of-band offline
                // result — no longer worker-owned, so release the lease (the
                // reconciler ignores non-Running rows anyway; this is hygiene).
                d.ClaimedBy = null;
                d.LeaseUntil = null;
            }, ct: ct).ConfigureAwait(false);
        if (!parked)
        {
            logger.LogInformation(
                "DeploymentWorker: deployment {Id} reached a terminal state ({Status}) during " +
                "the offline-bundle build; not marking PendingOfflineResult.",
                deployment.Id, deployment.Status);
            return;
        }

        logger.LogInformation(
            "Offline drop bundle generated for deployment {DeploymentId}: {Path}.",
            deployment.Id, bundlePath);

        // Attempt delivery if configured (non-Manual).
        await DeliverDropBundleAsync(deployment, target, dataPath, ct).ConfigureAwait(false);
    }

    private async Task DeliverDropBundleAsync(
        Deployment deployment, DeploymentTarget target, string dataPath, CancellationToken ct)
    {
        var deliveryChannel = target.OfflineDropConfig?.DeliveryChannel
            ?? OfflineDropDeliveryChannel.Manual;

        if (deliveryChannel == OfflineDropDeliveryChannel.Manual ||
            string.IsNullOrEmpty(deployment.DropBundlePath))
        {
            return; // User will download manually from the UI
        }

        try
        {
            switch (deliveryChannel)
            {
                case OfflineDropDeliveryChannel.Webhook:
                    await DeliverViaWebhookAsync(deployment, target, dataPath, ct).ConfigureAwait(false);
                    break;
                case OfflineDropDeliveryChannel.FileShareDrop:
                    await DeliverViaFileShareAsync(deployment, target, dataPath, ct).ConfigureAwait(false);
                    break;
                case OfflineDropDeliveryChannel.Email:
                    logger.LogWarning(
                        "Email delivery not yet implemented for deployment {Id}. " +
                        "Bundle is available for manual download.",
                        deployment.Id);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Delivery failure is non-fatal — the bundle is still available for download.
            logger.LogError(ex,
                "Failed to deliver drop bundle for deployment {Id} via {Channel}. " +
                "Bundle is available for manual download.",
                deployment.Id, deliveryChannel);
        }
    }

    private async Task DeliverViaWebhookAsync(
        Deployment deployment, DeploymentTarget target, string dataPath, CancellationToken ct)
    {
        var webhookUrl = target.OfflineDropConfig?.WebhookUrl;
        if (string.IsNullOrEmpty(webhookUrl))
        {
            return;
        }

        var bundleFullPath = Path.Combine(
            dataPath,
            deployment.DropBundlePath!.Replace('/', Path.DirectorySeparatorChar));

        using var httpClient = new HttpClient();
        await using var stream = new FileStream(
            bundleFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Headers.Add("X-Kraken-Deployment-Id", deployment.Id.ToString());

        var response = await httpClient.PostAsync(webhookUrl, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        logger.LogInformation(
            "Drop bundle for deployment {Id} delivered via webhook to {Url}.",
            deployment.Id, webhookUrl);
    }

    private async Task DeliverViaFileShareAsync(
        Deployment deployment, DeploymentTarget target, string dataPath, CancellationToken ct)
    {
        var targetPath = target.OfflineDropConfig?.FileSharePath;
        if (string.IsNullOrEmpty(targetPath))
        {
            return;
        }

        var bundleFullPath = Path.Combine(
            dataPath,
            deployment.DropBundlePath!.Replace('/', Path.DirectorySeparatorChar));

        var destDir = Path.Combine(targetPath, deployment.Id.ToString());
        Directory.CreateDirectory(destDir);
        var destFile = Path.Combine(destDir, Path.GetFileName(bundleFullPath));

        await using var source = new FileStream(
            bundleFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var dest = new FileStream(
            destFile, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(dest, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Drop bundle for deployment {Id} copied to file share: {Path}.",
            deployment.Id, destFile);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // M14.4: Partition + IsServerStep moved to WavePartitioner so the wave
    // walker can be tested in isolation. Use WavePartitioner.IsServerStep
    // when classifying outside the wave path.

    /// <summary>
    /// Dispatches one server-side step to the appropriate runner based on its
    /// <see cref="DeploymentStepPlan.StepType"/>. Orchestrator step types (like
    /// <c>Octopus.DeployRelease</c>) route to a dedicated runner; everything
    /// else falls through to the generic <see cref="ServerScriptStepRunner"/>.
    /// </summary>
    private async Task<ServerScriptResult> ExecuteServerStepAsync(
        Guid deploymentId,
        DeploymentStepPlan step,
        IReadOnlyDictionary<string, string> flatVars,
        Guid spaceId,
        SecretRedactor redactor,
        CancellationToken ct)
    {
        // WP3 — a manual-intervention gate is decided at the wave boundary and its
        // steps are stripped from the wave before it runs, so reaching here means the
        // gate was bypassed. Throw rather than fall through to ServerScriptStepRunner,
        // which would try to execute an approval step as a script.
        if (step.StepType.Equals(
                ManualInterventionConfigKeys.StepType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Manual-intervention step '{step.Name}' reached the server step runner. " +
                "Gate steps are resolved at the wave boundary and must never be executed.");
        }

        // Overlay this step's per-step variable delta (step/action scope) onto
        // the deployment-wide vars — the server-side counterpart of the agent's
        // ApplyStepVariables. No-op when the step carries no delta.
        var effectiveVars = OverlayStepVariables(flatVars, step);
        if (step.StepType.Equals(DeployReleaseStepRunner.StepType, StringComparison.OrdinalIgnoreCase))
        {
            // DeployRelease creates a CHILD deployment that must inherit the
            // parent's Space — thread spaceId so CreateAsync stamps it and the
            // child-log polling resolves in the right Space. (The runner opens
            // its own DI scope, so the worker's WithSpace doesn't reach it.)
            // DeployRelease captures no output variables (B4).
            var ok = await deployReleaseRunner
                .ExecuteAsync(deploymentId, step, effectiveVars, spaceId, ct)
                .ConfigureAwait(false);
            return ok
                ? new ServerScriptResult(true, new Dictionary<string, string>(), [])
                : ServerScriptResult.Failure;
        }
        // ServerScriptStepRunner only writes deployment-log rows and scopes those
        // short-lived writes itself (IgnoreQueryFilters + explicit SpaceId stamp),
        // so it needs no Space threading. The redactor masks sensitive values in
        // every log line the runner emits (T0-6). DeployRelease steps route to a
        // child deployment whose own worker run redacts its logs, so they don't
        // take the redactor here.
        return await serverRunner
            .ExecuteAsync(deploymentId, step, effectiveVars, redactor, ct)
            .ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> OverlayStepVariables(
        IReadOnlyDictionary<string, string> baseVars, DeploymentStepPlan step)
    {
        if (step.StepVariables is not { Count: > 0 } delta)
        {
            return baseVars;
        }

        var merged = new Dictionary<string, string>(baseVars, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in delta)
        {
            // Server-side flatVars hold StringArrays as their comma-joined form,
            // so split the delta's raw JSON the same way for consistency.
            if (v.StartsWith('[') && TryParseStringArray(v, out var items))
            {
                merged[k] = string.Join(", ", items);
            }
            else
            {
                merged[k] = v;
            }
        }
        return merged;
    }

    private static bool TryParseStringArray(string value, out string[] items)
    {
        try
        {
            items = JsonSerializer.Deserialize<string[]>(value) ?? [];
            return true;
        }
        catch (JsonException)
        {
            items = [];
            return false;
        }
    }

    /// <summary>
    /// "Run on Server on behalf of each deployment target" role filtering:
    /// when a server step has <c>TargetRoles</c>, only execute it if ANY of
    /// the deployment's assigned targets has at least one of those roles
    /// (server steps run once per deployment, so one qualifying target in
    /// the set is enough). A server step without roles always applies (it's
    /// a pure "Run on Server" step). Requires the Targets join loaded.
    /// </summary>
    private static bool StepAppliesToTarget(ServerTask deployment, DeploymentStepPlan step)
    {
        if (step.TargetRoles is null || step.TargetRoles.Count == 0)
        {
            return true;
        }
        return deployment.ResolvedTargets().Any(t =>
            step.TargetRoles.Any(r =>
                t.Roles.Contains(r, StringComparer.OrdinalIgnoreCase)));
    }

    // M15.2: SubstituteConfig moved into DeploymentPlanFlattener so it
    // can run per-ForEach-iteration with the right variable bag. The
    // orchestrator no longer pre-substitutes the snapshot's Config.

    // E2 — the single ownership predicate: is this task STILL Running in the DB?
    // Evaluated at the wave, rolling-batch and dispatch boundaries; a false stops
    // the orchestration cleanly. A cancel (Status=Cancelled) OR a reconciler
    // interrupt (Status=Failed, flipped when the lease expired) both flip it to a
    // non-Running status, so both are caught here — the pre-E2 predicate tested
    // only == Cancelled and let a reconciler-failed zombie keep dispatching.
    //
    // Read via a fresh scalar projection, not the tracked entity: the worker's
    // shared db still tracks the row as Running (the B1 MirrorClaim), but a
    // projection always executes SQL and returns the authoritative DB value.
    // Typed against db.ServerTasks (not db.Deployments) so it stays correct once
    // D1 generalises the orchestrator to the unified task type — a Deployment is
    // a ServerTask, so this reads the same row today.
    private static async Task<bool> IsTaskStillRunningAsync(
        KrakenDbContext db, Guid taskId, CancellationToken ct)
        => await db.ServerTasks
            .Where(t => t.Id == taskId)
            .Select(t => t.Status)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) == DeploymentStatus.Running;

    // D1: operates on the ServerTask base so both kinds finalise through one path.
    // AI diagnosis is deployment-only — derived HERE from task.Kind (the SINGLE
    // source, correct in every calling context: main dispatch, the error catch,
    // and the offline path) rather than a threaded flag. `reason` is recorded to
    // the ops log for failure correlation.
    private async Task FailAsync(
        KrakenDbContext db, ServerTask task, string reason, CancellationToken ct)
    {
        // Never overwrite a TERMINAL status with Failed. A CancelAsync landing
        // while a wave was in flight makes Cancelled the terminal state, and —
        // B1 — the dispatch reconciler may already have failed this run as
        // interrupted; the recorded verdict wins and, for the reconciler case,
        // a duplicate Failed write + AI diagnosis is avoided. B5: the guarded
        // writer also cures the tracked entity's stale xmin (lease renewals +
        // log-sequence bumps churn the row constantly while a dispatch runs)
        // and closes the old check-then-save race window atomically.
        var failed = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task, static t =>
            {
                t.Status = DeploymentStatus.Failed;
                t.CompletedUtc = DateTimeOffset.UtcNow;
                // B1: terminal — release the dispatch lease.
                t.ClaimedBy = null;
                t.LeaseUntil = null;
                // WP3: a terminal task never carries a resume checkpoint.
                t.PauseCheckpointEncrypted = null;
            }, ct: ct).ConfigureAwait(false);
        if (!failed)
        {
            // The recorded verdict (cancel / reconciler interrupt) stands — this
            // call didn't flip the status, so don't announce a failure it didn't
            // cause.
            return;
        }

        // Ops-log correlation: record WHY the task failed (the task log gets the
        // operator-facing detail from the callers that pre-append it).
        logger.LogWarning("Task {Id} ({Kind}) failed: {Reason}", task.Id, task.Kind, reason);

        // M11.C — queue an AI diagnosis, but only for DEPLOYMENTS that actually
        // started executing. Pre-flight refusals (no target, no variable snapshot,
        // blocked by freeze, agent offline at dispatch) set Failed before
        // StartedUtc is stamped; diagnosing "it never ran" wastes AI budget +
        // produces no useful analysis. Runbook runs have no diagnosis worker at
        // all. Best-effort TryWrite on an unbounded channel — never blocks
        // finalisation.
        if (task.Kind == ServerTaskKind.Deployment && task.StartedUtc is not null)
        {
            diagnosisChannel.Writer.TryWrite(new TenantWorkItem(_dispatchAccountId.Value, task.Id));
        }
    }

    // ── WP3 manual-intervention helpers ──────────────────────────────────

    /// <summary>
    /// Parks the task at a manual-intervention gate: writes the resume checkpoint and
    /// flips <c>Running → Paused</c> in ONE transaction together with the staged
    /// <see cref="Interruption"/> row, then returns so the caller's <c>using</c> scopes
    /// release the <c>NodeTaskGate</c> slot, the in-flight gauge and the lease renewal.
    /// <para>
    /// The lease is CLEARED, not left to expire: a paused task has no owner, and B1's
    /// reconciler must not see a stale live lease on it. That is safe only because the
    /// reconciler's orphan predicate is scoped to <c>Running</c> — a <c>Paused</c> row
    /// with a null lease is invisible to it, where a <c>Running</c> one would be reaped
    /// within the minute.
    /// </para>
    /// </summary>
    private async Task PauseForInterventionAsync(
        KrakenDbContext db,
        ServerTask task,
        TaskAuditVocabulary vocab,
        IAuditLog auditLog,
        LogSequencer logSeq,
        Interruption interruption,
        IReadOnlyList<string>? responsibleTeamNames,
        int waveIndex,
        bool hasFailed,
        bool interventionRejected,
        IReadOnlyList<DeploymentTarget> aliveTargets,
        IReadOnlyList<DroppedTargetInfo> droppedTargets,
        IReadOnlySet<Guid> softFailedTargets,
        DeploymentOutputAccumulator outputAccumulator,
        KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService encryption,
        CancellationToken ct)
    {
        var (targetOutputs, serverOutputs) = outputAccumulator.Export();
        var checkpoint = new TaskPauseCheckpoint
        {
            ResumeWaveIndex      = waveIndex,
            HasFailed            = hasFailed,
            // Without this a rejection recorded on an EARLIER wave is lost across this
            // pause, and the run finalises SucceededWithWarnings after the next gate is
            // approved — a refused change reported as a warning-level success.
            InterventionRejected = interventionRejected,
            AliveTargetIds      = [.. aliveTargets.Select(t => t.Id)],
            DroppedTargets      = [.. droppedTargets.Select(d => new CheckpointDroppedTarget(
                                      d.Target.Id, d.Reason.ToString(), d.StepName, d.Error))],
            SoftFailedTargetIds = [.. softFailedTargets],
            TargetOutputs       = [.. targetOutputs],
            ServerOutputs       = [.. serverOutputs],
        };
        var payload = TaskPauseCheckpointCodec.Write(checkpoint, encryption);

        // The operator-facing banner goes in BEFORE the status flip so a UI that
        // reacts to Paused already finds the explanation in the log.
        await logSeq.AppendAsync(-1, null, "info",
            ManualInterventionGate.DescribePause(interruption, responsibleTeamNames),
            ct).ConfigureAwait(false);

        // Guard on Running specifically (not merely "not terminal"): a concurrent
        // cancel must win, and a task that is somehow already Paused must not be
        // re-paused over its existing gate.
        var paused = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task,
            t =>
            {
                t.Status = DeploymentStatus.Paused;
                // No owner while parked — see the remarks above.
                t.ClaimedBy  = null;
                t.LeaseUntil = null;
                t.PauseCheckpointEncrypted = payload;
            },
            canTransitionFrom: static s => s == DeploymentStatus.Running,
            ct: ct).ConfigureAwait(false);

        if (!paused)
        {
            // A cancel (or a reconciler interrupt) landed first; its verdict stands and
            // the staged interruption row is discarded unsaved with this scope.
            logger.LogInformation(
                "Task {Id}: manual-intervention pause refused — status is {Status}; the " +
                "recorded verdict stands.", task.Id, task.Status);
            return;
        }

        logger.LogInformation(
            "Task {Id} paused at wave {Wave} for manual intervention '{Step}' " +
            "(interruption {InterruptionId}); node slot released.",
            task.Id, waveIndex, interruption.StepName, interruption.Id);

        await auditLog.RecordAsync(
            vocab.Paused,
            subjectType: vocab.SubjectType,
            subjectId:   task.Id.ToString(),
            details:     $"InterruptionId={interruption.Id}, Step={interruption.StepName}, " +
                         $"StepIndex={interruption.StepIndex.ToString(CultureInfo.InvariantCulture)}, " +
                         $"ResponsibleTeams=[{(responsibleTeamNames is { Count: > 0 }
                             ? string.Join(", ", responsibleTeamNames)
                             : "anyone with the approve permission")}], " +
                         $"ExpiresUtc={interruption.ExpiresUtc?.ToString("O") ?? "<none>"}",
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the task-log line + step outcome for each gate on a wave whose decision
    /// is already in. Runs for approvals AND rejections — the Steps tab must show what
    /// the human decided either way, which is the point of the change-control trail.
    /// The resolution AUDIT is emitted at decision time by the interruption service /
    /// timeout sweeper, not here, so subscriptions fire once.
    /// </summary>
    private static async Task RecordResolvedGatesAsync(
        KrakenDbContext db,
        ServerTask task,
        LogSequencer logSeq,
        IReadOnlyList<Interruption> resolved,
        StepSnapshot[] snapshotSteps,
        TimeProvider time,
        CancellationToken ct)
    {
        // The rows come from ManualInterventionGate.EvaluateAsync, which already
        // loaded them on this same context — re-querying here bought nothing but a
        // round trip and handed back the same tracked instances.
        var now = time.GetUtcNow();
        foreach (var interruption in resolved.OrderBy(i => i.StepIndex))
        {
            // StepIndex comes from the DB and indexes an array REBUILT at resume, so it
            // is not self-evidently in range: RestoreFromCheckpoint accepts that the
            // process may no longer partition the way it did at pause time. Refuse with
            // an operator-readable failure rather than throwing IndexOutOfRangeException
            // out of the wave loop into the generic dispatch catch.
            if (interruption.StepIndex < 0 || interruption.StepIndex >= snapshotSteps.Length)
            {
                throw new InvalidOperationException(
                    $"Manual intervention '{interruption.StepName}' was recorded at step " +
                    $"index {interruption.StepIndex}, which no longer exists in this task's " +
                    $"process ({snapshotSteps.Length} step(s)). The process changed while the " +
                    "task was paused — re-deploy the release instead of resuming it.");
            }
            var snap = snapshotSteps[interruption.StepIndex];
            var verdict = interruption.Status switch
            {
                InterruptionStatus.Approved => "APPROVED",
                InterruptionStatus.Rejected => "REJECTED",
                InterruptionStatus.TimedOut => "TIMED OUT",
                // Unreachable: the gate's allow-list refuses Pending and Cancelled before
                // any outcome is recorded. Named rather than defaulted so a status added
                // later cannot silently be logged as a timeout.
                var other                   => other.ToString().ToUpperInvariant(),
            };
            var by = interruption.ActedByDisplay ?? "<no responder>";
            var notes = string.IsNullOrWhiteSpace(interruption.Notes)
                ? "" : $" Notes: {interruption.Notes}";

            await logSeq.AppendAsync(interruption.StepIndex, null,
                interruption.Status == InterruptionStatus.Approved ? "info" : "error",
                $"--- Manual intervention '{interruption.StepName}' {verdict} by {by}." +
                $"{notes} ---", ct).ConfigureAwait(false);

            await UpsertStepOutcomeAsync(
                db, task.Id, interruption.StepIndex, snap.Name,
                ManualInterventionGate.OutcomeFor(interruption.Status),
                attemptCount: 1,
                errorMessage: interruption.Status == InterruptionStatus.Approved
                    ? null
                    : $"Manual intervention {verdict.ToLowerInvariant()}" +
                      (string.IsNullOrWhiteSpace(interruption.Notes)
                          ? "." : $": {interruption.Notes}"),
                startedUtc:   interruption.CreatedUtc,
                completedUtc: interruption.ActedUtc ?? now,
                isServerSide: true,
                required:     snap.Required, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a <c>Skipped</c> outcome + log line for steps the gate block removed
    /// from a wave without running them, so they behave like every other skipped step
    /// instead of silently vanishing from the Steps tab. Two callers, both in the gate
    /// block: gates their own run condition or role filter excluded
    /// (<paramref name="what"/> = "manual intervention"), and the non-gate companions of
    /// a wave abandoned because a gate was refused (<paramref name="what"/> = "step").
    /// <para>
    /// No <see cref="Interruption"/> row is created and nothing is audited: a step that
    /// did not run was never a change-control question, so putting it in front of a
    /// reviewer would be noise.
    /// </para>
    /// <para>
    /// Idempotent — <c>UpsertStepOutcomeAsync</c> overwrites in place, which matters
    /// because a pause/resume cycle re-evaluates the wave and re-reports the same
    /// excluded set.
    /// </para>
    /// </summary>
    private static async Task RecordSkippedStepsAsync(
        KrakenDbContext db,
        LogSequencer logSeq,
        ServerTask task,
        IReadOnlyList<DeploymentStepPlan> skippedSteps,
        StepSnapshot[] snapshotSteps,
        string what,
        string reason,
        TimeProvider time,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var step in skippedSteps)
        {
            var snap = snapshotSteps[step.Index];
            await logSeq.AppendAsync(step.Index, null, "info",
                $"--- Skipped {what} '{snap.Name}': {reason} ---", ct)
                .ConfigureAwait(false);
            await UpsertStepOutcomeAsync(
                db, task.Id, step.Index, snap.Name,
                StepOutcomeKind.Skipped, attemptCount: 0,
                errorMessage: reason,
                startedUtc:   null,
                completedUtc: now,
                isServerSide: true,
                required:     snap.Required, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-seeds the wave loop's mutable state from a pause checkpoint. Returns
    /// <c>null</c> on success, or an operator-readable reason the resume must FAIL.
    /// <para>
    /// Both invariant checks matter. A <c>ResumeWaveIndex</c> outside the rebuilt wave
    /// list means the process snapshot no longer partitions the way it did at pause
    /// time, so "continue from wave N" is meaningless. An alive target id that is no
    /// longer assigned means the target set changed under the pause. Either way,
    /// continuing would deploy something other than what was approved — so it fails
    /// loudly instead (pre-production policy: no soft-fallback for stale state).
    /// </para>
    /// </summary>
    private static string? RestoreFromCheckpoint(
        TaskPauseCheckpoint checkpoint,
        List<WavePartitioner.Wave> waves,
        IReadOnlyList<DeploymentTarget> targets,
        List<DeploymentTarget> aliveTargets,
        List<DroppedTargetInfo> droppedTargets,
        HashSet<Guid> softFailedTargets,
        DeploymentOutputAccumulator outputAccumulator)
    {
        if (checkpoint.ResumeWaveIndex < 0 || checkpoint.ResumeWaveIndex >= waves.Count)
        {
            return $"Resume checkpoint points at wave " +
                   $"{checkpoint.ResumeWaveIndex.ToString(CultureInfo.InvariantCulture)}, but the " +
                   $"process now partitions into " +
                   $"{waves.Count.ToString(CultureInfo.InvariantCulture)} wave(s). The approved " +
                   "process no longer matches this task — re-deploy the release.";
        }

        var byId = targets.ToDictionary(t => t.Id);

        aliveTargets.Clear();
        foreach (var id in checkpoint.AliveTargetIds)
        {
            if (!byId.TryGetValue(id, out var target))
            {
                return $"Resume checkpoint lists target {id} as still deploying, but it is no " +
                       "longer assigned to this task. The target set changed while the task was " +
                       "paused — re-deploy the release.";
            }
            aliveTargets.Add(target);
        }

        droppedTargets.Clear();
        foreach (var dropped in checkpoint.DroppedTargets)
        {
            // A dropped target stays ASSIGNED (drop-out is orchestration state, not an
            // assignment change), so a missing row here is the same corruption as above.
            if (!byId.TryGetValue(dropped.TargetId, out var target))
            {
                return $"Resume checkpoint records target {dropped.TargetId} as dropped, but it " +
                       "is no longer assigned to this task. The target set changed while the " +
                       "task was paused — re-deploy the release.";
            }
            if (!Enum.TryParse<DropReason>(dropped.Reason, out var reason))
            {
                return $"Resume checkpoint records an unknown drop reason " +
                       $"'{dropped.Reason}' for target {dropped.TargetId}.";
            }
            droppedTargets.Add(new DroppedTargetInfo(
                target, reason, dropped.StepName, dropped.Error));
        }

        softFailedTargets.Clear();
        softFailedTargets.UnionWith(checkpoint.SoftFailedTargetIds);

        outputAccumulator.RestoreFrom(checkpoint.TargetOutputs, checkpoint.ServerOutputs);
        return null;
    }

    // ── M14.2 + M14.3 helpers ────────────────────────────────────────────

    /// <summary>
    /// Runs a server-side step through the shared <see cref="StepRetryRunner"/>
    /// (KrakenDeploy.Execution) — the same retry loop + per-attempt timeout the
    /// offline agent runner uses — and keeps the server's own side-effects via
    /// the runner's callbacks: a <c>Deployment.StepRetried</c> audit + retry
    /// marker log before each delay, and a "succeeded on attempt N" log on a
    /// late success. Target-side groups have their own equivalent retry loop
    /// inline in <c>DispatchAsync</c> because the sub-plan dispatch lifecycle
    /// (TCS + subPlans.Register + linked CTS) doesn't factor cleanly here.
    ///
    /// <para>
    /// Returns <c>(ok, timedOut)</c> reflecting the FINAL attempt only — the
    /// retry detail lives in the deployment-log entries + audit rows. A
    /// per-step timeout is surfaced via the runner's <c>TimedOut</c> and the
    /// caller (<c>RunServerWaveAsync</c>) emits the timeout log + audit once.
    /// <c>MaxRetries = 0</c> (default) makes this a single attempt.
    /// </para>
    ///
    /// <para>
    /// Each retry attempt gets a fresh per-step timeout window — total
    /// wall time can be up to <c>(TimeoutSeconds + RetryDelaySeconds) *
    /// (MaxRetries + 1)</c>. Operators should size the deployment-level
    /// expectations accordingly; we don't model an aggregate budget in v1.
    /// </para>
    ///
    /// <para>
    /// <strong>Output variables on retry (B4):</strong> each attempt returns a
    /// fresh <see cref="ServerScriptResult"/>, so only the FINAL attempt's
    /// captures are returned — a failed attempt's partial outputs are
    /// discarded naturally by the retry.
    /// </para>
    /// </summary>
    private async Task<(bool Ok, bool TimedOut, int AttemptCount, DateTimeOffset StartedUtc, int EffectiveTimeoutSeconds, ServerScriptResult Result)>
        RunServerStepWithRetriesAsync(
            Guid deploymentId,
            DeploymentStepPlan step,
            StepSnapshot snapshot,
            ServerTask deployment,
            TaskAuditVocabulary vocab,
            IAuditLog audit,
            LogSequencer logSeq,
            IReadOnlyDictionary<string, string> flatVars,
            SecretRedactor redactor,
            CancellationToken ct)
    {
        // M14.5 — capture start time at first attempt so the outcome row
        // carries an accurate StartedUtc the Steps tab can show duration from.
        var startedUtc = DateTimeOffset.UtcNow;

        // E3 — the per-attempt timeout StepRetryRunner enforces. For a
        // DeployRelease step this folds in the Engine ceiling (see
        // EffectiveServerStepTimeoutSeconds); StepRetryRunner's own timeout then
        // fires and classifies the step TimedOut, so no separate ceiling logic is
        // needed inside WaitForChildAsync.
        var effectiveTimeoutSeconds = await EffectiveServerStepTimeoutSecondsAsync(
            step, snapshot.TimeoutSeconds, deployment.SpaceId, ct).ConfigureAwait(false);

        // E3 — a DeployRelease step runs at most ONCE (see
        // EffectiveServerStepMaxRetries): a step-level retry would trigger a fresh
        // child deployment while the prior (timed-out) child is still running,
        // racing duplicate deploys of the same release to the same targets.
        var effectiveMaxRetries = EffectiveServerStepMaxRetries(step, snapshot.MaxRetries);
        if (effectiveMaxRetries != snapshot.MaxRetries)
        {
            logger.LogDebug(
                "Server step '{Step}' ({Type}) is not step-retried (configured MaxRetries={Configured} " +
                "ignored) — retrying it would re-trigger a child deployment.",
                snapshot.Name, step.StepType, snapshot.MaxRetries);
        }

        var outcome = await StepRetryRunner.RunAsync(
            snapshot.Name,
            effectiveMaxRetries,
            snapshot.RetryDelaySeconds,
            effectiveTimeoutSeconds,
            runAttempt: (CancellationToken attemptCt) =>
                ExecuteServerStepAsync(deploymentId, step, flatVars, deployment.SpaceId, redactor, attemptCt),
            isSuccess: r => r.Success,
            onTimeoutResult: () => ServerScriptResult.Failure,
            // Server surfaces the per-step timeout ONCE via the final TimedOut
            // (RunServerWaveAsync logs + audits it), not per timed-out attempt.
            onAttemptTimedOutAsync: null,
            // Wave steps run in parallel; each writes its log line through its
            // own short-lived context (LogSequencer.AppendAsync) so they never
            // contend on the shared per-dispatch db. Audit already uses its own
            // per-call context (AuditLogService).
            onRetryAsync: async info =>
            {
                await logSeq.AppendAsync(-1, null, "warning", info.Marker, ct).ConfigureAwait(false);
                await audit.RecordAsync(
                    vocab.StepRetried,
                    subjectType: vocab.SubjectType,
                    subjectId:   deployment.Id.ToString(),
                    details:     $"Step={snapshot.Name}, " +
                                 $"Attempt={info.Attempt.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"MaxRetries={info.MaxAttempts.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"RetryDelaySeconds={info.DelaySeconds.ToString(CultureInfo.InvariantCulture)}",
                    ct: ct).ConfigureAwait(false);
            },
            onLateSuccessAsync: attemptCount => logSeq.AppendAsync(-1, null, "info",
                $"--- Step '{snapshot.Name}' succeeded on attempt " +
                $"{attemptCount.ToString(CultureInfo.InvariantCulture)} ---", ct),
            ct).ConfigureAwait(false);

        return (Ok: outcome.Result.Success, TimedOut: outcome.TimedOut,
                AttemptCount: outcome.AttemptCount, StartedUtc: startedUtc,
                EffectiveTimeoutSeconds: effectiveTimeoutSeconds,
                Result: outcome.Result);
    }

    /// <summary>
    /// E3 — the per-attempt timeout <see cref="StepRetryRunner"/> enforces for a
    /// server-side step. An <c>Octopus.DeployRelease</c> step polls its child
    /// deployment in <c>WaitForChildAsync</c>; left unbounded it would pin the
    /// parent's <see cref="NodeTaskGate"/> slot forever if the child never
    /// terminates. So a DeployRelease step with NO explicit timeout
    /// (<c>TimeoutSeconds &lt;= 0</c>) is bounded by the Engine BACKSTOP
    /// (<see cref="EngineOptions.MaxDeployReleaseGatedWaitDuration"/>, 7 d), while the
    /// tighter working bound (<see cref="EngineOptions.MaxDeployReleaseWaitDuration"/>,
    /// 1 h) is enforced inside <c>WaitForChildAsync</c> against NON-paused time only —
    /// see WP3-b. An explicit
    /// per-step timeout is honoured as-is (even above the ceiling — operator
    /// intent, same rule as <see cref="EngineOptions.MaxTargetWaveDuration"/>).
    /// Every other server step keeps its raw <c>TimeoutSeconds</c> (0 = unlimited
    /// — the documented no-server-ceiling residual for script steps). Whichever
    /// bound fires is classified <c>TimedOut</c> by <see cref="StepRetryRunner"/>,
    /// preserving the OCE-propagation contract.
    /// </summary>
    private async Task<int> EffectiveServerStepTimeoutSecondsAsync(
        DeploymentStepPlan step,
        int configuredTimeoutSeconds,
        Guid spaceId,
        CancellationToken ct)
    {
        if (configuredTimeoutSeconds > 0
            || !step.StepType.Equals(DeployReleaseStepRunner.StepType, StringComparison.OrdinalIgnoreCase))
        {
            return configuredTimeoutSeconds;
        }

        // WP3-b — the budget depends on whether the CHILD can pause. A child with no
        // manual-intervention gate keeps the tight 1 h ceiling, so a hung child is still
        // classified TimedOut at exactly the same moment as before. Only a child that
        // actually contains a gate gets the far larger backstop, because its wait is
        // legitimately bounded by a human's approval window (72 h by default) rather than
        // by execution time. Deciding it HERE rather than inside WaitForChildAsync is what
        // preserves the TimedOut classification: StepRetryRunner infers a timeout from its
        // own token firing, and nothing inside the wait loop can reach that.
        var childHasGate = await ChildProjectHasGateAsync(step, spaceId, ct)
            .ConfigureAwait(false);
        if (!childHasGate)
        {
            var working = engineOptions.Value.MaxDeployReleaseWaitDuration;
            var workingSeconds = working > TimeSpan.Zero
                ? working.TotalSeconds
                : TimeSpan.FromHours(1).TotalSeconds;
            return Math.Clamp(
                (int)Math.Ceiling(Math.Min(workingSeconds, MaxServerStepTimeoutSeconds)),
                1, MaxServerStepTimeoutSeconds);
        }
        // Unconfigured DeployRelease wait → apply the HARD backstop
        // (MaxDeployReleaseGatedWaitDuration, 7 d). The working bound —
        // MaxDeployReleaseWaitDuration, 1 h — is enforced inside WaitForChildAsync
        // instead, because only that loop can tell "the child is working" from "the child
        // is parked at a manual-intervention gate" and charge just the former against the
        // budget (WP3-b). Before this split, a child with an approval gate always failed
        // its parent at 1 h against a 72 h approval window, and when the human eventually
        // approved, the child resumed and deployed for real AFTER the parent had already
        // reported failure. Simply raising the ceiling was the alternative, but it would
        // also stop catching a genuinely hung child for days.
        //
        // A non-positive (misconfigured) backstop falls back to 7 d rather than
        // reintroducing an unbounded wait. Round up so a sub-second test value still
        // yields a positive whole second (StepRetryRunner treats <= 0 as unlimited).
        // Clamp to a max safely within CancellationTokenSource.CancelAfter's bound:
        // StepRetryRunner passes this to CancelAfter, which throws for a duration whose
        // milliseconds exceed ~Int32.MaxValue — an absurd (>24 day) configured value must
        // degrade to a long ceiling, not an ArgumentOutOfRangeException that fails every
        // DeployRelease step as a generic error.
        var ceiling = engineOptions.Value.MaxDeployReleaseGatedWaitDuration;
        var seconds = ceiling > TimeSpan.Zero
            ? ceiling.TotalSeconds
            : TimeSpan.FromDays(7).TotalSeconds;
        return Math.Clamp((int)Math.Ceiling(Math.Min(seconds, MaxServerStepTimeoutSeconds)), 1, MaxServerStepTimeoutSeconds);
    }

    /// <summary>
    /// Whether the child project a <c>Octopus.DeployRelease</c> step targets has any
    /// manual-intervention gate in its process — i.e. whether this step can legitimately
    /// wait on a human.
    /// <para>
    /// Best-effort and fail-SAFE: anything unresolvable (a slug or name rather than a GUID,
    /// a missing project, a DB hiccup) answers <c>false</c>, which keeps the TIGHTER
    /// ceiling. Guessing "gated" on uncertainty would quietly let a hung child hold the
    /// parent's node slot for days.
    /// </para>
    /// </summary>
    private async Task<bool> ChildProjectHasGateAsync(
        DeploymentStepPlan step, Guid spaceId, CancellationToken ct)
    {
        try
        {
            var raw = ManualInterventionConfigKeys.Read(
                step.Config, KrakenDeploy.Contracts.Steps.DeployReleaseConfigKeys.ProjectId);
            if (!Guid.TryParse(raw, out var childProjectId))
            {
                return false;
            }
            await using var scope = scopeFactory.CreateAsyncScope();
            await using var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
                .CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.Processes
                .IgnoreQueryFilters()
                .Where(p => p.OwnerKind == ProcessOwnerKind.Project
                            && p.OwnerId == childProjectId)
                .SelectMany(p => p.Steps)
                .AnyAsync(s => EF.Functions.ILike(
                    s.StepType, ManualInterventionConfigKeys.StepType), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "Could not determine whether the DeployRelease child of step '{Step}' has a " +
                "manual-intervention gate; applying the tighter wait ceiling.", step.Name);
            return false;
        }
    }

    // 24 days in seconds — 24d * 86_400 * 1000 ms stays under Int32.MaxValue ms,
    // so TimeSpan.FromSeconds(this) is a valid CancelAfter argument on every
    // framework (the pre-.NET-6 bound), leaving a wide safety margin.
    private const int MaxServerStepTimeoutSeconds = 24 * 24 * 60 * 60;

    /// <summary>
    /// E3 — an <c>Octopus.DeployRelease</c> step runs at most ONCE. A step-level
    /// retry re-invokes the runner, which TRIGGERS A NEW CHILD DEPLOYMENT (the
    /// child is a fresh deployment of the release, not an idempotent re-run) while
    /// a timed-out attempt leaves its previous child still running (children
    /// bypass the gate). Retrying would therefore race up to
    /// <c>(MaxRetries + 1)</c> concurrent deployments of the same release to the
    /// same targets and stretch the parent's <see cref="NodeTaskGate"/> slot hold
    /// to <c>(MaxRetries + 1)×</c> the ceiling — defeating it. The child
    /// deployment carries its own retry/failure semantics; the parent step does
    /// not re-drive it. Every other server step keeps its configured
    /// <c>MaxRetries</c>.
    /// </summary>
    private static int EffectiveServerStepMaxRetries(DeploymentStepPlan step, int configuredMaxRetries)
        => step.StepType.Equals(DeployReleaseStepRunner.StepType, StringComparison.OrdinalIgnoreCase)
            ? 0
            : configuredMaxRetries;

    private static async Task LogAndAuditStepSkippedAsync(
        KrakenDbContext db, IAuditLog audit, LogSequencer logSeq,
        ServerTask deployment, TaskAuditVocabulary vocab, StepSnapshot snapshot,
        StepConditionEvaluator.Decision decision,
        CancellationToken ct)
    {
        await logSeq.AppendAsync(-1, null, "info", $"--- Step '{snapshot.Name}' skipped: {decision.Reason} ---", ct).ConfigureAwait(false);

        // M14.3.1 — typed Decision.Kind drives the audit event type
        // (replaced the pre-M14.3.1 substring-on-Reason heuristic which
        // would silently change behaviour when the reason wording changed).
        var eventType = decision.Kind == StepConditionEvaluator.Kind.Unresolved
            ? vocab.VariableConditionUnresolved
            : vocab.StepSkipped;
        await audit.RecordAsync(
            eventType,
            subjectType: vocab.SubjectType,
            subjectId:   deployment.Id.ToString(),
            details:     $"Step={snapshot.Name}, Reason={decision.Reason}",
            ct: ct).ConfigureAwait(false);
    }

    // Instance (not static) + fresh-context log write: this is the one
    // log/audit helper with a CONCURRENT caller (RunServerWaveAsync's parallel
    // step tasks), so its log line goes through logSeq.AppendAsync rather
    // than the shared db. The sequential target-wave caller is unaffected.
    // E3: takes the EFFECTIVE timeout (which, for a DeployRelease step with no
    // explicit TimeoutSeconds, is the Engine ceiling) rather than reading
    // snapshot.TimeoutSeconds — otherwise a ceiling-driven timeout would log the
    // misleading "timed out after 0s".
    private static async Task LogAndAuditStepTimedOutAsync(
        IAuditLog audit, LogSequencer logSeq,
        ServerTask deployment, TaskAuditVocabulary vocab, StepSnapshot snapshot, int effectiveTimeoutSeconds,
        CancellationToken ct)
    {
        await logSeq.AppendAsync(-1, null, "error",
            $"--- Step '{snapshot.Name}' timed out after " +
            $"{effectiveTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}s ---",
            ct).ConfigureAwait(false);
        await audit.RecordAsync(
            vocab.StepTimedOut,
            subjectType: vocab.SubjectType,
            subjectId:   deployment.Id.ToString(),
            details:     $"Step={snapshot.Name}, " +
                         $"TimeoutSeconds={effectiveTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}",
            ct: ct).ConfigureAwait(false);
    }

    private static async Task LogAndAuditStepFailedNonRequiredAsync(
        KrakenDbContext db, IAuditLog audit, LogSequencer logSeq,
        ServerTask deployment, TaskAuditVocabulary vocab, StepSnapshot snapshot,
        CancellationToken ct)
    {
        await logSeq.AppendAsync(-1, null, "warning",
            $"--- Step '{snapshot.Name}' failed (not required) — " +
            "deployment continues ---", ct).ConfigureAwait(false);
        await audit.RecordAsync(
            vocab.StepFailedNonRequired,
            subjectType: vocab.SubjectType,
            subjectId:   deployment.Id.ToString(),
            details:     $"Step={snapshot.Name}",
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits the <c>Deployment.Slow</c> audit event when the deployment's
    /// total runtime exceeded the operator-configured threshold (M13.F.3).
    /// A non-zero threshold AND a known StartedUtc are required; failure
    /// to resolve PerformanceSettings is swallowed so an audit-event hiccup
    /// can never fail an otherwise-successful deployment.
    /// </summary>
    private static async Task EmitSlowDeploymentAuditIfNeededAsync(
        IServiceProvider sp,
        ServerTask deployment,
        TaskAuditVocabulary vocab,
        DateTimeOffset completedUtc,
        CancellationToken ct)
    {
        try
        {
            if (deployment.StartedUtc is null)
            {
                return;
            }

            var performance = sp.GetRequiredService<
                KrakenDeploy.Server.Data.Services.PerformanceSettingsService>();
            var settings = await performance.GetAsync(ct).ConfigureAwait(false);
            var threshold = settings.SlowDeploymentThresholdMinutes;
            if (threshold <= 0)
            {
                return;
            }

            // WP3 — discount time spent parked at manual-intervention gates.
            // TryResumeAsync deliberately does not restamp StartedUtc (the slow audits
            // want the true start), so a raw completed-minus-started span bills the
            // human approval window as execution time: with a 30 min default threshold
            // and a 72 h approval default, EVERY approved deployment would emit a
            // *.Slow audit — an M13.B subscription event — drowning the real signal.
            // Cheap pre-filter: the discount can only ever REDUCE the span, so a task
            // already under the threshold cannot cross it and needs no gate query at all
            // — which is every task that never paused.
            if ((completedUtc - deployment.StartedUtc.Value).TotalMinutes < threshold)
            {
                return;
            }
            var pausedSpans = await PausedSpansAsync(sp, deployment.Id, ct)
                .ConfigureAwait(false);
            var elapsed = completedUtc - deployment.StartedUtc.Value - TotalPaused(pausedSpans);
            if (elapsed.TotalMinutes < threshold)
            {
                return;
            }

            var audit = sp.GetRequiredService<IAuditLog>();
            await audit.RecordAsync(
                vocab.Slow,
                subjectType: vocab.SubjectType,
                subjectId:   deployment.Id.ToString(),
                details:     string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "DurationMinutes={0:F1}, ThresholdMinutes={1}, ProjectId={2}",
                    elapsed.TotalMinutes, threshold, deployment.ProjectId),
                ct: ct).ConfigureAwait(false);
        }
        catch
        {
            // Audit emission is best-effort — never bubble the failure
            // up into deployment finalisation.
        }
    }

    /// <summary>
    /// WP3 — total wall time this task spent parked at manual-intervention gates, so
    /// the slow-task audits can discount it. Summed from each gate's
    /// <c>CreatedUtc → ActedUtc</c> span; an unanswered gate contributes nothing
    /// (the task is still paused, so nothing is being finalised).
    /// <para>
    /// Gates are sequential by construction — the task is parked while one is open —
    /// so summing spans cannot double-count.
    /// </para>
    /// </summary>
    /// <summary>Overload for a caller that has no context open yet.</summary>
    private static async Task<List<PausedSpan>> PausedSpansAsync(
        IServiceProvider sp, Guid taskId, CancellationToken ct)
    {
        await using var db = await sp
            .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
            .CreateDbContextAsync(ct).ConfigureAwait(false);
        return await PausedSpansAsync(db, taskId, ct).ConfigureAwait(false);
    }

    private static async Task<List<PausedSpan>> PausedSpansAsync(
        KrakenDbContext db, Guid taskId, CancellationToken ct)
    {
        var rows = await db.Interruptions
            .IgnoreQueryFilters()
            .Where(i => i.TaskId == taskId && i.ActedUtc != null)
            .Select(i => new { i.CreatedUtc, i.ActedUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Clamp each span at zero. CreatedUtc is stamped by the orchestrating worker and
        // ActedUtc by whichever node served the decision (web, /api, or the sweeper), so
        // under HA clock skew a fast approval can produce a NEGATIVE span — which would
        // INFLATE the elapsed time these spans are subtracted from and fire the very
        // slow-task audit the discount exists to suppress.
        return [.. rows
            .Select(r => new PausedSpan(
                r.CreatedUtc,
                r.ActedUtc!.Value < r.CreatedUtc ? r.CreatedUtc : r.ActedUtc!.Value))];
    }

    /// <summary>
    /// One interval a task spent parked at a gate, used to discount waiting-for-a-human
    /// time out of the "slow" audits. Kept as intervals rather than one total because
    /// the total is only valid for the TASK: a per-target span must subtract just the
    /// part of the pause that actually overlaps that target's own window.
    /// </summary>
    private readonly record struct PausedSpan(DateTimeOffset From, DateTimeOffset To)
    {
        public TimeSpan Duration => To - From;

        /// <summary>How much of this pause falls inside <c>[start, end]</c>.</summary>
        public TimeSpan OverlapWith(DateTimeOffset start, DateTimeOffset end)
        {
            var from = From > start ? From : start;
            var to = To < end ? To : end;
            return to > from ? to - from : TimeSpan.Zero;
        }
    }

    private static TimeSpan TotalPaused(List<PausedSpan> spans)
    {
        var total = TimeSpan.Zero;
        foreach (var s in spans)
        {
            total += s.Duration;
        }
        return total;
    }

    /// <summary>
    /// M-RollingDeployments Phase 3 — emits one
    /// <see cref="AuditEventType.DeploymentTargetSlow"/> per target whose
    /// effective duration (max <c>CompletedUtc</c> − min <c>StartedUtc</c>
    /// across its <see cref="TaskStepOutcome"/> rows) exceeded
    /// <c>SlowDeploymentThresholdMinutes</c>. Lets operators pinpoint
    /// which specific machine slowed a multi-target run, even when the
    /// deployment as a whole stayed under threshold (single straggler
    /// drowned out by faster peers).
    /// <para>
    /// Skips silently on any DB / lookup hiccup — slow-audit emission is
    /// best-effort and must never fail an otherwise-successful deployment.
    /// </para>
    /// </summary>
    private static async Task EmitTargetSlowAuditsIfNeededAsync(
        IServiceProvider sp,
        ServerTask deployment,
        TaskAuditVocabulary vocab,
        CancellationToken ct)
    {
        try
        {
            var performance = sp.GetRequiredService<
                KrakenDeploy.Server.Data.Services.PerformanceSettingsService>();
            var settings = await performance.GetAsync(ct).ConfigureAwait(false);
            var threshold = settings.SlowDeploymentThresholdMinutes;
            if (threshold <= 0)
            {
                return;
            }

            await using var db = await sp
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
                .CreateDbContextAsync(ct).ConfigureAwait(false);

            var rows = await db.TaskStepOutcomes
                .Where(o => o.TaskId == deployment.Id
                            && o.TargetId != null
                            && o.StartedUtc != null)
                .Select(o => new { o.TargetId, o.StartedUtc, o.CompletedUtc })
                .ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return;
            }

            // WP3 — discount time parked at a manual-intervention gate, for the same
            // reason the task-level audit does. Gate outcomes carry a NULL TargetId, so
            // they are already excluded from `rows`; it is the SPAN that has to shrink.
            //
            // WP3-b — per target, subtract only the OVERLAP with that target's own
            // window, not the task-wide total. The earlier version subtracted the whole
            // total from every target, so a target that finished before the gate ever
            // opened had ~72 h taken off a span that never contained the pause: its
            // effective duration went deeply negative and a genuinely slow machine was
            // silently never audited. With the shipped defaults that blinded the
            // DeploymentTargetSlow signal for any task with one answered gate.
            // Shares the context already open above — this used to open a second one from
            // the factory for a query the caller could just as well issue itself.
            var pausedSpans = await PausedSpansAsync(db, deployment.Id, ct)
                .ConfigureAwait(false);

            var perTarget = rows
                .GroupBy(r => r.TargetId!.Value)
                .Select(g =>
                {
                    var start = g.Min(r => r.StartedUtc!.Value);
                    var end = g.Max(r => r.CompletedUtc);
                    var parked = TimeSpan.Zero;
                    foreach (var span in pausedSpans)
                    {
                        parked += span.OverlapWith(start, end);
                    }
                    return new
                    {
                        TargetId = g.Key,
                        // The ONE duration used for both the threshold test and the audit
                        // payload. They used to disagree — filtered on the discounted span
                        // but reported the raw one — so an operator got a notification
                        // claiming a target took 72 hours against a 30-minute threshold.
                        Duration = end - start - parked,
                    };
                })
                .Where(t => t.Duration.TotalMinutes >= threshold)
                .ToList();
            if (perTarget.Count == 0)
            {
                return;
            }

            // Resolve target names so the audit detail is operator-friendly.
            var slowTargetIds = perTarget.Select(t => t.TargetId).ToList();
            var nameById = await db.DeploymentTargets
                .Where(t => slowTargetIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Name })
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct).ConfigureAwait(false);

            var audit = sp.GetRequiredService<IAuditLog>();
            foreach (var t in perTarget)
            {
                var duration = t.Duration.TotalMinutes;
                var name = nameById.GetValueOrDefault(t.TargetId, t.TargetId.ToString());
                await audit.RecordAsync(
                    vocab.TargetSlow,
                    subjectType: vocab.SubjectType,
                    subjectId:   deployment.Id.ToString(),
                    details:     string.Format(
                        CultureInfo.InvariantCulture,
                        "TargetId={0}, Target={1}, DurationMinutes={2:F1}, ThresholdMinutes={3}",
                        t.TargetId, name, duration, threshold),
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort, same policy as EmitSlowDeploymentAuditIfNeededAsync.
        }
    }

    /// <summary>
    /// M13.F.3 — emits one <see cref="AuditEventType.DeploymentStepSlow"/> per
    /// step whose own duration (<c>CompletedUtc − StartedUtc</c> on its
    /// <see cref="TaskStepOutcome"/>) exceeded
    /// <c>SlowStepThresholdMinutes</c>. Lets operators pinpoint the specific
    /// slow step even when the deployment/target stayed under the coarser
    /// deployment threshold. Threshold = 0 disables. Best-effort — a lookup
    /// hiccup never fails an otherwise-successful deployment.
    /// </summary>
    private static async Task EmitSlowStepAuditsIfNeededAsync(
        IServiceProvider sp,
        ServerTask deployment,
        TaskAuditVocabulary vocab,
        CancellationToken ct)
    {
        try
        {
            var performance = sp.GetRequiredService<
                KrakenDeploy.Server.Data.Services.PerformanceSettingsService>();
            var settings = await performance.GetAsync(ct).ConfigureAwait(false);
            var threshold = settings.SlowStepThresholdMinutes;
            if (threshold <= 0)
            {
                return;
            }

            await using var db = await sp
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
                .CreateDbContextAsync(ct).ConfigureAwait(false);

            // WP3 — exclude manual-intervention outcomes. Their "duration" is the human
            // approval window (72 h by default), so every approved gate would emit a
            // *.StepSlow audit — an M13.B subscription event — turning a normal
            // change-control wait into a permanent stream of false "slow step"
            // notifications. A gate's own deadline is its intervention timeout, which
            // is the meaningful bound.
            var slowSteps = await db.TaskStepOutcomes
                .Where(o => o.TaskId == deployment.Id && o.StartedUtc != null
                         && o.Outcome != StepOutcomeKind.ManualInterventionApproved
                         && o.Outcome != StepOutcomeKind.ManualInterventionRejected
                         && o.Outcome != StepOutcomeKind.ManualInterventionTimedOut)
                .Select(o => new { o.StepIndex, o.StepName, o.TargetId, o.StartedUtc, o.CompletedUtc })
                .ToListAsync(ct).ConfigureAwait(false);

            var audit = sp.GetRequiredService<IAuditLog>();
            foreach (var s in slowSteps)
            {
                var duration = (s.CompletedUtc - s.StartedUtc!.Value).TotalMinutes;
                if (duration < threshold)
                {
                    continue;
                }
                await audit.RecordAsync(
                    vocab.StepSlow,
                    subjectType: vocab.SubjectType,
                    subjectId:   deployment.Id.ToString(),
                    details:     string.Format(
                        CultureInfo.InvariantCulture,
                        "StepIndex={0}, Step={1}, TargetId={2}, DurationMinutes={3:F1}, ThresholdMinutes={4}",
                        s.StepIndex, s.StepName, s.TargetId, duration, threshold),
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort, same policy as EmitSlowDeploymentAuditIfNeededAsync.
        }
    }

    /// <summary>
    /// M-RollingDeployments Phase 3 — emits the audit + log row for a
    /// target drop-out. Centralised so the orchestrator's two callsites
    /// (Required-step failure inside a batch + agent-offline at dispatch
    /// time) emit identical event shapes; downstream subscribers
    /// (M13.B.2/3 notifications) can route on
    /// <see cref="AuditEventType.DeploymentTargetDropped"/> consistently.
    /// </summary>
    private async Task EmitTargetDroppedAsync(
        KrakenDbContext db,
        IAuditLog auditLog,
        LogSequencer logSeq,
        ServerTask deployment,
        TaskAuditVocabulary vocab,
        DroppedTargetInfo dropped,
        WavePartitioner.Wave wave,
        CancellationToken ct)
    {
        var waveNames = string.Join(", ", wave.Steps.Select(s => s.Name));
        var reasonText = dropped.Reason switch
        {
            DropReason.RequiredStepFailed
                => $"Required step '{dropped.StepName}' failed",
            DropReason.AgentOffline => "agent offline at dispatch",
            _                       => "unknown",
        };

        await logSeq.AppendAsync(-1, null, "warning",
            $"--- Target '{dropped.Target.Name}' dropped out: " +
            $"{reasonText}{(dropped.Error is null ? "" : $" — {dropped.Error}")} ---", ct).ConfigureAwait(false);

        await auditLog.RecordAsync(
            vocab.TargetDropped,
            subjectType: vocab.SubjectType,
            subjectId:   deployment.Id.ToString(),
            details:     $"TargetId={dropped.Target.Id}, " +
                         $"Target={dropped.Target.Name}, " +
                         $"Reason={dropped.Reason}, " +
                         $"Step={dropped.StepName ?? "(none)"}, " +
                         $"Wave=[{waveNames}], " +
                         $"Error={dropped.Error ?? "(none)"}",
            ct: ct).ConfigureAwait(false);

        logger.LogInformation(
            "Deployment {DeploymentId}: target {TargetId} ({TargetName}) dropped — {Reason}",
            deployment.Id, dropped.Target.Id, dropped.Target.Name, dropped.Reason);
    }

    // ── M14.4 wave helpers ──────────────────────────────────────────────

    /// <summary>
    /// One server-side step's outcome inside a wave. <see cref="Skipped"/>
    /// flags steps that were filtered out by Run Condition or by the
    /// role-based <c>StepAppliesToTarget</c> gate so the outer loop can
    /// distinguish "didn't run" from "ran and failed".
    ///
    /// <para>
    /// M14.5: <see cref="AttemptCount"/> and <see cref="StartedUtc"/>
    /// flow from <see cref="RunServerStepWithRetriesAsync"/> so the
    /// orchestrator can populate <see cref="TaskStepOutcome"/>
    /// rows with accurate timing + retry counts. <see cref="StartedUtc"/>
    /// is null when the step was skipped (it never started).
    /// </para>
    /// </summary>
    private sealed record ServerStepOutcome(
        DeploymentStepPlan Step,
        bool Skipped,
        bool Ok,
        bool TimedOut,
        int AttemptCount,
        DateTimeOffset? StartedUtc,
        // B4: output variables captured from ##octopus[setVariable] markers
        // (final attempt only) + their sensitive subset. Null when the step
        // was skipped or is a kind that captures nothing (DeployRelease).
        IReadOnlyDictionary<string, string>? CapturedOutputs = null,
        IReadOnlyCollection<string>? SensitiveOutputNames = null,
        // E3: the timeout StepRetryRunner actually enforced (the Engine ceiling
        // for an unconfigured DeployRelease wait). Used for the timeout log +
        // outcome message so a ceiling hit doesn't read "timed out after 0s". 0
        // for skipped steps.
        int EffectiveTimeoutSeconds = 0);

    /// <summary>
    /// Runs every step in a server-side wave concurrently. Each step retains
    /// its own Run Condition + Required + Retries + Timeout (the M14.2/3
    /// helpers operate on a single step and compose cleanly under
    /// <see cref="Task.WhenAll"/>). Returns the wave's per-step outcomes
    /// for the caller's Required gate.
    ///
    /// <para>
    /// <strong>Concurrency:</strong> <see cref="KrakenDbContext"/> is NOT
    /// thread-safe across concurrent operations on the same instance, so
    /// per-step work that needs to write logs / audit rows uses a fresh
    /// <see cref="IDbContextFactory{KrakenDbContext}"/>-created context.
    /// Variables shared across siblings (the wave-level <c>hasFailed</c>,
    /// the var dictionary) are read-only inside the wave so no contention.
    /// </para>
    /// </summary>
    private async Task<List<ServerStepOutcome>> RunServerWaveAsync(
        WavePartitioner.Wave wave,
        StepSnapshot[] snapshotSteps,
        bool hasFailedAtWaveStart,
        VariableDictionary varDict,
        ServerTask deployment,
        TaskAuditVocabulary vocab,
        KrakenDbContext db,
        IAuditLog auditLog,
        LogSequencer logSeq,
        IReadOnlyDictionary<string, string> flatVars,
        SecretRedactor redactor,
        CancellationToken ct)
    {
        // Evaluate Conditions + Role filter sequentially first so skipped-
        // step logs land in declared order. Surviving steps run in parallel.
        var toRun = new List<DeploymentStepPlan>(wave.Steps.Count);
        var skipped = new List<DeploymentStepPlan>(wave.Steps.Count);
        foreach (var s in wave.Steps)
        {
            var snapshot = snapshotSteps[s.Index];
            var decision = StepConditionEvaluator.Evaluate(
                snapshot.Condition,
                snapshot.ConditionVariableExpression,
                hasFailedAtWaveStart,
                varDict);
            if (decision.Action == StepConditionEvaluator.Action.Skip)
            {
                await LogAndAuditStepSkippedAsync(
                    db, auditLog, logSeq, deployment, vocab, snapshot, decision, ct)
                    .ConfigureAwait(false);
                // M14.5 — record the Skipped outcome so the Steps tab shows
                // "Skipped: <reason>" instead of leaving an empty row.
                await UpsertStepOutcomeAsync(
                    db, deployment.Id, s.Index, snapshot.Name,
                    StepOutcomeKind.Skipped, attemptCount: 0,
                    errorMessage: decision.Reason,
                    startedUtc:   null,
                    completedUtc: DateTimeOffset.UtcNow,
                    isServerSide: true,
                    required:     snapshot.Required, ct).ConfigureAwait(false);
                skipped.Add(s);
                continue;
            }
            if (!StepAppliesToTarget(deployment, s))
            {
                // Role-filtered skip — no audit row, the M14.0..3 path
                // didn't audit these either (they're not a user-visible
                // skip). Still record an outcome so the Steps tab shows
                // them with a clear "Skipped: role filter" reason.
                await UpsertStepOutcomeAsync(
                    db, deployment.Id, s.Index, snapshot.Name,
                    StepOutcomeKind.Skipped, attemptCount: 0,
                    errorMessage: "Step roles don't overlap deployment target's roles.",
                    startedUtc:   null,
                    completedUtc: DateTimeOffset.UtcNow,
                    isServerSide: true,
                    required:     snapshot.Required, ct).ConfigureAwait(false);
                skipped.Add(s);
                continue;
            }
            toRun.Add(s);
        }

        if (toRun.Count == 0)
        {
            return skipped.Select(s => new ServerStepOutcome(
                Step: s, Skipped: true, Ok: true, TimedOut: false,
                AttemptCount: 0, StartedUtc: null)).ToList();
        }

        // Fire all surviving steps in parallel. Each Task wraps the M14.3 retry
        // helper. Those per-step writes (retry markers, late-success, timeout)
        // go through short-lived per-write contexts (logSeq.AppendAsync),
        // NOT the shared per-dispatch db, so the steps run fully in parallel with
        // no DbContext contention. Audit uses its own per-call context
        // (AuditLogService). LogSequencer is independently locked, so sequence
        // numbers stay monotonic. The post-wave outcome upserts below run after
        // Task.WhenAll (sequentially) on the shared db.
        var stepTasks = toRun.Select(async s =>
        {
            var snap = snapshotSteps[s.Index];
            var (ok, timedOut, attemptCount, startedUtc, effectiveTimeoutSeconds, result) =
                await RunServerStepWithRetriesAsync(
                    deployment.Id, s, snap, deployment, vocab, auditLog,
                    logSeq, flatVars, redactor, ct).ConfigureAwait(false);
            if (timedOut)
            {
                await LogAndAuditStepTimedOutAsync(
                    auditLog, logSeq, deployment, vocab, snap, effectiveTimeoutSeconds, ct).ConfigureAwait(false);
            }
            return new ServerStepOutcome(
                Step:         s,
                Skipped:      false,
                Ok:           ok,
                TimedOut:     timedOut,
                AttemptCount: attemptCount,
                StartedUtc:   startedUtc,
                CapturedOutputs:      result.Outputs,
                SensitiveOutputNames: result.SensitiveOutputNames,
                EffectiveTimeoutSeconds: effectiveTimeoutSeconds);
        }).ToArray();

        var outcomes = (await Task.WhenAll(stepTasks).ConfigureAwait(false)).ToList();

        // M14.5 — record terminal outcomes for the steps that actually
        // ran (skipped steps already got their outcome row from the
        // pre-loop filter above). Save flushes everything together with
        // the wave's existing log + audit rows in the next SaveChanges.
        var completedUtc = DateTimeOffset.UtcNow;
        foreach (var o in outcomes)
        {
            if (o.Skipped)
            {
                continue;
            }
            var snap = snapshotSteps[o.Step.Index];
            var kind = o.TimedOut ? StepOutcomeKind.TimedOut
                     : o.Ok      ? StepOutcomeKind.Succeeded
                                 : StepOutcomeKind.Failed;
            await UpsertStepOutcomeAsync(
                db, deployment.Id, o.Step.Index, snap.Name,
                kind, o.AttemptCount,
                errorMessage: o.Ok ? null
                              : o.TimedOut
                                  ? $"Step exceeded TimeoutSeconds={o.EffectiveTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}."
                                  : "Step handler returned failure.",
                startedUtc:   o.StartedUtc,
                completedUtc: completedUtc,
                isServerSide: true,
                required:     snap.Required, ct).ConfigureAwait(false);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        outcomes.AddRange(skipped.Select(s => new ServerStepOutcome(
            Step: s, Skipped: true, Ok: true, TimedOut: false,
            AttemptCount: 0, StartedUtc: null)));
        return outcomes;
    }

    /// <summary>
    /// Dispatches a target-side wave to the agent as a single sub-plan and
    /// awaits the agent's <c>CompleteDeploymentAsync</c> via the TCS in
    /// <see cref="IPendingSubPlanRegistry"/>. Returns the wave outcome
    /// plus the per-step boundary reports the agent emitted during the
    /// wave (drained from the registry) so the caller can apply per-step
    /// Required attribution + collision detection.
    ///
    /// <para>
    /// Wave timeout = longest <see cref="StepSnapshot.TimeoutSeconds"/>
    /// across the wave's steps (0 = unlimited). Wave retry = longest
    /// <see cref="StepSnapshot.MaxRetries"/> with matching delay; retries
    /// re-dispatch the WHOLE sub-plan. Operators relying on wave retries
    /// MUST ensure step scripts are idempotent — the agent re-runs every
    /// step in the wave, not just the failed one. Documented in the
    /// M14.4 plan body.
    /// </para>
    /// </summary>
    private async Task<(SubPlanResult Result, bool TimedOut, IReadOnlyList<SubPlanStepResult> PerStepResults)>
        DispatchTargetWaveAsync(
            DeploymentPlan plan,
            IReadOnlyList<DeploymentStepPlan> stepsToRun,
            StepSnapshot[] snapshotSteps,
            ServerTask deployment,
            TaskAuditVocabulary vocab,
            Guid targetId,
            string connectionId,
            IAuditLog auditLog,
            LogSequencer logSeq,
            CancellationToken ct)
    {
        var waveTimeoutSeconds = stepsToRun
            .Select(p => snapshotSteps[p.Index].TimeoutSeconds)
            .DefaultIfEmpty(0)
            .Max();
        var waveMaxRetries = stepsToRun
            .Select(p => snapshotSteps[p.Index].MaxRetries)
            .DefaultIfEmpty(0)
            .Max();
        var waveRetryDelaySeconds = stepsToRun
            .Select(p => snapshotSteps[p.Index].RetryDelaySeconds)
            .DefaultIfEmpty(0)
            .Max();
        var waveNamesForAudit = string.Join(", ", stepsToRun.Select(p => p.Name));

        var subPlan = plan with { Steps = stepsToRun.ToArray() };
        SubPlanResult subPlanResult = default!;
        IReadOnlyList<SubPlanStepResult> lastPerStepResults = [];
        var timedOut = false;
        var attempt = 0;

        // B3: the wave never waits unbounded. An explicit step TimeoutSeconds
        // is honoured as-is (even above the ceiling — operator intent); the
        // engine ceiling only replaces "unlimited" (0). Pre-B3 the default
        // config awaited the TCS forever: the lease renewal kept the B1
        // reconciler away (the process IS alive), the in-flight gauge stayed
        // up, and one dead agent blocked blue-green retirement indefinitely.
        var stepTimeoutConfigured = waveTimeoutSeconds > 0;
        var configuredCeiling = engineOptions.Value.MaxTargetWaveDuration;
        var executionBudget = stepTimeoutConfigured
            ? TimeSpan.FromSeconds(waveTimeoutSeconds)
            // Non-positive config would mean "immediately" — fall back to the
            // shipped default rather than reintroducing an unbounded wait.
            : (configuredCeiling > TimeSpan.Zero ? configuredCeiling : TimeSpan.FromHours(1));

        // F2: the budget above is EXECUTION time. It used to be armed at dispatch,
        // so a sub-plan queued behind another task on the same machine burned its
        // whole deadline while waiting — an operator's 30 s step timeout blew up
        // purely because the box was busy. It is now armed when the agent reports
        // gate acquisition (ReportExecutionStartedAsync). The dispatch-time arm
        // becomes a BACKSTOP — execution budget plus the queue-wait ceiling — so
        // B3's "always armed" invariant still holds when that report never arrives
        // (a wedged agent that stays connected but never executes).
        var configuredQueueWait = engineOptions.Value.MaxTargetQueueWait;
        var queueWaitCeiling = configuredQueueWait > TimeSpan.Zero
            ? configuredQueueWait
            : EngineOptions.DefaultMaxTargetQueueWait;
        // Clamped once: an explicit step TimeoutSeconds is honoured as-is and is an
        // int of seconds, so the sum can exceed what CancelAfter accepts.
        var dispatchBackstop = WaveDeadline.ClampToTimerLimit(
            executionBudget + queueWaitCeiling);

        while (true)
        {
            // B3/B7-sliver: re-resolve the connection per ATTEMPT. Retries used
            // to re-dispatch to the connection id captured before attempt 1 —
            // after a disconnect that id is dead and Clients.Client() silently
            // no-ops, burning a full deadline window per retry. A reconnected
            // agent gets the fresh id; a still-offline agent fails fast here.
            // P3-8 Phase 5 parity: the refreshed connection must belong to the
            // dispatching account, same defense-in-depth as the initial
            // dispatch guard — treat a cross-account hit like offline.
            var currentConnectionId = attempt == 0
                ? connectionId
                : registry.GetConnectionId(targetId);
            if (currentConnectionId is not null
                && attempt > 0
                && _dispatchAccountId.Value != Guid.Empty
                && registry.GetAccountForTarget(targetId) != _dispatchAccountId.Value)
            {
                logger.LogError(
                    "Cross-account dispatch blocked on wave retry for deployment {Deployment}: " +
                    "target {Target}'s live connection belongs to account {ConnectionAccount}, " +
                    "not the dispatch account {DispatchAccount}; abandoning retries.",
                    deployment.Id, targetId,
                    registry.GetAccountForTarget(targetId), _dispatchAccountId.Value);
                currentConnectionId = null;
            }
            if (currentConnectionId is null)
            {
                subPlanResult = new SubPlanResult(
                    Success: false,
                    ErrorMessage:
                        "Agent went offline during the wave; remaining retries abandoned.");
                timedOut = false;
                break;
            }

            // B2 (B6.2): fresh idempotency key per dispatch ATTEMPT. Wave
            // retries re-dispatch the same steps under the same
            // (deployment, target) slot key — a late completion of a previous
            // attempt (e.g. buffered on a disconnected agent and flushed on
            // reconnect) must not resolve THIS attempt's TCS, and a duplicate
            // must not reach the hub's DB fallback finalizer.
            var attemptPlan = subPlan with { DispatchId = Guid.NewGuid() };

            var tcs = new TaskCompletionSource<SubPlanResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // F2: armed with the backstop now, re-armed with the pure execution
            // budget the moment the agent says it took the machine gate. Registered
            // AFTER the deadline object exists but BEFORE the plan is pushed, so an
            // agent that reports instantly still finds its slot.
            linkedCts.CancelAfter(dispatchBackstop);
            var waveDeadline = new WaveDeadline(
                linkedCts, executionBudget,
                backstopDeadline: timeProvider.GetUtcNow() + dispatchBackstop,
                timeProvider);
            subPlans.Register(
                deployment.Id, targetId, attemptPlan.DispatchId, tcs,
                onExecutionStarted: waveDeadline.ArmForExecution);
            var thisAttemptTimedOut = false;

            // B3: watch the agent's connection while this attempt is awaited —
            // a CONTINUOUS disconnect past the grace cancels the slot so the
            // wave resolves per the deployment's failure mode instead of
            // waiting out the whole deadline on an agent that is gone.
            var monitorTask = MonitorAgentConnectionDuringWaveAsync(
                deployment.Id, targetId, linkedCts.Token);

            try
            {
                await agentHub.Clients.Client(currentConnectionId)
                    .RunDeploymentAsync(attemptPlan).ConfigureAwait(false);

                using var ctr = linkedCts.Token.Register(
                    () => tcs.TrySetCanceled(linkedCts.Token));
                try
                {
                    subPlanResult = await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Timeout fired (or external cancel). Distinguish by
                    // whether the outer ct was cancelled too.
                    if (!ct.IsCancellationRequested && linkedCts.IsCancellationRequested)
                    {
                        thisAttemptTimedOut = true;
                        subPlanResult = new SubPlanResult(
                            Success: false,
                            // F2: distinguish "never got the machine" from "ran too
                            // long" — the operator fix differs (a busy/wedged box vs
                            // a slow step), and only the latter is a step timeout.
                            ErrorMessage: !waveDeadline.ArmedForExecution
                                ? "Target step wave never started executing: the agent did " +
                                  $"not acquire its machine execution slot within {dispatchBackstop} " +
                                  "(another task may be holding it, or the agent is wedged)."
                                : stepTimeoutConfigured
                                    ? $"Target step wave timed out after {waveTimeoutSeconds}s."
                                    : "Target step wave exceeded the server-side maximum " +
                                      $"duration ({executionBudget}) with no step timeout " +
                                      "configured; the agent never reported completion.");
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            finally
            {
                // Stop the disconnect monitor BEFORE the CTS is disposed — a
                // pending Task.Delay on a disposed-but-uncancelled source never
                // completes and would leak the monitor task.
                await linkedCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await monitorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Monitor honoured the cancel — expected.
                }

                // Drain whatever the agent reported THIS attempt and clear the
                // registry slot. Cancel() also resolves a still-pending TCS
                // (no-op if already resolved by the agent's CompleteDeployment).
                lastPerStepResults = subPlans.DrainStepResults(deployment.Id, targetId);
                subPlans.Cancel(deployment.Id, targetId, "completed");
            }

            if (subPlanResult.Success)
            {
                timedOut = false;
                if (attempt > 0)
                {
                    await logSeq.AppendAsync(-1, null, "info",
                        $"--- Target wave [{waveNamesForAudit}] succeeded on attempt " +
                        $"{(attempt + 1).ToString(CultureInfo.InvariantCulture)} ---",
                        ct).ConfigureAwait(false);
                }
                break;
            }

            if (attempt >= waveMaxRetries)
            {
                // Final attempt failed — fall through to the Required gate.
                timedOut = thisAttemptTimedOut;
                break;
            }

            // Non-final attempt failed — emit retry marker + audit + delay.
            attempt++;
            await logSeq.AppendAsync(-1, null, "warning",
                $"--- Target wave [{waveNamesForAudit}] attempt " +
                $"{attempt.ToString(CultureInfo.InvariantCulture)} failed; retrying " +
                $"(attempt {(attempt + 1).ToString(CultureInfo.InvariantCulture)} of " +
                $"{(waveMaxRetries + 1).ToString(CultureInfo.InvariantCulture)})" +
                (waveRetryDelaySeconds > 0
                    ? $" in {waveRetryDelaySeconds.ToString(CultureInfo.InvariantCulture)}s ---"
                    : " ---"),
                ct).ConfigureAwait(false);
            await auditLog.RecordAsync(
                vocab.StepRetried,
                subjectType: vocab.SubjectType,
                subjectId:   deployment.Id.ToString(),
                details:     $"TargetWave=[{waveNamesForAudit}], " +
                             $"Attempt={attempt.ToString(CultureInfo.InvariantCulture)}, " +
                             $"MaxRetries={waveMaxRetries.ToString(CultureInfo.InvariantCulture)}, " +
                             $"RetryDelaySeconds={waveRetryDelaySeconds.ToString(CultureInfo.InvariantCulture)}",
                ct: ct).ConfigureAwait(false);

            if (waveRetryDelaySeconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(waveRetryDelaySeconds), ct)
                    .ConfigureAwait(false);
            }
        }

        return (subPlanResult, timedOut, lastPerStepResults);
    }

    /// <summary>
    /// F2 — one wave attempt's deadline timer, in two stages:
    /// <list type="number">
    ///   <item>armed at DISPATCH with the backstop ceiling (execution budget +
    ///     <see cref="EngineOptions.MaxTargetQueueWait"/>), so B3's "the wave
    ///     deadline is always armed" invariant holds even if the agent never
    ///     reports;</item>
    ///   <item>re-armed with the pure EXECUTION budget the moment the agent reports
    ///     it acquired its machine execution gate — queue time behind a busy target
    ///     therefore does not consume the wave's budget — but CLAMPED so the re-arm
    ///     can never push the attempt past the stage-1 backstop instant.</item>
    /// </list>
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> reschedules the
    /// existing timer, so the second arm replaces the first outright; the clamp is
    /// what keeps that replacement from being an extension. In the normal case
    /// (report within the queue-wait ceiling) the clamp is inert and the attempt gets
    /// its whole execution budget.
    /// <para>
    /// Contract: <see cref="ArmForExecution"/> is called from the reporting agent's
    /// hub thread and MUST NOT throw. At-most-once is the REGISTRY's job — a fresh
    /// slot and a fresh <see cref="WaveDeadline"/> are created per attempt, and
    /// <c>PendingSubPlanRegistry.TryMarkExecutionStarted</c> interlocks the one-shot
    /// before invoking this — so no second guard is needed here. It does tolerate
    /// arriving after the attempt already ended: the linked source is disposed by
    /// the attempt's <c>using</c>, which is exactly the
    /// <see cref="ObjectDisposedException"/> swallowed here.
    /// </para>
    /// </summary>
    internal sealed class WaveDeadline(
        CancellationTokenSource cts,
        TimeSpan executionBudget,
        DateTimeOffset backstopDeadline,
        TimeProvider timeProvider)
    {
        /// <summary>Whether the agent ever reported gate acquisition for this
        /// attempt — lets a timeout say "never started" instead of mislabelling a
        /// queue-ceiling hit as a step timeout. Written on the hub thread, read on
        /// the orchestrator's, hence volatile.</summary>
        public bool ArmedForExecution => Volatile.Read(ref _armedForExecution);
        private bool _armedForExecution;

        public void ArmForExecution()
        {
            Volatile.Write(ref _armedForExecution, true);
            try
            {
                var window = ComputeArmWindow(
                    executionBudget, backstopDeadline - timeProvider.GetUtcNow());
                if (window is { } delay)
                {
                    cts.CancelAfter(delay);
                }
                else
                {
                    // Already at or past the backstop: the dispatch-time timer is
                    // firing anyway, but CancelAfter rejects negatives, so cancel
                    // outright rather than reschedule.
                    cts.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // The attempt finished (or was cancelled) while this report was in
                // flight — there is no longer a deadline to move.
            }
        }

        /// <summary>
        /// F2-followup 6 — the re-arm window: the execution budget, CLAMPED to what
        /// is left of the dispatch backstop. <c>null</c> means "the backstop is
        /// already spent, cancel now".
        /// <para>
        /// Arming with the bare budget would let a late report EXTEND the attempt
        /// past the backstop — a report arriving a second before it buys another
        /// full budget, so the ceiling the operator configured (wave + queue wait)
        /// is not actually a ceiling, and a slow-but-alive agent could stretch a
        /// wave indefinitely by reporting late on each retry. Clamping leaves the
        /// normal case untouched (a report inside the queue-wait ceiling has the
        /// whole budget available) and makes the pathological one borrow nothing.
        /// </para>
        /// </summary>
        internal static TimeSpan? ComputeArmWindow(
            TimeSpan executionBudget, TimeSpan remainingToBackstop)
        {
            if (remainingToBackstop <= TimeSpan.Zero)
            {
                return null;
            }

            return ClampToTimerLimit(
                remainingToBackstop < executionBudget ? remainingToBackstop : executionBudget);
        }

        /// <summary>
        /// Largest delay <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
        /// accepts (<c>Timer.MaxSupportedTimeout</c>, ~49.7 days). Anything larger
        /// throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        internal static readonly TimeSpan MaxTimerDelay =
            TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        /// <summary>
        /// Caps a deadline at what the timer can express. Reachable without any
        /// misconfiguration: an explicit per-step <c>TimeoutSeconds</c> is honoured
        /// as-is (operator intent, deliberately not subject to the engine ceiling)
        /// and it is an <see cref="int"/> of SECONDS, so a large one is ~68 years —
        /// past which the arm threw and failed the wave at dispatch with a raw
        /// "Parameter 'delay'". A timeout that far out is indistinguishable from no
        /// timeout, so capping it loses nothing an operator can observe.
        /// </summary>
        internal static TimeSpan ClampToTimerLimit(TimeSpan delay)
            => delay > MaxTimerDelay ? MaxTimerDelay : delay;
    }

    /// <summary>
    /// B3 — watches the target's live connection while a wave attempt is
    /// awaited. After a CONTINUOUS disconnect of
    /// <see cref="EngineOptions.AgentDisconnectWaveGrace"/> the pending
    /// sub-plan slot is cancelled, resolving the wave as a failure ("agent
    /// disconnected") into the deployment's BestEffort/Atomic failure mode.
    /// A reconnect within the grace resets the clock: the B2 agent reconnects
    /// with unbounded retry and FLUSHES its buffered wave results, which
    /// resolves the wave normally — this monitor only gives up on agents that
    /// stay gone. Grace deliberately exceeds the hub's 30 s offline-marking
    /// grace for exactly that reason. Cancelled attempts retire their
    /// DispatchId (B2), so a later flush of that attempt is swallowed as
    /// stale rather than corrupting a re-dispatched attempt.
    /// </summary>
    private async Task MonitorAgentConnectionDuringWaveAsync(
        Guid deploymentId, Guid targetId, CancellationToken ct)
    {
        var grace = engineOptions.Value.AgentDisconnectWaveGrace;
        if (grace <= TimeSpan.Zero)
        {
            return; // disconnect monitor disabled; the wave deadline still applies
        }

        // Sample fast enough that short (test) graces work, slow enough to be
        // free in production — one registry lookup per poll per in-flight wave.
        var poll = TimeSpan.FromMilliseconds(
            Math.Clamp(grace.TotalMilliseconds / 4, 25, 5_000));

        DateTimeOffset? disconnectedSince = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(poll, timeProvider, ct).ConfigureAwait(false);

                if (registry.HasConnectionFor(targetId))
                {
                    disconnectedSince = null;
                    continue;
                }

                var now = timeProvider.GetUtcNow();
                disconnectedSince ??= now;
                if (now - disconnectedSince < grace)
                {
                    continue;
                }

                logger.LogWarning(
                    "Deployment {Id}: target {Target} has been disconnected for {Grace}; " +
                    "cancelling the in-flight wave (resolves per the deployment's failure mode).",
                    deploymentId, targetId, grace);
                subPlans.Cancel(
                    deploymentId, targetId,
                    $"Agent disconnected mid-wave and did not reconnect within {grace}.");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Attempt ended (agent completed / deadline / shutdown) — done.
        }
    }

    /// <summary>
    /// M14.4 — emits one <c>Deployment.ParallelOutputCollision</c> audit +
    /// warning log line per output-variable name written by more than one
    /// parallel sibling in the same wave. The orchestrator calls this
    /// after a target wave's per-step boundary reports have drained.
    /// Server-side waves don't capture <c>Set-OctopusVariable</c> output
    /// today (the M14.3 retry helper documents this gap), so this
    /// helper sees no input from server waves.
    /// </summary>
    private static async Task EmitWaveCollisionsAsync(
        IReadOnlyList<SubPlanStepResult> perStepResults,
        IReadOnlyList<DeploymentStepPlan> waveSteps,
        ServerTask deployment,
        TaskAuditVocabulary vocab,
        KrakenDbContext db,
        IAuditLog auditLog,
        LogSequencer logSeq,
        CancellationToken ct)
    {
        if (perStepResults.Count == 0)
        {
            return;
        }

        // Build a SortOrder-ordered iteration over the per-step buckets so
        // last-writer-wins resolves deterministically by StepIndex (==
        // SortOrder rank in the process).
        var ordered = perStepResults
            .Where(r => r.Outputs.Count > 0)
            .OrderBy(r => r.StepIndex)
            .Select(r => (r.StepName,
                          (IReadOnlyDictionary<string, string>)r.Outputs))
            .ToArray();
        if (ordered.Length == 0)
        {
            return;
        }

        var collisions = DeploymentOutputCollisionDetector.Detect(ordered);
        if (collisions.Count == 0)
        {
            return;
        }

        foreach (var c in collisions)
        {
            var writersDesc = string.Join(", ",
                c.Writers.Select(w => $"{w.StepName}={Elide(w.Value)}"));
            var loserDesc = string.Join(", ",
                c.Losers.Select(w => $"{w.StepName}={Elide(w.Value)}"));

            await logSeq.AppendAsync(-1, null, "warning",
                $"Output variable '{c.VariableName}' was set by " +
                $"parallel siblings [{writersDesc}]; last-writer-wins " +
                $"in SortOrder → {c.Winner.StepName}={Elide(c.Winner.Value)}.", ct).ConfigureAwait(false);
            await auditLog.RecordAsync(
                vocab.ParallelOutputCollision,
                subjectType: vocab.SubjectType,
                subjectId:   deployment.Id.ToString(),
                details:     $"Variable={c.VariableName}, " +
                             $"Wave=[{string.Join(", ", waveSteps.Select(p => p.Name))}], " +
                             $"Winner={c.Winner.StepName}, " +
                             $"Losers=[{loserDesc}]",
                ct: ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        static string Elide(string v) =>
            v.Length <= 60 ? v : v[..57] + "...";
    }

    // ── M14.5 step-outcome aggregate ────────────────────────────────────

    /// <summary>
    /// M14.5 — upsert a <see cref="TaskStepOutcome"/> row keyed by
    /// (DeploymentId, StepIndex, TargetId). Wave-level target retries
    /// re-dispatch the whole sub-plan so a step's outcome can be
    /// reported multiple times; the upsert keeps a single row per
    /// step-per-target reflecting the final attempt.
    ///
    /// <para>
    /// M-RollingDeployments groundwork: the key now includes
    /// <paramref name="targetId"/> so multi-target dispatch writes one
    /// outcome row per target per step. <paramref name="targetId"/>
    /// stays null on this commit (the orchestrator still single-targets
    /// every dispatch); Phase 1b's orchestrator rewrite starts passing
    /// the real target id.
    /// </para>
    ///
    /// <para>
    /// Caller is responsible for <c>SaveChangesAsync</c> — the helper
    /// queues the insert/update on the shared context without flushing,
    /// matching the rest of the orchestrator's write pattern (audit + log
    /// rows accumulate then flush together).
    /// </para>
    /// </summary>
    // internal (not private) so the Space-scope regression test can drive it
    // directly — InternalsVisibleTo KrakenDeploy.Server.Data.Tests.
    internal static async Task UpsertStepOutcomeAsync(
        KrakenDbContext db,
        Guid deploymentId,
        int stepIndex,
        string stepName,
        StepOutcomeKind outcome,
        int attemptCount,
        string? errorMessage,
        DateTimeOffset? startedUtc,
        DateTimeOffset completedUtc,
        bool isServerSide,
        bool required,
        CancellationToken ct,
        Guid? targetId = null)
    {
        // IgnoreQueryFilters: the worker runs under DefaultSpaceId (no HTTP Space
        // context — see the stamp below), but the outcome row carries the task's
        // REAL Space. Without this, the filtered read misses the existing row for a
        // non-Default-Space task and the method would attempt a duplicate INSERT
        // that the (task_id, step_index, target_id) unique index rejects. task_id is
        // Space-safe (composite FK to server_tasks), so the lookup stays scoped.
        var existing = await db.TaskStepOutcomes
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o =>
                o.TaskId == deploymentId
                && o.StepIndex == stepIndex
                && o.TargetId == targetId, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.StepName      = stepName;
            existing.Outcome       = outcome;
            existing.AttemptCount  = attemptCount;
            existing.ErrorMessage  = errorMessage;
            existing.StartedUtc    = startedUtc;
            existing.CompletedUtc  = completedUtc;
            existing.IsServerSide  = isServerSide;
            existing.Required      = required;
            existing.TargetId      = targetId;
            return;
        }

        // Worker context has no real Space (DefaultSpaceId); resolve + stamp the
        // deployment's Space explicitly so the interceptor leaves it alone.
        var spaceId = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == deploymentId)
            .Select(t => t.SpaceId)
            .FirstAsync(ct)
            .ConfigureAwait(false);
        db.TaskStepOutcomes.Add(new TaskStepOutcome
        {
            SpaceId      = spaceId,
            TaskId       = deploymentId,
            StepIndex    = stepIndex,
            StepName     = stepName,
            Outcome      = outcome,
            AttemptCount = attemptCount,
            ErrorMessage = errorMessage,
            StartedUtc   = startedUtc,
            CompletedUtc = completedUtc,
            IsServerSide = isServerSide,
            Required     = required,
            TargetId     = targetId,
        });
    }

    // ── M-RollingDeployments Phase 1b: per-target dispatch context ──────

    /// <summary>
    /// Per-target dispatch state assembled before the wave loop runs.
    /// Holds the target-scoped variable bag + the flatten result + the
    /// plan envelope the agent receives + the per-plan-index snapshot
    /// lookup the orchestrator uses on the wave-dispatch hot path.
    ///
    /// <para>
    /// Built once per target by <see cref="BuildTargetDispatchContextAsync"/>
    /// and indexed by target id in the orchestrator. Server waves use the
    /// canonical (== first) target's context as their machine context;
    /// target waves index by the wave's target id.
    /// </para>
    /// </summary>
    internal sealed record TargetDispatchContext(
        DeploymentTarget Target,
        VariableDictionary VarDict,
        IReadOnlyDictionary<string, string> FlatVars,
        IReadOnlyDictionary<string, string[]> ArrayVars,
        DeploymentPlan Plan,
        DeploymentStepPlan[] Steps,
        StepSnapshot[] SnapshotByPlanIndex,
        DeploymentPlanFlattener.FlattenResult Flatten,
        IReadOnlyCollection<string> SensitiveVariableNames);

    /// <summary>
    /// Resolves project variables for a single target, builds the Octostache
    /// dictionary + system variables, runs the M15.2 flattener with the
    /// per-target variable bag (so any per-target Octostache substitutions
    /// inside step Configs resolve to that target's value), then overlays
    /// referenced-package resolution. The structural waves layout is
    /// snapshot-driven and therefore identical across targets — only the
    /// substituted Configs differ.
    /// </summary>
    // Static + logger-as-parameter so the offline regenerate path
    // (OfflineDropBundleBuilder) can build the exact same plan the online +
    // dispatch paths do, without a DeploymentWorker instance and without
    // duplicating this ~130-line body.
    internal static async Task<TargetDispatchContext> BuildTargetDispatchContextAsync(
        ILogger logger,
        ServerTask deployment,
        ITaskDispatchSource source,
        DeploymentTarget target,
        IReadOnlyList<StepSnapshot> snapshotSteps,
        VariableService variableService,
        string? serverBaseUrl,
        IDbContextFactory<KrakenDbContext> dbFactory,
        CancellationToken ct)
    {
        // D1: resolve deployment-wide variables + per-step deltas via the
        // kind-correct source — a deployment reads the FROZEN
        // Release.VariableSnapshot (channel-scoped), a runbook run resolves LIVE
        // from the project's current variables. The per-step phase is skipped
        // internally when no variable is step-scoped (the common case).
        var stepIdsAndNames = snapshotSteps
            .Select(s => (s.Id, s.Name))
            .ToList();

        // Load tenant tag IDs for variable scope matching (bit 4 specificity).
        IReadOnlyList<Guid>? tenantTagIds = null;
        if (deployment.TenantId is { } tenantIdForScope)
        {
            await using var scopeDb = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            tenantTagIds = await TagService
                .GetTenantTagIdsAsync(scopeDb, tenantIdForScope, ct).ConfigureAwait(false);
        }

        var stepResolution = await source.ResolveVariablesAsync(
            variableService, target, stepIdsAndNames, tenantTagIds, ct).ConfigureAwait(false);
        var rawVars = stepResolution.DeploymentWide;

        if (!string.IsNullOrEmpty(deployment.FormValues))
        {
            var prompted = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(deployment.FormValues);
            if (prompted is { Count: > 0 })
            {
                foreach (var (k, v) in prompted)
                {
                    rawVars[k] = v;
                }

                foreach (var (_, delta) in stepResolution.PerStepDelta)
                {
                    foreach (var (k, v) in prompted)
                    {
                        if (delta.ContainsKey(k))
                        {
                            delta[k] = v;
                        }
                    }
                }
            }
        }

        var varDict = new VariableDictionary();

        // Octopus.Deployment.Tenant.Tags — canonical strings of the tenant's
        // applied tags (extended tag sets; polymorphic table, so resolved by
        // query rather than a navigation). Short-lived context: this runs once
        // per target at plan-build time, only for tenanted deployments.
        IReadOnlyList<string>? tenantTagCanonicals = null;
        if (deployment.TenantId is { } tenantIdForTags)
        {
            await using var tagDb = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            tenantTagCanonicals = await TagService
                .GetTenantTagCanonicalsAsync(tagDb, tenantIdForTags, ct).ConfigureAwait(false);
        }

        var systemVars = source.BuildSystemVariables(target, serverBaseUrl, tenantTagCanonicals);

        var flatVars = new Dictionary<string, string>(systemVars, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, val) in systemVars)
        {
            varDict[k] = val;
        }

        var arrayVars = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in rawVars)
        {
            if (value.StartsWith('['))
            {
                // StringArray: try to parse as JSON array.
                try
                {
                    var items = JsonSerializer.Deserialize<string[]>(value) ?? [];
                    arrayVars[name] = items;

                    var joined = string.Join(", ", items);
                    flatVars[name] = joined;
                    varDict[name] = joined;

                    // #{name[i]} index keys are added in one pass after the loop
                    // (VariableDictionaryExtensions.AddArrayIndexEntries) so the
                    // online varDict and the offline runner's condition bag
                    // generate identical keys — single source of truth for the
                    // name[i] format.
                    continue;
                }
                catch (JsonException)
                {
                    // Not valid JSON — treat as plain string.
                }
            }

            flatVars[name] = value;
            varDict[name] = value;
        }

        // Expand every StringArray into #{name[i]} keys via the shared formatter
        // — the same call the offline runner's condition bag uses, so an indexed
        // Variable run-condition (e.g. #{Arr[0]}) makes identical Run/Skip
        // decisions online and offline.
        VariableDictionaryExtensions.AddArrayIndexEntries(varDict, arrayVars);

        var flatten = DeploymentPlanFlattener.Flatten(
            snapshotSteps, arrayVars, varDict);
        var steps = flatten.Plans;
        var snapshotByPlanIndex = flatten.SnapshotByPlanIndex;

        // PackageReferenceResolver is async + DB-backed so it can't run
        // inside the pure-function flattener. Overlay per emitted plan.
        for (var i = 0; i < steps.Length; i++)
        {
            var referenced = await PackageReferenceResolver
                .ResolveAsync((Dictionary<string, string>)steps[i].Config,
                              dbFactory, logger, ct)
                .ConfigureAwait(false);
            if (referenced.Count > 0)
            {
                steps[i] = steps[i] with { ReferencedPackages = referenced };
            }

            // Per-step variable scope: attach the step's delta (keyed by source
            // snapshot Id) so the agent overlays it onto the deployment-wide vars.
            if (stepResolution.PerStepDelta.Count > 0
                && stepResolution.PerStepDelta.TryGetValue(snapshotByPlanIndex[i].Id, out var stepDelta))
            {
                steps[i] = steps[i] with { StepVariables = stepDelta };
            }
        }

        var plan = new DeploymentPlan(
            DeploymentId:    deployment.Id,
            EnvironmentName: deployment.Environment.Name,
            Steps:           steps,
            Variables:       flatVars,
            ArrayVariables:  arrayVars,
            SensitiveVariableNames: stepResolution.SensitiveNames,
            // F2 — the target's own concurrency policy, resolved at plan-build time
            // (a flip applies to the next dispatch, not to work already queued on
            // the agent). Never relaxes the F1 (project, env, tenant) serialization.
            AllowParallelTaskExecution: target.AllowParallelTaskExecution);

        return new TargetDispatchContext(
            Target:              target,
            VarDict:             varDict,
            FlatVars:            flatVars,
            ArrayVars:           arrayVars,
            Plan:                plan,
            Steps:               steps,
            SnapshotByPlanIndex: snapshotByPlanIndex,
            Flatten:             flatten,
            SensitiveVariableNames: stepResolution.SensitiveNames);
    }

    /// <summary>
    /// M-RollingDeployments Phase 1b/3 — outcome of fanning out one target
    /// wave across the currently-alive targets. Aggregates per-target
    /// completion enough for the orchestrator to apply per-target drop-out
    /// + non-required failure accumulation.
    /// <para>
    /// Phase 3 swap: <c>DroppedTargets</c> replaces the single
    /// <c>AbortedRequired</c> tuple. Each target with a Required failure
    /// OR an offline-mid-wave outage is now a separate drop-out entry;
    /// the orchestrator removes those targets from <c>aliveTargets</c> and
    /// keeps running with the survivors. The deployment fails ONLY when
    /// every alive target has dropped.
    /// </para>
    /// </summary>
    private sealed record TargetWaveAggregateResult(
        IReadOnlyList<DroppedTargetInfo> DroppedTargets,
        // Target ids that had a NON-required step failure this wave (the target
        // survives but is "soft-failed"). In BestEffort mode only this target's
        // later Condition=Success steps skip; in Atomic mode any failure taints
        // the whole deployment via the global hasFailed flag instead.
        IReadOnlyList<Guid> SoftFailedTargetIds);

    /// <summary>
    /// One target that dropped out of subsequent waves. Carries enough
    /// detail for the operator-facing audit row + the deployment log
    /// summary the orchestrator emits at drop-out time.
    /// </summary>
    private sealed record DroppedTargetInfo(
        DeploymentTarget Target,
        DropReason Reason,
        string? StepName,
        string? Error);

    private enum DropReason
    {
        /// <summary>A Required step failed on this target inside the
        /// current wave. The agent's per-step boundary report identifies
        /// which step; the M14.0..3 group-level pessimistic fallback is
        /// still in play when the agent didn't report per-step.</summary>
        RequiredStepFailed,

        /// <summary>The target's agent went offline between dispatches
        /// (the SignalR registry has no connection id for it). The wave's
        /// pre-flight check catches this BEFORE any RPC; the target
        /// drops + the deployment continues with the rest.</summary>
        AgentOffline,
    }

    /// <summary>
    /// Fans out a single target wave across <paramref name="targets"/> in
    /// parallel. Each target dispatches its own sub-plan (with its
    /// per-target variable bag) and the agent runs the wave's steps in
    /// parallel; per-step boundary reports drain into the registry slot
    /// keyed by (deployment, target).
    ///
    /// <para>
    /// After Task.WhenAll the orchestrator:
    /// <list type="number">
    ///   <item>Persists per-(target, step) outcome rows + non-required /
    ///         timeout audits per target.</item>
    ///   <item>Runs the collision detector per target.</item>
    ///   <item>Applies the Required gate: first Required failure on any
    ///         target trips <see cref="TargetWaveAggregateResult.AbortedRequired"/>
    ///         and the caller fails the whole deployment.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Cancellation: when one target's wave returns failure, peers keep
    /// running until they settle naturally — we don't pre-emptively cancel
    /// the others to avoid leaving half-completed state behind. Required
    /// gating is applied AFTER WhenAll resolves; the conservative Phase 1b
    /// semantic is "any Required failure on any target aborts" but we
    /// don't kill in-flight peers.
    /// </para>
    /// </summary>
    private async Task<TargetWaveAggregateResult> DispatchTargetWaveAcrossTargetsAsync(
        WavePartitioner.Wave wave,
        List<DeploymentTarget> targets,
        Dictionary<Guid, TargetDispatchContext> contexts,
        StepSnapshot[] canonicalSnapshotByPlanIndex,
        IReadOnlyDictionary<Guid, StepSnapshot> snapshotById,
        DeploymentFailureMode failureMode,
        bool deploymentHasFailed,
        HashSet<Guid> softFailedTargets,
        ServerTask deployment,
        TaskAuditVocabulary vocab,
        KrakenDbContext db,
        IAuditLog auditLog,
        LogSequencer logSeq,
        DeploymentOutputAccumulator outputAccumulator,
        CancellationToken ct)
    {
        // ── Per-target Condition + role filter ─────────────────────────
        // Skipped outcomes are recorded inline so the Steps tab carries
        // the reason; surviving step lists feed the parallel dispatch.
        //
        // Phase 3: offline targets are collected as drop-outs (rather than
        // aborting the whole deployment). The caller removes them from
        // aliveTargets and continues with the rest.
        var dispatchPlan = new List<(TargetDispatchContext Ctx, List<DeploymentStepPlan> Steps)>(
            targets.Count);
        var droppedTargets = new List<DroppedTargetInfo>();
        foreach (var target in targets)
        {
            var ctx = contexts[target.Id];

            // Per-target failed state for Condition evaluation:
            //  - Atomic: the deployment-global flag (any failure anywhere taints
            //    every target, so cleanup runs farm-wide and Success steps skip).
            //  - BestEffort: the global flag (server-level failures are deployment-
            //    wide) OR this target's own soft-failure — a non-required failure
            //    on another target does NOT skip this target's Success steps.
            var targetHasFailed = deploymentHasFailed
                || (failureMode == DeploymentFailureMode.BestEffort
                    && softFailedTargets.Contains(target.Id));

            var stepsToRun = new List<DeploymentStepPlan>(wave.Steps.Count);
            foreach (var s in wave.Steps)
            {
                var snapshot = ctx.SnapshotByPlanIndex[s.Index];
                var decision = StepConditionEvaluator.Evaluate(
                    snapshot.Condition,
                    snapshot.ConditionVariableExpression,
                    targetHasFailed,
                    ctx.VarDict);
                if (decision.Action == StepConditionEvaluator.Action.Skip)
                {
                    await LogAndAuditStepSkippedAsync(
                        db, auditLog, logSeq, deployment, vocab, snapshot, decision, ct)
                        .ConfigureAwait(false);
                    await UpsertStepOutcomeAsync(
                        db, deployment.Id, s.Index, snapshot.Name,
                        StepOutcomeKind.Skipped, attemptCount: 0,
                        errorMessage: decision.Reason,
                        startedUtc:   null,
                        completedUtc: DateTimeOffset.UtcNow,
                        isServerSide: false,
                        required:     snapshot.Required, ct,
                        targetId:     target.Id).ConfigureAwait(false);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    continue;
                }
                stepsToRun.Add(s);
            }

            if (stepsToRun.Count == 0)
            {
                continue;
            }

            var connectionId = registry.GetConnectionId(target.Id);

            // P3-8 Phase 5 — cross-account dispatch guard (defense-in-depth). The
            // connection's account is recorded at connect (host-derived). In multi-
            // account a live connection whose account differs from this deployment's
            // account must never receive the plan. Structurally this cannot happen
            // (a target id is globally unique and validated against the connecting
            // account's DB at connect), so a hit here means an upstream invariant
            // broke — block it like an offline target rather than push cross-tenant.
            string? dropError = null;
            if (connectionId is null)
            {
                dropError = "Target agent offline at dispatch time.";
            }
            else if (_dispatchAccountId.Value != Guid.Empty
                     && registry.GetAccountForTarget(target.Id) != _dispatchAccountId.Value)
            {
                logger.LogError(
                    "Cross-account dispatch blocked for deployment {Deployment}: target " +
                    "{Target}'s live connection belongs to account {ConnectionAccount}, not " +
                    "the dispatch account {DispatchAccount}; dropping target.",
                    deployment.Id, target.Id,
                    registry.GetAccountForTarget(target.Id), _dispatchAccountId.Value);
                dropError = "Cross-account connection blocked at dispatch.";
            }

            if (dropError is not null)
            {
                // Record a drop-out (instead of aborting). Steps tab gets one Failed
                // outcome per remaining step so operators see "dropped at wave X".
                var droppedAt = DateTimeOffset.UtcNow;
                foreach (var p in stepsToRun)
                {
                    var snap = ctx.SnapshotByPlanIndex[p.Index];
                    await UpsertStepOutcomeAsync(
                        db, deployment.Id, p.Index, snap.Name,
                        StepOutcomeKind.Failed, attemptCount: 0,
                        errorMessage: dropError,
                        startedUtc:   null,
                        completedUtc: droppedAt,
                        isServerSide: false,
                        required:     snap.Required, ct,
                        targetId:     target.Id).ConfigureAwait(false);
                }
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                droppedTargets.Add(new DroppedTargetInfo(
                    Target:   target,
                    Reason:   DropReason.AgentOffline,
                    StepName: null,
                    Error:    dropError));
                continue;
            }

            dispatchPlan.Add((ctx, stepsToRun));
        }

        if (dispatchPlan.Count == 0)
        {
            // Every alive target was either fully Condition-skipped or
            // dropped offline. Return whatever drop-outs we accumulated;
            // the outer loop applies them to aliveTargets + fails the
            // deployment when the set goes empty.
            return new TargetWaveAggregateResult(
                DroppedTargets:      droppedTargets,
                SoftFailedTargetIds: []);
        }

        // ── M-RollingDeployments Phase 2 — resolve effective rolling window ──
        // The cap comes from a Kraken.StepGroup ancestor's typed MaxParallelism
        // column (D3). When every step in the wave shares a single rolling
        // ancestor with a positive cap, the resolver returns it (Resolved);
        // otherwise no batching, WITH a reason. See RollingWindowResolver for the
        // precise semantic + edge cases.
        var rolling = RollingWindowResolver.ResolveWaveRollingWindow(
            wave.Steps, canonicalSnapshotByPlanIndex, snapshotById);
        var maxParallelism = rolling.Cap;
        var rollingGroupName = rolling.RollingGroupName;

        // D3 RIDER — rolling visibility. A rolling group is present but the
        // fan-out was NOT batched (malformed cap from imported/legacy data, or
        // the wave spans multiple rolling groups): warn into the task log +
        // audit so the operator can fix the source data, rather than silently
        // fanning out to every target. The no-cap fallback itself is deliberate
        // (1-at-a-time would be worse); this just makes it audible.
        if (rolling.Reason is RollingCapReason.Malformed or RollingCapReason.MixedAncestors)
        {
            var reasonDetail = rolling.Reason == RollingCapReason.Malformed
                ? $"the rolling window on group '{rollingGroupName}' is not a positive integer"
                : "the wave's steps do not all belong to the same rolling group";
            await logSeq.AppendAsync(-1, null, "warning",
                $"--- Rolling batching disabled: {reasonDetail}; all " +
                $"{dispatchPlan.Count.ToString(CultureInfo.InvariantCulture)} target(s) run in " +
                "one batch (no concurrency cap). ---", ct).ConfigureAwait(false);
            await auditLog.RecordAsync(
                vocab.RollingBatchingDisabled,
                subjectType: vocab.SubjectType,
                subjectId:   deployment.Id.ToString(),
                details:     $"RollingGroup={rollingGroupName}, " +
                             $"Reason={rolling.Reason}, " +
                             $"Targets={dispatchPlan.Count.ToString(CultureInfo.InvariantCulture)}",
                ct: ct).ConfigureAwait(false);
        }

        var batches = maxParallelism is null
            ? [dispatchPlan]
            : RollingWindowResolver.Chunk(dispatchPlan, maxParallelism.Value);

        // D3 RIDER — informational nudge: a Resolved cap that meets or exceeds
        // the wave's target count never fires (Chunk returns a single batch).
        // Surface it so an operator who set a too-large window understands why
        // there's no batching — distinct from the silent no-rolling-group case.
        if (rolling.Reason == RollingCapReason.Resolved
            && maxParallelism is not null
            && batches.Count == 1)
        {
            await logSeq.AppendAsync(-1, null, "info",
                $"--- Rolling window {maxParallelism.Value.ToString(CultureInfo.InvariantCulture)} on " +
                $"'{rollingGroupName}' is >= the {dispatchPlan.Count.ToString(CultureInfo.InvariantCulture)} " +
                "target(s) in this wave; the cap has no effect (all targets run at once). ---",
                ct).ConfigureAwait(false);
        }

        // Batching is only operator-visible when we ACTUALLY split — a cap
        // that meets or exceeds the dispatch count degrades to a single batch.
        var batchingActive = batches.Count > 1 && rollingGroupName is not null;

        var softFailedTargetIds = new List<Guid>();

        for (var batchIdx = 0; batchIdx < batches.Count; batchIdx++)
        {
            // ── E2: ownership check BETWEEN rolling batches ──────────────────
            // Rolling batches run sequentially. Pre-E2 the only status checks
            // were at dequeue and wave boundaries, so a zombie orchestration kept
            // dispatching batch after batch even after the operator cancelled or
            // the reconciler interrupted the run. Re-check the same ownership
            // predicate before every batch after the first (the wave boundary
            // just cleared the first). A lost lease cancels `ct` (orchestrationCt)
            // so the projection throws OCE → the worker's teardown catch stops the
            // run; an operator cancel / reconciler interrupt flips the status so
            // the predicate returns false and we stop dispatching further batches.
            if (batchIdx > 0
                && !await IsTaskStillRunningAsync(db, deployment.Id, ct).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Deployment {Id}: no longer Running — stopping before rolling batch {Batch} of {Total}.",
                    deployment.Id, batchIdx + 1, batches.Count);
                break;
            }

            var batch = batches[batchIdx];

            if (batchingActive)
            {
                var batchTargets = string.Join(", ", batch.Select(t => t.Ctx.Target.Name));
                var waveNames = string.Join(", ", wave.Steps.Select(s => s.Name));
                await auditLog.RecordAsync(
                    vocab.RollingBatchStarted,
                    subjectType: vocab.SubjectType,
                    subjectId:   deployment.Id.ToString(),
                    details:     $"RollingGroup={rollingGroupName}, " +
                                 $"Batch={(batchIdx + 1).ToString(CultureInfo.InvariantCulture)}/" +
                                 $"{batches.Count.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"BatchSize={batch.Count.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"MaxParallelism={maxParallelism!.Value.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"Targets=[{batchTargets}], " +
                                 $"Wave=[{waveNames}]",
                    ct: ct).ConfigureAwait(false);
                await logSeq.AppendAsync(-1, null, "info",
                    $"--- Rolling batch " +
                    $"{(batchIdx + 1).ToString(CultureInfo.InvariantCulture)} of " +
                    $"{batches.Count.ToString(CultureInfo.InvariantCulture)} for " +
                    $"'{rollingGroupName}' (window={maxParallelism!.Value}): " +
                    $"[{batchTargets}] ---", ct).ConfigureAwait(false);
            }

            var batchOutcome = await DispatchOneBatchAsync(
                batch, deployment, vocab, db, auditLog, logSeq, outputAccumulator, ct).ConfigureAwait(false);

            // Phase 3 — accumulate drop-outs from this batch into the
            // wave's aggregate; subsequent batches still run (a failed
            // target in batch K is local to that target, not a halt
            // signal for batches K+1..end). Operators who want canary
            // semantics across batches can rely on per-target drop-out
            // emptying aliveTargets if every batch's targets fail.
            droppedTargets.AddRange(batchOutcome.DroppedTargets);
            softFailedTargetIds.AddRange(batchOutcome.SoftFailedTargetIds);

            if (batchingActive)
            {
                var failedTargetNames = string.Join(", ", batchOutcome.FailedTargets);
                await auditLog.RecordAsync(
                    vocab.RollingBatchCompleted,
                    subjectType: vocab.SubjectType,
                    subjectId:   deployment.Id.ToString(),
                    details:     $"RollingGroup={rollingGroupName}, " +
                                 $"Batch={(batchIdx + 1).ToString(CultureInfo.InvariantCulture)}/" +
                                 $"{batches.Count.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"Success={(batchOutcome.DroppedTargets.Count == 0
                                               ? "true" : "false")}, " +
                                 $"FailedTargets=[{failedTargetNames}]",
                    ct: ct).ConfigureAwait(false);
            }
        }

        return new TargetWaveAggregateResult(
            DroppedTargets:      droppedTargets,
            SoftFailedTargetIds: softFailedTargetIds);
    }

    /// <summary>
    /// One rolling-batch's worth of (target, stepsToRun) tuples — the
    /// existing per-target dispatch loop, factored out so the outer
    /// batched dispatch can call it once per batch.
    /// <para>
    /// Phase 3: <see cref="DroppedTargets"/> replaces the singular
    /// <c>RequiredFailure</c>. Every target that hit a Required step
    /// failure inside this batch is recorded as a drop-out; the caller
    /// removes those from the surviving target set and continues with
    /// the rest.
    /// </para>
    /// </summary>
    private sealed record BatchOutcome(
        IReadOnlyList<DroppedTargetInfo> DroppedTargets,
        IReadOnlyList<Guid> SoftFailedTargetIds,
        IReadOnlyList<string> FailedTargets);

    private async Task<BatchOutcome> DispatchOneBatchAsync(
        IReadOnlyList<(TargetDispatchContext Ctx, List<DeploymentStepPlan> Steps)> batch,
        ServerTask deployment,
        TaskAuditVocabulary vocab,
        KrakenDbContext db,
        IAuditLog auditLog,
        LogSequencer logSeq,
        DeploymentOutputAccumulator outputAccumulator,
        CancellationToken ct)
    {
        var waveStartedUtc = DateTimeOffset.UtcNow;
        var dispatchTasks = batch.Select(async tuple =>
        {
            var (ctx, stepsToRun) = tuple;
            var connectionId = registry.GetConnectionId(ctx.Target.Id)!;
            // B4: this wave's sub-plan carries every prior wave's captured
            // outputs for THIS target (merged Variables + sensitive names) —
            // the agent's handlers and $OctopusParameters resolve them exactly
            // like the offline whole-plan path.
            var augmentedPlan = outputAccumulator.AugmentPlanForTarget(ctx.Target.Id, ctx.Plan);
            var (waveResult, waveTimedOut, perStepResults) = await DispatchTargetWaveAsync(
                augmentedPlan, stepsToRun, ctx.SnapshotByPlanIndex, deployment, vocab,
                ctx.Target.Id, connectionId, auditLog, logSeq, ct)
                .ConfigureAwait(false);
            return (ctx, stepsToRun, waveResult, waveTimedOut, perStepResults);
        }).ToArray();

        var perTargetOutcomes = await Task.WhenAll(dispatchTasks).ConfigureAwait(false);
        var waveCompletedUtc = DateTimeOffset.UtcNow;

        var softFailedTargetIds = new List<Guid>();
        var droppedTargets = new List<DroppedTargetInfo>();
        var failedTargets = new List<string>();

        foreach (var (ctx, stepsToRun, waveResult, waveTimedOut, rawPerStepResults) in perTargetOutcomes)
        {
            // B6: range-guard the agent-supplied step indices ONCE before every
            // consumer below indexes SnapshotByPlanIndex with them. The hub
            // already rejects negatives at the trust boundary, but it cannot
            // know the plan's size — an out-of-range index (buggy or malicious
            // agent) would otherwise throw inside this fold and abort the whole
            // cross-target deployment.
            var perStepResults = rawPerStepResults;
            if (perStepResults.Any(r => r.StepIndex < 0 || r.StepIndex >= ctx.SnapshotByPlanIndex.Length))
            {
                foreach (var bad in perStepResults.Where(r =>
                    r.StepIndex < 0 || r.StepIndex >= ctx.SnapshotByPlanIndex.Length))
                {
                    logger.LogWarning(
                        "Discarding step report with out-of-range index {Index} " +
                        "(plan has {Count} steps) for deployment {Id}, target {Target}.",
                        bad.StepIndex, ctx.SnapshotByPlanIndex.Length, deployment.Id, ctx.Target.Id);
                }
                perStepResults = perStepResults
                    .Where(r => r.StepIndex >= 0 && r.StepIndex < ctx.SnapshotByPlanIndex.Length)
                    .ToList();
            }

            var reportedIndices = new HashSet<int>();
            foreach (var r in perStepResults)
            {
                var snap = ctx.SnapshotByPlanIndex[r.StepIndex];
                var kind = r.Success ? StepOutcomeKind.Succeeded
                                      : StepOutcomeKind.Failed;
                await UpsertStepOutcomeAsync(
                    db, deployment.Id, r.StepIndex, snap.Name,
                    kind, attemptCount: 1,
                    errorMessage: r.Success ? null : r.ErrorMessage,
                    startedUtc:   waveStartedUtc,
                    completedUtc: waveCompletedUtc,
                    isServerSide: false,
                    required:     snap.Required, ct,
                    targetId:     ctx.Target.Id).ConfigureAwait(false);
                reportedIndices.Add(r.StepIndex);
            }

            foreach (var p in stepsToRun)
            {
                if (reportedIndices.Contains(p.Index))
                {
                    continue;
                }
                var snap = ctx.SnapshotByPlanIndex[p.Index];
                var kind = waveTimedOut
                    ? StepOutcomeKind.TimedOut
                    : (waveResult.Success
                        ? StepOutcomeKind.Succeeded
                        : StepOutcomeKind.Failed);
                await UpsertStepOutcomeAsync(
                    db, deployment.Id, p.Index, snap.Name,
                    kind, attemptCount: 1,
                    errorMessage: kind == StepOutcomeKind.Succeeded ? null
                                  : waveResult.ErrorMessage
                                    ?? "Agent did not report step completion.",
                    startedUtc:   waveStartedUtc,
                    completedUtc: waveCompletedUtc,
                    isServerSide: false,
                    required:     snap.Required, ct,
                    targetId:     ctx.Target.Id).ConfigureAwait(false);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // B4: fold this target's captured outputs so LATER waves' dispatches
            // (and server waves / run conditions) see them. Captures fold
            // regardless of step success — parity with the agent's accumulator.
            foreach (var r in perStepResults)
            {
                outputAccumulator.RecordTargetStep(
                    ctx.Target.Id, r.StepName, r.Outputs, r.SensitiveOutputNames);
            }

            await EmitWaveCollisionsAsync(
                perStepResults, stepsToRun, deployment, vocab, db, auditLog, logSeq, ct)
                .ConfigureAwait(false);

            if (waveTimedOut)
            {
                var timeoutStep = stepsToRun
                    .Select(p => ctx.SnapshotByPlanIndex[p.Index])
                    .FirstOrDefault(snap => snap.TimeoutSeconds > 0);
                if (timeoutStep is not null)
                {
                    // Target waves are bounded by the wave deadline, not the E3
                    // DeployRelease ceiling, so the effective timeout is the step's
                    // own explicit TimeoutSeconds.
                    await LogAndAuditStepTimedOutAsync(
                        auditLog, logSeq, deployment, vocab, timeoutStep, timeoutStep.TimeoutSeconds, ct)
                        .ConfigureAwait(false);
                }
            }

            if (!waveResult.Success)
            {
                failedTargets.Add(ctx.Target.Name);

                // Phase 3 — identify whether ANY step failure on this
                // target was Required. If so, the target drops out of
                // subsequent waves. Required attribution comes from the
                // per-step boundary reports; M14.0..3 group-level
                // pessimistic fallback applies when no per-step report
                // landed.
                var failedSteps = perStepResults.Where(r => !r.Success).ToList();
                DeploymentStepPlan? thisTargetRequiredFailure = null;
                if (failedSteps.Count > 0)
                {
                    foreach (var failed in failedSteps)
                    {
                        var snap = ctx.SnapshotByPlanIndex[failed.StepIndex];
                        if (snap.Required)
                        {
                            thisTargetRequiredFailure = stepsToRun
                                .FirstOrDefault(p => p.Index == failed.StepIndex);
                            break;
                        }
                    }
                }
                else
                {
                    thisTargetRequiredFailure = stepsToRun
                        .FirstOrDefault(p => ctx.SnapshotByPlanIndex[p.Index].Required);
                }

                if (thisTargetRequiredFailure is not null)
                {
                    droppedTargets.Add(new DroppedTargetInfo(
                        Target:   ctx.Target,
                        Reason:   DropReason.RequiredStepFailed,
                        StepName: thisTargetRequiredFailure.Name,
                        Error:    waveResult.ErrorMessage));
                    continue;
                }

                if (failedSteps.Count > 0)
                {
                    foreach (var failed in failedSteps)
                    {
                        await LogAndAuditStepFailedNonRequiredAsync(
                            db, auditLog, logSeq, deployment, vocab,
                            ctx.SnapshotByPlanIndex[failed.StepIndex], ct)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    foreach (var p in stepsToRun)
                    {
                        await LogAndAuditStepFailedNonRequiredAsync(
                            db, auditLog, logSeq, deployment, vocab,
                            ctx.SnapshotByPlanIndex[p.Index], ct).ConfigureAwait(false);
                    }
                }
                softFailedTargetIds.Add(ctx.Target.Id);
            }
        }

        return new BatchOutcome(
            DroppedTargets:      droppedTargets,
            SoftFailedTargetIds: softFailedTargetIds,
            FailedTargets:       failedTargets);
    }
}
