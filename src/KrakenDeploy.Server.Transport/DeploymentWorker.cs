using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using KrakenDeploy.Contracts;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    Channel<Guid> queue,
    IAgentConnectionRegistry registry,
    IHubContext<AgentHub, IAgentHubClient> agentHub,
    ServerScriptStepRunner serverRunner,
    DeployReleaseStepRunner deployReleaseRunner,
    IPendingSubPlanRegistry subPlans,
    IServiceScopeFactory scopeFactory,
    DeploymentDiagnosisChannel diagnosisChannel,
    ILogger<DeploymentWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var deploymentId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Process fire-and-forget; don't block the reader loop.
            _ = DispatchAsync(deploymentId, stoppingToken);
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
        => DispatchAsync(deploymentId, ct);

    private async Task DispatchAsync(Guid deploymentId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var variableService = scope.ServiceProvider.GetRequiredService<VariableService>();
        var serverBaseUrl = scope.ServiceProvider
            .GetRequiredService<IConfiguration>()["Server:BaseUrl"];

        try
        {
            var deployment = await db.Deployments
                .Include(d => d.Release)
                    .ThenInclude(r => r.Project)
                .Include(d => d.Environment)
                .Include(d => d.Target)
                .Include(d => d.Targets)
                    .ThenInclude(a => a.Target!)
                .Include(d => d.Tenant)
                .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
                .ConfigureAwait(false);

            if (deployment is null)
            {
                logger.LogWarning("DeploymentWorker: deployment {Id} not found.", deploymentId);
                return;
            }

            // M14.3.1 — serialise log-sequence allocation. Single-threaded
            // until M14.4 introduced wave-parallel step execution; M-RollingDeployments
            // Phase 1b adds per-target parallel fan-out on top, so multiple
            // target waves write log entries concurrently through the same
            // sequencer.
            var logSeq = new LogSequencer(deployment);

            // ── M-RollingDeployments Phase 1b — resolve the target SET ──
            // The join collection is the source of truth post-Phase 1a.
            // Legacy single-target deployments still set deployment.Target +
            // deployment.TargetId; Phase 1a's migration backfilled the join
            // so reading deployment.Targets covers both shapes. Fallback
            // path: a deployment row whose join was deleted by hand still
            // dispatches single-target via the legacy nav.
            var targets = deployment.Targets
                .Where(a => a.Target is not null)
                .Select(a => a.Target!)
                .ToList();
            if (targets.Count == 0 && deployment.Target is not null)
            {
                targets.Add(deployment.Target);
            }

            if (deployment.TargetId is null && targets.Count == 0)
            {
                await FailAsync(db, deployment, "No target assigned to deployment.", ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── Deployment-freeze gate (M13.F.2) ────────────────────────────
            // Consulted before EVERY dispatch path (online + offline) so an
            // operator can't sneak past the gate by configuring a target as
            // OfflineDrop. The check is cheap (30 s cache; almost always a
            // dictionary lookup; only the first call per Space per 30 s
            // round-trips to the DB). Override is gated at the deployment-
            // CREATE endpoint via DeploymentFreezeOverride permission — by
            // the time we get here, the deployment has already been
            // authorised to run, so we just block on raw freeze match.
            var freezeService = scope.ServiceProvider.GetRequiredService<DeploymentFreezeService>();
            // NOTE: tenant-tag matching is intentionally left empty for now.
            // The Tenant aggregate doesn't carry a flat canonical-name
            // collection (tags live on TagSets) — wiring that resolution
            // here would require an extra join per dispatch. Project +
            // Environment scoping covers the common freeze use cases; the
            // tenant-tag dimension can light up when the tenant rendering
            // path needs the same lookup anyway.
            var blockingFreeze = await freezeService.FindBlockingFreezeAsync(
                spaceId:                 deployment.Release.Project.SpaceId,
                projectId:               deployment.Release.ProjectId,
                environmentId:           deployment.EnvironmentId,
                tenantTagCanonicalNames: null,
                ct:                      ct).ConfigureAwait(false);
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
                db.DeploymentLogEntries.Add(new DeploymentLogEntry
                {
                    SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
                    Sequence     = logSeq.Next(),
                    Timestamp    = DateTimeOffset.UtcNow,
                    Level        = "error",
                    Message      = msg,
                });
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

            // ── Offline drop path ───────────────────────────────────────────
            // Single-target by design — the bundle is a physical artifact
            // for a specific machine. Phase 1b refuses multi-target offline
            // drops; the per-machine bundle multiplication is a polish item
            // (no operator demand surfaced yet, and the offline-drop
            // workflow's manual delivery channel makes it an odd fit for
            // fan-out semantics anyway).
            if (deployment.Target?.TransportMode == TransportMode.OfflineDrop)
            {
                if (targets.Count > 1)
                {
                    await FailAsync(db, deployment,
                        "Offline-drop deployments must target a single machine. " +
                        "This deployment has multiple targets in its assignment set; " +
                        "either remove the extra targets or switch the primary " +
                        "target's TransportMode away from OfflineDrop.", ct)
                        .ConfigureAwait(false);
                    return;
                }
                await DispatchOfflineDropAsync(scope.ServiceProvider, db, deployment, ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── 1. Resolve project variables ─────────────────────────────────
            // Releases freeze the project's variables at cut time (Octopus-
            // style snapshot). The release's VariableSnapshotUpdatedUtc is
            // the "I have a valid snapshot" marker — it's set by
            // ReleaseService.CreateAsync and bumped by UpdateVariablesAsync.
            //
            // Pre-production policy (see docs/architecture.md): we don't
            // ship a soft-fallback for the null case. A null timestamp means
            // the row predates the feature and the deployment refuses to
            // run until an operator clicks "Update Variables" on the release.
            // No silent reads from live project variables — the whole point
            // of the snapshot is reproducibility.
            //
            // (Agent-connection check is deferred until after we know whether
            // any target-side steps need dispatching — fully-server-side
            // deployments don't require an online agent.)
            if (deployment.Release.VariableSnapshotUpdatedUtc is null)
            {
                var msg =
                    $"Release '{deployment.Release.Version}' has no variable snapshot. " +
                    "Open the release in the UI and click 'Update Variables' to freeze " +
                    "the project's current variables into the release, then re-deploy.";
                logger.LogError(
                    "Deployment {DeploymentId}: refusing to dispatch — release {ReleaseId} " +
                    "has no variable snapshot (pre-feature row).",
                    deployment.Id, deployment.Release.Id);
                db.DeploymentLogEntries.Add(new DeploymentLogEntry
                {
                    SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
                    Sequence     = logSeq.Next(),
                    Timestamp    = DateTimeOffset.UtcNow,
                    Level        = "error",
                    Message      = msg,
                });
                await FailAsync(db, deployment, msg, ct).ConfigureAwait(false);
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

            var snapshotSteps = deployment.Release.ProcessSnapshot
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
                    deployment, target, snapshotSteps, variableService,
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
            foreach (var w in canonicalCtx.Flatten.Warnings)
            {
                var eventType = w.Kind switch
                {
                    DeploymentPlanFlattener.WarningKind.ForEachEmpty
                        => AuditEventType.DeploymentForEachEmpty,
                    DeploymentPlanFlattener.WarningKind.ForEachUnresolved
                        => AuditEventType.DeploymentForEachUnresolved,
                    _ => AuditEventType.DeploymentForEachEmpty,
                };
                db.DeploymentLogEntries.Add(new DeploymentLogEntry
                {
                    SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
                    Sequence     = logSeq.Next(),
                    Timestamp    = DateTimeOffset.UtcNow,
                    Level        = w.Kind == DeploymentPlanFlattener.WarningKind.ForEachEmpty
                                       ? "info" : "error",
                    Message      = $"--- {w.Source.Name}: {w.Detail} ---",
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await auditLog.RecordAsync(
                    eventType,
                    subjectType: "Deployment",
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
                    AuditEventType.DeploymentMixedWaveRefused,
                    subjectType: "Deployment",
                    subjectId:   deployment.Id.ToString(),
                    details:     $"Wave=[{string.Join(", ", ex.WaveSteps.Select(s => s.Name))}], " +
                                 $"ServerSteps=[{string.Join(", ", ex.ServerStepNames)}], " +
                                 $"TargetSteps=[{string.Join(", ", ex.TargetStepNames)}]",
                    ct: ct).ConfigureAwait(false);
                db.DeploymentLogEntries.Add(new DeploymentLogEntry
                {
                    SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
                    Sequence     = logSeq.Next(),
                    Timestamp    = DateTimeOffset.UtcNow,
                    Level        = "error",
                    Message      = ex.Message,
                });
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
                await FailAsync(db, deployment, ex.Message, ct).ConfigureAwait(false);
                return;
            }

            // Transition to Running before doing any work so the UI updates immediately.
            deployment.Status     = DeploymentStatus.Running;
            deployment.StartedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

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
            var hasFailed = false;
            var aliveTargets = new List<DeploymentTarget>(targets);
            var droppedTargets = new List<DroppedTargetInfo>();

            foreach (var wave in waves)
            {
                if (wave.Kind == WavePartitioner.WaveKind.Server)
                {
                    // ── Server wave ─────────────────────────────────────
                    // M-RollingDeployments Phase 1b: server waves run ONCE,
                    // using the canonical (== first) target's variable bag
                    // for system + machine vars and the legacy
                    // deployment.Target for the role filter
                    // (StepAppliesToTarget). Server steps are deployment-
                    // scoped — DeployRelease cascade, manual interventions,
                    // … — so we deliberately preserve the single-execution
                    // semantic. Operators authoring server steps in a
                    // multi-target deployment see the canonical target's
                    // machine context (same as today's single-target).
                    var serverOutcomes = await RunServerWaveAsync(
                        wave, canonicalCtx.SnapshotByPlanIndex, hasFailed,
                        canonicalCtx.VarDict, deployment, db, auditLog, logSeq,
                        canonicalCtx.FlatVars, ct).ConfigureAwait(false);

                    var firstRequiredFailure = serverOutcomes.FirstOrDefault(o =>
                        !o.Skipped && !o.Ok && canonicalCtx.SnapshotByPlanIndex[o.Step.Index].Required);
                    if (firstRequiredFailure is not null)
                    {
                        await auditLog.RecordAsync(
                            AuditEventType.DeploymentRequiredStepFailed,
                            subjectType: "Deployment",
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
                            db, auditLog, logSeq, deployment,
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
                    var targetWaveResult = await DispatchTargetWaveAcrossTargetsAsync(
                        wave, aliveTargets, contexts, canonicalCtx.SnapshotByPlanIndex,
                        snapshotById, hasFailed, deployment,
                        db, auditLog, logSeq, ct).ConfigureAwait(false);

                    foreach (var dropped in targetWaveResult.DroppedTargets)
                    {
                        await EmitTargetDroppedAsync(
                            db, auditLog, logSeq, deployment, dropped,
                            wave, ct).ConfigureAwait(false);
                        aliveTargets.Remove(dropped.Target);
                        droppedTargets.Add(dropped);
                    }

                    if (targetWaveResult.HasFailedNonRequired)
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
                            AuditEventType.DeploymentRequiredStepFailed,
                            subjectType: "Deployment",
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
            // hasFailed = true means at least one non-required step failed
            // along the way; the deployment terminates as
            // SucceededWithWarnings (Octopus's yellow-badge state) rather
            // than the pristine Succeeded.
            // Phase 3 — droppedTargets non-empty ALSO yields
            // SucceededWithWarnings, even if every surviving target's
            // remaining steps all succeeded cleanly. Partial success is
            // visible in the terminal status without needing to scrape
            // audit rows.
            var terminalStatus = (hasFailed || droppedTargets.Count > 0)
                ? DeploymentStatus.SucceededWithWarnings
                : DeploymentStatus.Succeeded;
            DateTimeOffset finalCompletedUtc;
            await using (var finalDb = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
                .CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var d = await finalDb.Deployments.FindAsync([deployment.Id], ct).ConfigureAwait(false);
                finalCompletedUtc = DateTimeOffset.UtcNow;
                if (d is not null)
                {
                    d.Status       = terminalStatus;
                    d.CompletedUtc = finalCompletedUtc;
                    await finalDb.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }

            // ── Slow-deployment audit (M13.F.3) ──────────────────────────
            // Emit a Deployment.Slow audit event when the run exceeded
            // the configured threshold so M13.B.2/3 subscribers can route
            // a notification (webhook / email / runbook / AI inspection).
            // Threshold = 0 disables.
            await EmitSlowDeploymentAuditIfNeededAsync(
                scope.ServiceProvider, deployment, finalCompletedUtc, ct).ConfigureAwait(false);

            // ── Phase 3 — per-target slow audit ──────────────────────────
            // Each target's effective duration (max CompletedUtc − min
            // StartedUtc across its DeploymentStepOutcome rows) is
            // compared against the same threshold; one
            // Deployment.TargetSlow audit per slow target. Operators can
            // pinpoint which specific machine slowed a multi-target run,
            // even when the deployment as a whole stayed under threshold.
            await EmitTargetSlowAuditsIfNeededAsync(
                scope.ServiceProvider, deployment, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Deployment {Id} completed ({ServerSteps} server step(s), {TargetSteps} target step(s)).",
                deployment.Id, serverStepCount, targetStepCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Unhandled error dispatching deployment {DeploymentId}.", deploymentId);

            await using var errorScope = scopeFactory.CreateAsyncScope();
            var errorDb = errorScope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            var dep = await errorDb.Deployments.FindAsync([deploymentId], ct).ConfigureAwait(false);
            if (dep is not null)
            {
                await FailAsync(errorDb, dep, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Offline drop ─────────────────────────────────────────────────────

    private async Task DispatchOfflineDropAsync(
        IServiceProvider sp, KrakenDbContext db, Deployment deployment, CancellationToken ct)
    {
        var variableService = sp.GetRequiredService<VariableService>();
        var dropBundleService = sp.GetRequiredService<DropBundleService>();
        var stepPackages = sp.GetRequiredService<StepPackageService>();
        var encryption = sp.GetRequiredService<
            KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService>();
        var config = sp.GetRequiredService<IConfiguration>();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KrakenDbContext>>();
        var dataPath = config["DataPath"] ?? "data";
        var serverBaseUrl = config["Server:BaseUrl"];

        // Offline drops use the frozen release snapshot, exactly like online —
        // refuse to ship a bundle from an un-snapshotted (pre-feature) release.
        if (deployment.Release.VariableSnapshotUpdatedUtc is null)
        {
            await FailAsync(db, deployment,
                $"Release '{deployment.Release.Version}' has no variable snapshot. " +
                "Open the release and click 'Update Variables', then re-deploy.", ct)
                .ConfigureAwait(false);
            return;
        }

        // Per-target bundle encryption key (provisioned when the target was
        // configured as offline-drop). Without it we can't produce plan.enc.
        var bundleKeyEnc = deployment.Target!.OfflineDropConfig?.BundleKeyEncrypted;
        if (string.IsNullOrEmpty(bundleKeyEnc))
        {
            await FailAsync(db, deployment,
                "Offline-drop target has no bundle encryption key. Re-save the " +
                "target's offline-drop settings to provision one (and deliver it to " +
                "the target operator out-of-band), then re-deploy.", ct).ConfigureAwait(false);
            return;
        }
        var bundleKey = Convert.FromBase64String(encryption.Decrypt(bundleKeyEnc));

        // Build the SAME plan the online path dispatches (snapshot-resolved,
        // Octostache-substituted, flattened, per-step deltas) so the offline
        // runner executes it through the identical DeploymentExecutor.
        var snapshotSteps = deployment.Release.ProcessSnapshot
            .OrderBy(s => s.SortOrder)
            .ToArray();
        var ctx = await BuildTargetDispatchContextAsync(
            deployment, deployment.Target, snapshotSteps, variableService,
            serverBaseUrl, dbFactory, ct).ConfigureAwait(false);

        // Required ForEach that couldn't resolve its collection aborts here,
        // mirroring the online gate.
        foreach (var w in ctx.Flatten.Warnings)
        {
            if (w.Kind == DeploymentPlanFlattener.WarningKind.ForEachUnresolved && w.Source.Required)
            {
                await FailAsync(db, deployment,
                    $"Required ForEach step '{w.Source.Name}' could not resolve its " +
                    $"collection: {w.Detail}", ct).ConfigureAwait(false);
                return;
            }
        }

        var plan = ctx.Plan;

        // Server-orchestrated step types can't run on an air-gapped box (no
        // server to drive the cascade / approval). Refuse rather than ship a
        // bundle that fails mid-run.
        var onlineOnly = plan.Steps
            .Where(s => s.StepType is "Octopus.DeployRelease" or "Octopus.Manual")
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (onlineOnly.Count > 0)
        {
            await FailAsync(db, deployment,
                "Offline drop cannot run server-orchestrated steps: " +
                $"{string.Join(", ", onlineOnly)}. Remove them from the process or " +
                "deploy this project to an online target.", ct).ConfigureAwait(false);
            return;
        }

        // Runner embedding (PerformanceSettings.EmbedOfflineRunner, default true,
        // editable on /configuration/performance): embed the self-contained
        // runner published for the target's RID under
        // {dataPath}/offline-runner/{rid}/ so the bundle needs no .NET on the
        // target (~110 MB/bundle). When off, bundles stay small (data only) and
        // the bootstrap falls back to a KrakenDeploy.Agent on PATH. An absent
        // staged runner degrades gracefully regardless.
        var perfSettings = await sp.GetRequiredService<PerformanceSettingsService>()
            .GetAsync(ct).ConfigureAwait(false);
        string? runnerStageDir = null;
        if (perfSettings.EmbedOfflineRunner)
        {
            var rid = (deployment.Target.OperatingSystem ?? "")
                .Contains("windows", StringComparison.OrdinalIgnoreCase)
                    ? "win-x64" : "linux-x64";
            runnerStageDir = Path.Combine(dataPath, "offline-runner", rid);
        }

        var bundlePath = await dropBundleService
            .GenerateAsync(deployment, plan, bundleKey,
                stepPackages.TryGetArchivePath, dataPath, runnerStageDir, ct: ct)
            .ConfigureAwait(false);

        deployment.DropBundlePath = bundlePath;
        deployment.Status = DeploymentStatus.PendingOfflineResult;
        deployment.StartedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Offline drop bundle generated for deployment {DeploymentId}: {Path}.",
            deployment.Id, bundlePath);

        // Attempt delivery if configured (non-Manual).
        await DeliverDropBundleAsync(sp, deployment, dataPath, ct).ConfigureAwait(false);
    }

    private async Task DeliverDropBundleAsync(
        IServiceProvider sp, Deployment deployment, string dataPath, CancellationToken ct)
    {
        var deliveryChannel = deployment.Target?.OfflineDropConfig?.DeliveryChannel
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
                    await DeliverViaWebhookAsync(deployment, dataPath, ct).ConfigureAwait(false);
                    break;
                case OfflineDropDeliveryChannel.FileShareDrop:
                    await DeliverViaFileShareAsync(deployment, dataPath, ct).ConfigureAwait(false);
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
        Deployment deployment, string dataPath, CancellationToken ct)
    {
        var webhookUrl = deployment.Target?.OfflineDropConfig?.WebhookUrl;
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
        Deployment deployment, string dataPath, CancellationToken ct)
    {
        var targetPath = deployment.Target?.OfflineDropConfig?.FileSharePath;
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
    private Task<bool> ExecuteServerStepAsync(
        Guid deploymentId,
        DeploymentStepPlan step,
        IReadOnlyDictionary<string, string> flatVars,
        CancellationToken ct)
    {
        // Overlay this step's per-step variable delta (step/action scope) onto
        // the deployment-wide vars — the server-side counterpart of the agent's
        // ApplyStepVariables. No-op when the step carries no delta.
        var effectiveVars = OverlayStepVariables(flatVars, step);
        if (step.StepType.Equals(DeployReleaseStepRunner.StepType, StringComparison.OrdinalIgnoreCase))
        {
            return deployReleaseRunner.ExecuteAsync(deploymentId, step, effectiveVars, ct);
        }
        return serverRunner.ExecuteAsync(deploymentId, step, effectiveVars, ct);
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
    /// Encapsulates "Run on Server on behalf of each deployment target" role
    /// filtering for our one-target-per-deployment model: when a server step
    /// has <c>TargetRoles</c>, only execute it if the deployment's target has
    /// at least one of those roles. A server step without roles always
    /// applies (it's a pure "Run on Server" step).
    /// </summary>
    private static bool StepAppliesToTarget(Deployment deployment, DeploymentStepPlan step)
    {
        if (step.TargetRoles is null || step.TargetRoles.Count == 0)
        {
            return true;
        }
        var targetRoles = deployment.Target?.Roles ?? [];
        if (targetRoles.Count == 0)
        {
            return false;
        }
        return step.TargetRoles.Any(r =>
            targetRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    // M15.2: SubstituteConfig moved into DeploymentPlanFlattener so it
    // can run per-ForEach-iteration with the right variable bag. The
    // orchestrator no longer pre-substitutes the snapshot's Config.

    private async Task FailAsync(
        KrakenDbContext db, Deployment deployment, string reason, CancellationToken ct)
    {
        deployment.Status = DeploymentStatus.Failed;
        deployment.CompletedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // M11.C — queue an AI diagnosis, but only for deployments that
        // actually started executing. Pre-flight refusals (no target, no
        // variable snapshot, blocked by freeze, agent offline at dispatch)
        // set Failed before StartedUtc is stamped; diagnosing "it never ran"
        // wastes AI budget + produces no useful analysis. Best-effort
        // TryWrite on an unbounded channel — never blocks finalisation.
        if (deployment.StartedUtc is not null)
        {
            diagnosisChannel.Writer.TryWrite(deployment.Id);
        }
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
    /// <strong>Output variables on retry:</strong> server-side script
    /// steps don't currently capture <c>Set-OctopusVariable</c> output
    /// into <c>deployment_output_variables</c> (the <c>##octopus[setVariable]</c>
    /// lines pass through as log entries verbatim), so there's nothing
    /// to discard between retry attempts. When server-side output capture
    /// lands, this is the place to clear the partial output bucket between
    /// failed attempts.
    /// </para>
    /// </summary>
    private async Task<(bool Ok, bool TimedOut, int AttemptCount, DateTimeOffset StartedUtc)>
        RunServerStepWithRetriesAsync(
            Guid deploymentId,
            DeploymentStepPlan step,
            StepSnapshot snapshot,
            Deployment deployment,
            IAuditLog audit,
            LogSequencer logSeq,
            IReadOnlyDictionary<string, string> flatVars,
            CancellationToken ct)
    {
        // M14.5 — capture start time at first attempt so the outcome row
        // carries an accurate StartedUtc the Steps tab can show duration from.
        var startedUtc = DateTimeOffset.UtcNow;

        var outcome = await StepRetryRunner.RunAsync(
            snapshot.Name,
            snapshot.MaxRetries,
            snapshot.RetryDelaySeconds,
            snapshot.TimeoutSeconds,
            runAttempt: (CancellationToken attemptCt) =>
                ExecuteServerStepAsync(deploymentId, step, flatVars, attemptCt),
            isSuccess: ok => ok,
            onTimeoutResult: () => false,
            // Server surfaces the per-step timeout ONCE via the final TimedOut
            // (RunServerWaveAsync logs + audits it), not per timed-out attempt.
            onAttemptTimedOutAsync: null,
            // Wave steps run in parallel; each writes its log line through its
            // own short-lived context (AppendConcurrentLogAsync) so they never
            // contend on the shared per-dispatch db. Audit already uses its own
            // per-call context (AuditLogService).
            onRetryAsync: async info =>
            {
                await AppendConcurrentLogAsync(
                    deployment.Id, logSeq, "warning", info.Marker, ct).ConfigureAwait(false);
                await audit.RecordAsync(
                    AuditEventType.DeploymentStepRetried,
                    subjectType: "Deployment",
                    subjectId:   deployment.Id.ToString(),
                    details:     $"Step={snapshot.Name}, " +
                                 $"Attempt={info.Attempt.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"MaxRetries={info.MaxAttempts.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"RetryDelaySeconds={info.DelaySeconds.ToString(CultureInfo.InvariantCulture)}",
                    ct: ct).ConfigureAwait(false);
            },
            onLateSuccessAsync: attemptCount => AppendConcurrentLogAsync(
                deployment.Id, logSeq, "info",
                $"--- Step '{snapshot.Name}' succeeded on attempt " +
                $"{attemptCount.ToString(CultureInfo.InvariantCulture)} ---", ct),
            ct).ConfigureAwait(false);

        return (Ok: outcome.Result, TimedOut: outcome.TimedOut,
                AttemptCount: outcome.AttemptCount, StartedUtc: startedUtc);
    }

    /// <summary>
    /// Appends one deployment-log entry through a SHORT-LIVED DI scope (its own
    /// <see cref="KrakenDbContext"/>) instead of the shared per-dispatch
    /// <c>db</c>. Wave steps and rolling-deployment targets run in parallel and
    /// would otherwise contend on the single (non-thread-safe) DbContext; giving
    /// each concurrent log write its own scoped context removes that contention
    /// entirely — no global lock — so the fan-out scales (DB concurrency is then
    /// bounded by the connection pool, not serialised). <see cref="LogSequencer"/>
    /// is independently locked, so sequence numbers stay monotonic across
    /// branches, and <c>IAuditLog</c> already uses its own per-call context
    /// (<c>AuditLogService</c>), so audit writes need no special handling.
    /// <para>
    /// Resolved via <see cref="IServiceScopeFactory"/> rather than injecting
    /// <c>IDbContextFactory</c>: the factory is registered SCOPED in this app, so
    /// injecting it into this singleton hosted service is a captive dependency
    /// that fails the host's ValidateOnBuild. Mirrors <c>DispatchAsync</c>.
    /// </para>
    /// Used only on the CONCURRENT paths; the sequential post-wave writes keep
    /// using the shared <c>db</c>. Internal (not private) so a focused test can
    /// drive it from genuinely-parallel tasks (the orchestrator's fake-agent
    /// harness resolves dispatches synchronously and can't race it otherwise).
    /// </summary>
    internal async Task AppendConcurrentLogAsync(
        Guid deploymentId, LogSequencer logSeq, string level, string message, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var logDb = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        // This short-lived scope has no real Space context (DefaultSpaceId), so
        // resolve the deployment's Space directly (IgnoreQueryFilters) and stamp
        // it explicitly — the interceptor would otherwise mis-stamp DefaultSpaceId.
        var spaceId = await logDb.Deployments.IgnoreQueryFilters()
            .Where(d => d.Id == deploymentId)
            .Select(d => d.SpaceId)
            .FirstAsync(ct)
            .ConfigureAwait(false);
        logDb.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            SpaceId      = spaceId,
            DeploymentId = deploymentId,
            Sequence     = logSeq.Next(),
            Timestamp    = DateTimeOffset.UtcNow,
            Level        = level,
            Message      = message,
        });
        await logDb.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task LogAndAuditStepSkippedAsync(
        KrakenDbContext db, IAuditLog audit, LogSequencer logSeq,
        Deployment deployment, StepSnapshot snapshot,
        StepConditionEvaluator.Decision decision,
        CancellationToken ct)
    {
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
            Sequence     = logSeq.Next(),
            Timestamp    = DateTimeOffset.UtcNow,
            Level        = "info",
            Message      = $"--- Step '{snapshot.Name}' skipped: {decision.Reason} ---",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // M14.3.1 — typed Decision.Kind drives the audit event type
        // (replaced the pre-M14.3.1 substring-on-Reason heuristic which
        // would silently change behaviour when the reason wording changed).
        var eventType = decision.Kind == StepConditionEvaluator.Kind.Unresolved
            ? AuditEventType.DeploymentVariableConditionUnresolved
            : AuditEventType.DeploymentStepSkipped;
        await audit.RecordAsync(
            eventType,
            subjectType: "Deployment",
            subjectId:   deployment.Id.ToString(),
            details:     $"Step={snapshot.Name}, Reason={decision.Reason}",
            ct: ct).ConfigureAwait(false);
    }

    // Instance (not static) + fresh-context log write: this is the one
    // log/audit helper with a CONCURRENT caller (RunServerWaveAsync's parallel
    // step tasks), so its log line goes through AppendConcurrentLogAsync rather
    // than the shared db. The sequential target-wave caller is unaffected.
    private async Task LogAndAuditStepTimedOutAsync(
        IAuditLog audit, LogSequencer logSeq,
        Deployment deployment, StepSnapshot snapshot,
        CancellationToken ct)
    {
        await AppendConcurrentLogAsync(
            deployment.Id, logSeq, "error",
            $"--- Step '{snapshot.Name}' timed out after " +
            $"{snapshot.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}s ---",
            ct).ConfigureAwait(false);
        await audit.RecordAsync(
            AuditEventType.DeploymentStepTimedOut,
            subjectType: "Deployment",
            subjectId:   deployment.Id.ToString(),
            details:     $"Step={snapshot.Name}, " +
                         $"TimeoutSeconds={snapshot.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}",
            ct: ct).ConfigureAwait(false);
    }

    private static async Task LogAndAuditStepFailedNonRequiredAsync(
        KrakenDbContext db, IAuditLog audit, LogSequencer logSeq,
        Deployment deployment, StepSnapshot snapshot,
        CancellationToken ct)
    {
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
            Sequence     = logSeq.Next(),
            Timestamp    = DateTimeOffset.UtcNow,
            Level        = "warning",
            Message      = $"--- Step '{snapshot.Name}' failed (not required) — " +
                           "deployment continues ---",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.RecordAsync(
            AuditEventType.DeploymentStepFailedNonRequired,
            subjectType: "Deployment",
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
        Deployment deployment,
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

            var elapsed = completedUtc - deployment.StartedUtc.Value;
            if (elapsed.TotalMinutes < threshold)
            {
                return;
            }

            var audit = sp.GetRequiredService<IAuditLog>();
            await audit.RecordAsync(
                AuditEventType.DeploymentSlow,
                subjectType: "Deployment",
                subjectId:   deployment.Id.ToString(),
                details:     string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "DurationMinutes={0:F1}, ThresholdMinutes={1}, ReleaseId={2}",
                    elapsed.TotalMinutes, threshold, deployment.ReleaseId),
                ct: ct).ConfigureAwait(false);
        }
        catch
        {
            // Audit emission is best-effort — never bubble the failure
            // up into deployment finalisation.
        }
    }

    /// <summary>
    /// M-RollingDeployments Phase 3 — emits one
    /// <see cref="AuditEventType.DeploymentTargetSlow"/> per target whose
    /// effective duration (max <c>CompletedUtc</c> − min <c>StartedUtc</c>
    /// across its <see cref="DeploymentStepOutcome"/> rows) exceeded
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
        Deployment deployment,
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

            var rows = await db.DeploymentStepOutcomes
                .Where(o => o.DeploymentId == deployment.Id
                            && o.TargetId != null
                            && o.StartedUtc != null)
                .Select(o => new { o.TargetId, o.StartedUtc, o.CompletedUtc })
                .ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return;
            }

            var perTarget = rows
                .GroupBy(r => r.TargetId!.Value)
                .Select(g => new
                {
                    TargetId = g.Key,
                    Start    = g.Min(r => r.StartedUtc!.Value),
                    End      = g.Max(r => r.CompletedUtc),
                })
                .Where(t => (t.End - t.Start).TotalMinutes >= threshold)
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
                var duration = (t.End - t.Start).TotalMinutes;
                var name = nameById.GetValueOrDefault(t.TargetId, t.TargetId.ToString());
                await audit.RecordAsync(
                    AuditEventType.DeploymentTargetSlow,
                    subjectType: "Deployment",
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
        Deployment deployment,
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

        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
            Sequence     = logSeq.Next(),
            Timestamp    = DateTimeOffset.UtcNow,
            Level        = "warning",
            Message      = $"--- Target '{dropped.Target.Name}' dropped out: " +
                           $"{reasonText}{(dropped.Error is null ? "" : $" — {dropped.Error}")} ---",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await auditLog.RecordAsync(
            AuditEventType.DeploymentTargetDropped,
            subjectType: "Deployment",
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
    /// orchestrator can populate <see cref="DeploymentStepOutcome"/>
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
        DateTimeOffset? StartedUtc);

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
        Deployment deployment,
        KrakenDbContext db,
        IAuditLog auditLog,
        LogSequencer logSeq,
        IReadOnlyDictionary<string, string> flatVars,
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
                    db, auditLog, logSeq, deployment, snapshot, decision, ct)
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
        // go through short-lived per-write contexts (AppendConcurrentLogAsync),
        // NOT the shared per-dispatch db, so the steps run fully in parallel with
        // no DbContext contention. Audit uses its own per-call context
        // (AuditLogService). LogSequencer is independently locked, so sequence
        // numbers stay monotonic. The post-wave outcome upserts below run after
        // Task.WhenAll (sequentially) on the shared db.
        var stepTasks = toRun.Select(async s =>
        {
            var snap = snapshotSteps[s.Index];
            var (ok, timedOut, attemptCount, startedUtc) =
                await RunServerStepWithRetriesAsync(
                    deployment.Id, s, snap, deployment, auditLog,
                    logSeq, flatVars, ct).ConfigureAwait(false);
            if (timedOut)
            {
                await LogAndAuditStepTimedOutAsync(
                    auditLog, logSeq, deployment, snap, ct).ConfigureAwait(false);
            }
            return new ServerStepOutcome(
                Step:         s,
                Skipped:      false,
                Ok:           ok,
                TimedOut:     timedOut,
                AttemptCount: attemptCount,
                StartedUtc:   startedUtc);
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
                                  ? $"Step exceeded TimeoutSeconds={snap.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}."
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
            Deployment deployment,
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
        while (true)
        {
            var tcs = new TaskCompletionSource<SubPlanResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            subPlans.Register(deployment.Id, targetId, tcs);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (waveTimeoutSeconds > 0)
            {
                linkedCts.CancelAfter(TimeSpan.FromSeconds(waveTimeoutSeconds));
            }
            var thisAttemptTimedOut = false;

            try
            {
                await agentHub.Clients.Client(connectionId)
                    .RunDeploymentAsync(subPlan).ConfigureAwait(false);

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
                            ErrorMessage:
                                $"Target step wave timed out after {waveTimeoutSeconds}s.");
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            finally
            {
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
                    await AppendConcurrentLogAsync(
                        deployment.Id, logSeq, "info",
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
            await AppendConcurrentLogAsync(
                deployment.Id, logSeq, "warning",
                $"--- Target wave [{waveNamesForAudit}] attempt " +
                $"{attempt.ToString(CultureInfo.InvariantCulture)} failed; retrying " +
                $"(attempt {(attempt + 1).ToString(CultureInfo.InvariantCulture)} of " +
                $"{(waveMaxRetries + 1).ToString(CultureInfo.InvariantCulture)})" +
                (waveRetryDelaySeconds > 0
                    ? $" in {waveRetryDelaySeconds.ToString(CultureInfo.InvariantCulture)}s ---"
                    : " ---"),
                ct).ConfigureAwait(false);
            await auditLog.RecordAsync(
                AuditEventType.DeploymentStepRetried,
                subjectType: "Deployment",
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
        Deployment deployment,
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

            db.DeploymentLogEntries.Add(new DeploymentLogEntry
            {
                SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
                Sequence     = logSeq.Next(),
                Timestamp    = DateTimeOffset.UtcNow,
                Level        = "warning",
                Message      = $"Output variable '{c.VariableName}' was set by " +
                               $"parallel siblings [{writersDesc}]; last-writer-wins " +
                               $"in SortOrder → {c.Winner.StepName}={Elide(c.Winner.Value)}.",
            });
            await auditLog.RecordAsync(
                AuditEventType.DeploymentParallelOutputCollision,
                subjectType: "Deployment",
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
    /// M14.5 — upsert a <see cref="DeploymentStepOutcome"/> row keyed by
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
    private static async Task UpsertStepOutcomeAsync(
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
        var existing = await db.DeploymentStepOutcomes
            .FirstOrDefaultAsync(o =>
                o.DeploymentId == deploymentId
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
        var spaceId = await db.Deployments.IgnoreQueryFilters()
            .Where(d => d.Id == deploymentId)
            .Select(d => d.SpaceId)
            .FirstAsync(ct)
            .ConfigureAwait(false);
        db.DeploymentStepOutcomes.Add(new DeploymentStepOutcome
        {
            SpaceId      = spaceId,
            DeploymentId = deploymentId,
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
    private sealed record TargetDispatchContext(
        DeploymentTarget Target,
        VariableDictionary VarDict,
        IReadOnlyDictionary<string, string> FlatVars,
        IReadOnlyDictionary<string, string[]> ArrayVars,
        DeploymentPlan Plan,
        DeploymentStepPlan[] Steps,
        StepSnapshot[] SnapshotByPlanIndex,
        DeploymentPlanFlattener.FlattenResult Flatten);

    /// <summary>
    /// Resolves project variables for a single target, builds the Octostache
    /// dictionary + system variables, runs the M15.2 flattener with the
    /// per-target variable bag (so any per-target Octostache substitutions
    /// inside step Configs resolve to that target's value), then overlays
    /// referenced-package resolution. The structural waves layout is
    /// snapshot-driven and therefore identical across targets — only the
    /// substituted Configs differ.
    /// </summary>
    private async Task<TargetDispatchContext> BuildTargetDispatchContextAsync(
        Deployment deployment,
        DeploymentTarget target,
        IReadOnlyList<StepSnapshot> snapshotSteps,
        VariableService variableService,
        string? serverBaseUrl,
        IDbContextFactory<KrakenDbContext> dbFactory,
        CancellationToken ct)
    {
        // Resolve deployment-wide variables + per-step deltas in one pass over
        // the frozen snapshot. The per-step phase is skipped internally when no
        // variable is step-scoped (the common case).
        var stepIdsAndNames = snapshotSteps
            .Select(s => (s.Id, s.Name))
            .ToList();
        var stepResolution = await variableService.ResolveFromSnapshotWithStepsAsync(
            deployment.Release.VariableSnapshot,
            deployment.EnvironmentId,
            target.Id,
            target.Roles,
            deployment.TenantId,
            deployment.Release.ChannelId,
            stepIdsAndNames,
            ct).ConfigureAwait(false);
        var rawVars = stepResolution.DeploymentWide;

        var varDict = new VariableDictionary();

        var systemVars = OctopusSystemVariablesBuilder.BuildForDeployment(
            deployment,
            deployment.Release,
            deployment.Release.Project,
            deployment.Environment,
            target,
            deployment.Tenant,
            deployment.Release.ProcessSnapshot,
            serverBaseUrl);

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
            ArrayVariables:  arrayVars);

        return new TargetDispatchContext(
            Target:              target,
            VarDict:             varDict,
            FlatVars:            flatVars,
            ArrayVars:           arrayVars,
            Plan:                plan,
            Steps:               steps,
            SnapshotByPlanIndex: snapshotByPlanIndex,
            Flatten:             flatten);
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
        bool HasFailedNonRequired);

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
        IReadOnlyDictionary<Guid, TargetDispatchContext> contexts,
        StepSnapshot[] canonicalSnapshotByPlanIndex,
        IReadOnlyDictionary<Guid, StepSnapshot> snapshotById,
        bool hasFailedAtWaveStart,
        Deployment deployment,
        KrakenDbContext db,
        IAuditLog auditLog,
        LogSequencer logSeq,
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
            var stepsToRun = new List<DeploymentStepPlan>(wave.Steps.Count);
            foreach (var s in wave.Steps)
            {
                var snapshot = ctx.SnapshotByPlanIndex[s.Index];
                var decision = StepConditionEvaluator.Evaluate(
                    snapshot.Condition,
                    snapshot.ConditionVariableExpression,
                    hasFailedAtWaveStart,
                    ctx.VarDict);
                if (decision.Action == StepConditionEvaluator.Action.Skip)
                {
                    await LogAndAuditStepSkippedAsync(
                        db, auditLog, logSeq, deployment, snapshot, decision, ct)
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
            if (connectionId is null)
            {
                // Phase 3 — record an offline drop-out (instead of aborting).
                // Steps tab gets one Failed outcome per remaining step so
                // operators can see "this target dropped at wave X".
                var offlineAt = DateTimeOffset.UtcNow;
                foreach (var p in stepsToRun)
                {
                    var snap = ctx.SnapshotByPlanIndex[p.Index];
                    await UpsertStepOutcomeAsync(
                        db, deployment.Id, p.Index, snap.Name,
                        StepOutcomeKind.Failed, attemptCount: 0,
                        errorMessage: "Target agent offline at dispatch time.",
                        startedUtc:   null,
                        completedUtc: offlineAt,
                        isServerSide: false,
                        required:     snap.Required, ct,
                        targetId:     target.Id).ConfigureAwait(false);
                }
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                droppedTargets.Add(new DroppedTargetInfo(
                    Target:   target,
                    Reason:   DropReason.AgentOffline,
                    StepName: null,
                    Error:    "Target agent offline at dispatch time."));
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
                DroppedTargets:       droppedTargets,
                HasFailedNonRequired: false);
        }

        // ── M-RollingDeployments Phase 2 — resolve effective MaxParallelism ──
        // The cap comes from a Kraken.StepGroup ancestor's
        // `Octopus.Action.MaxParallelism`. When every step in the wave shares
        // a single rolling ancestor with a parseable positive cap, the
        // resolver returns it; otherwise null (no batching). See
        // RollingWindowResolver for the precise semantic + edge cases.
        var maxParallelism = RollingWindowResolver.ResolveWaveMaxParallelism(
            wave.Steps, canonicalSnapshotByPlanIndex, snapshotById);
        var rollingGroupName = maxParallelism is null ? null
            : RollingWindowResolver.ResolveWaveRollingGroupName(
                wave.Steps, canonicalSnapshotByPlanIndex, snapshotById);

        var batches = maxParallelism is null
            ? [dispatchPlan]
            : RollingWindowResolver.Chunk(dispatchPlan, maxParallelism.Value);

        // Batching is only operator-visible when we ACTUALLY split — a cap
        // that meets or exceeds the dispatch count silently degrades to a
        // single batch + no audit (operators don't get noise for a cap that
        // didn't fire).
        var batchingActive = batches.Count > 1 && rollingGroupName is not null;

        var hasFailedNonRequired = false;

        for (var batchIdx = 0; batchIdx < batches.Count; batchIdx++)
        {
            var batch = batches[batchIdx];

            if (batchingActive)
            {
                var batchTargets = string.Join(", ", batch.Select(t => t.Ctx.Target.Name));
                var waveNames = string.Join(", ", wave.Steps.Select(s => s.Name));
                await auditLog.RecordAsync(
                    AuditEventType.DeploymentRollingBatchStarted,
                    subjectType: "Deployment",
                    subjectId:   deployment.Id.ToString(),
                    details:     $"RollingGroup={rollingGroupName}, " +
                                 $"Batch={(batchIdx + 1).ToString(CultureInfo.InvariantCulture)}/" +
                                 $"{batches.Count.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"BatchSize={batch.Count.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"MaxParallelism={maxParallelism!.Value.ToString(CultureInfo.InvariantCulture)}, " +
                                 $"Targets=[{batchTargets}], " +
                                 $"Wave=[{waveNames}]",
                    ct: ct).ConfigureAwait(false);
                db.DeploymentLogEntries.Add(new DeploymentLogEntry
                {
                    SpaceId      = deployment.SpaceId,
            DeploymentId = deployment.Id,
                    Sequence     = logSeq.Next(),
                    Timestamp    = DateTimeOffset.UtcNow,
                    Level        = "info",
                    Message      = $"--- Rolling batch " +
                                   $"{(batchIdx + 1).ToString(CultureInfo.InvariantCulture)} of " +
                                   $"{batches.Count.ToString(CultureInfo.InvariantCulture)} for " +
                                   $"'{rollingGroupName}' (window={maxParallelism!.Value}): " +
                                   $"[{batchTargets}] ---",
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var batchOutcome = await DispatchOneBatchAsync(
                batch, deployment, db, auditLog, logSeq, ct).ConfigureAwait(false);

            // Phase 3 — accumulate drop-outs from this batch into the
            // wave's aggregate; subsequent batches still run (a failed
            // target in batch K is local to that target, not a halt
            // signal for batches K+1..end). Operators who want canary
            // semantics across batches can rely on per-target drop-out
            // emptying aliveTargets if every batch's targets fail.
            droppedTargets.AddRange(batchOutcome.DroppedTargets);
            if (batchOutcome.HasFailedNonRequired)
            {
                hasFailedNonRequired = true;
            }

            if (batchingActive)
            {
                var failedTargetNames = string.Join(", ", batchOutcome.FailedTargets);
                await auditLog.RecordAsync(
                    AuditEventType.DeploymentRollingBatchCompleted,
                    subjectType: "Deployment",
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
            DroppedTargets:       droppedTargets,
            HasFailedNonRequired: hasFailedNonRequired);
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
        bool HasFailedNonRequired,
        IReadOnlyList<string> FailedTargets);

    private async Task<BatchOutcome> DispatchOneBatchAsync(
        IReadOnlyList<(TargetDispatchContext Ctx, List<DeploymentStepPlan> Steps)> batch,
        Deployment deployment,
        KrakenDbContext db,
        IAuditLog auditLog,
        LogSequencer logSeq,
        CancellationToken ct)
    {
        var waveStartedUtc = DateTimeOffset.UtcNow;
        var dispatchTasks = batch.Select(async tuple =>
        {
            var (ctx, stepsToRun) = tuple;
            var connectionId = registry.GetConnectionId(ctx.Target.Id)!;
            var (waveResult, waveTimedOut, perStepResults) = await DispatchTargetWaveAsync(
                ctx.Plan, stepsToRun, ctx.SnapshotByPlanIndex, deployment,
                ctx.Target.Id, connectionId, auditLog, logSeq, ct)
                .ConfigureAwait(false);
            return (ctx, stepsToRun, waveResult, waveTimedOut, perStepResults);
        }).ToArray();

        var perTargetOutcomes = await Task.WhenAll(dispatchTasks).ConfigureAwait(false);
        var waveCompletedUtc = DateTimeOffset.UtcNow;

        var hasFailedNonRequired = false;
        var droppedTargets = new List<DroppedTargetInfo>();
        var failedTargets = new List<string>();

        foreach (var (ctx, stepsToRun, waveResult, waveTimedOut, perStepResults) in perTargetOutcomes)
        {
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

            await EmitWaveCollisionsAsync(
                perStepResults, stepsToRun, deployment, db, auditLog, logSeq, ct)
                .ConfigureAwait(false);

            if (waveTimedOut)
            {
                var timeoutStep = stepsToRun
                    .Select(p => ctx.SnapshotByPlanIndex[p.Index])
                    .FirstOrDefault(snap => snap.TimeoutSeconds > 0);
                if (timeoutStep is not null)
                {
                    await LogAndAuditStepTimedOutAsync(
                        auditLog, logSeq, deployment, timeoutStep, ct).ConfigureAwait(false);
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
                            db, auditLog, logSeq, deployment,
                            ctx.SnapshotByPlanIndex[failed.StepIndex], ct)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    foreach (var p in stepsToRun)
                    {
                        await LogAndAuditStepFailedNonRequiredAsync(
                            db, auditLog, logSeq, deployment,
                            ctx.SnapshotByPlanIndex[p.Index], ct).ConfigureAwait(false);
                    }
                }
                hasFailedNonRequired = true;
            }
        }

        return new BatchOutcome(
            DroppedTargets:       droppedTargets,
            HasFailedNonRequired: hasFailedNonRequired,
            FailedTargets:        failedTargets);
    }
}
