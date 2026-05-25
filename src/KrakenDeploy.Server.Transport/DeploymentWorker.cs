using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using KrakenDeploy.Contracts;
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
                .Include(d => d.Tenant)
                .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
                .ConfigureAwait(false);

            if (deployment is null)
            {
                logger.LogWarning("DeploymentWorker: deployment {Id} not found.", deploymentId);
                return;
            }

            // M14.3.1 — serialise log-sequence allocation. Single-threaded
            // today; M14.4's wave-parallel execution shares this carrier
            // across concurrent step paths within the same deployment.
            var logSeq = new LogSequencer(deployment);

            if (deployment.TargetId is null)
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
            if (deployment.Target?.TransportMode == TransportMode.OfflineDrop)
            {
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
                    DeploymentId = deployment.Id,
                    Sequence     = logSeq.Next(),
                    Timestamp    = DateTimeOffset.UtcNow,
                    Level        = "error",
                    Message      = msg,
                });
                await FailAsync(db, deployment, msg, ct).ConfigureAwait(false);
                return;
            }

            var targetRoles = deployment.Target?.Roles ?? [];
            var rawVars = await variableService.ResolveFromSnapshotAsync(
                deployment.Release.VariableSnapshot,
                deployment.EnvironmentId,
                deployment.TargetId,
                targetRoles,
                deployment.TenantId,
                ct).ConfigureAwait(false);

            // ── 2. Build Octostache dictionary ───────────────────────────────
            // Scalar (String + Sensitive) variables go straight into Octostache.
            // StringArray variables are expanded as both VarName (comma-joined)
            // and VarName[0], VarName[1], … for indexed / #{each} access.
            var varDict = new VariableDictionary();

            // Octopus-compatible system variables (Octopus.Project.Name, Octopus.Release.Number, …).
            var systemVars = OctopusSystemVariablesBuilder.BuildForDeployment(
                deployment,
                deployment.Release,
                deployment.Release.Project,
                deployment.Environment,
                deployment.Target,
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

                        // Comma-joined for $OctopusParameters back-compat and Octostache #{VarName}.
                        var joined = string.Join(", ", items);
                        flatVars[name] = joined;
                        varDict[name] = joined;

                        // Indexed access for #{VarName[0]}, #{each x in VarName}.
                        for (var i = 0; i < items.Length; i++)
                        {
                            varDict[$"{name}[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]"] = items[i];
                        }

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

            // ── 3. Build steps with Octostache substitution applied + resolve
            //       referenced packages (Octopus.Action.Package.PackageReferences).
            var dbFactory = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>();

            var snapshotSteps = deployment.Release.ProcessSnapshot
                .OrderBy(s => s.SortOrder)
                .ToArray();

            var steps = new DeploymentStepPlan[snapshotSteps.Length];
            for (var i = 0; i < snapshotSteps.Length; i++)
            {
                var s = snapshotSteps[i];
                var substitutedConfig = SubstituteConfig(s.Config, varDict);
                var referenced = await PackageReferenceResolver
                    .ResolveAsync(substitutedConfig, dbFactory, logger, ct)
                    .ConfigureAwait(false);
                steps[i] = new DeploymentStepPlan(
                    Index: i,
                    Name: s.Name,
                    StepType: s.StepType,
                    PackageId: s.PackageId,
                    PackageVersion: s.PackageVersion,
                    Config: substitutedConfig,
                    TargetRoles: s.TargetRoles,
                    ReferencedPackages: referenced.Count > 0 ? referenced : null,
                    StepPackageName: s.StepPackageName,
                    StepPackageVersion: s.StepPackageVersion);
            }

            var plan = new DeploymentPlan(
                DeploymentId: deployment.Id,
                EnvironmentName: deployment.Environment.Name,
                Steps: steps,
                Variables: flatVars,
                ArrayVariables: arrayVars);

            // ── 4. Partition steps into waves (M14.4) ───────────────────────
            // A wave = first step + all subsequent StartWithPrevious steps,
            // until the next StartAfterPrevious opens wave N+1. Each wave is
            // either entirely server-side or entirely target-side; mixed
            // waves throw at partition time and we fail the deployment with
            // a clear MixedWaveRefused audit. Within a wave, steps run
            // concurrently.
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            List<WavePartitioner.Wave> waves;
            try
            {
                waves = WavePartitioner.Partition(
                    steps,
                    triggerByIndex: idx => snapshotSteps[idx].StartTrigger);
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
                    DeploymentId = deployment.Id,
                    Sequence     = logSeq.Next(),
                    Timestamp    = DateTimeOffset.UtcNow,
                    Level        = "error",
                    Message      = ex.Message,
                });
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
                "Deployment {DeploymentId}: {Waves} wave(s), {ServerSteps} server step(s), " +
                "{TargetSteps} target step(s), {VarCount} variables.",
                deploymentId, waves.Count, serverStepCount, targetStepCount, flatVars.Count);

            // M14.2 — orchestrator tracks `hasFailed` instead of returning on
            // first failure. Required steps still short-circuit; non-required
            // failures flip the flag and the loop continues so Failure / Always-
            // conditioned cleanup + finalisation steps still run. The
            // deployment's terminal status reflects the final state:
            // hasFailed → SucceededWithWarnings.
            var hasFailed = false;

            foreach (var wave in waves)
            {
                if (wave.Kind == WavePartitioner.WaveKind.Server)
                {
                    // ── Server wave: parallel per-step (each step keeps its
                    //    own Condition + Required + Retries + Timeout via the
                    //    existing M14.2/3 helpers). Task.WhenAll waits for all
                    //    siblings to complete; Required-failure short-circuit
                    //    is applied after the wave settles.
                    var serverOutcomes = await RunServerWaveAsync(
                        wave, snapshotSteps, hasFailed, varDict, deployment,
                        db, auditLog, logSeq, flatVars, ct).ConfigureAwait(false);

                    var firstRequiredFailure = serverOutcomes.FirstOrDefault(o =>
                        !o.Skipped && !o.Ok && snapshotSteps[o.Step.Index].Required);
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
                        !o.Skipped && !o.Ok && !snapshotSteps[o.Step.Index].Required))
                    {
                        await LogAndAuditStepFailedNonRequiredAsync(
                            db, auditLog, logSeq, deployment,
                            snapshotSteps[nonReq.Step.Index], ct).ConfigureAwait(false);
                        hasFailed = true;
                    }
                }
                else
                {
                    // ── Target wave: filter by Condition, then dispatch as a
                    //    single sub-plan; the agent runs the wave's steps in
                    //    parallel. Per-step boundary reports drain from the
                    //    registry after the wave's CompleteDeploymentAsync.
                    var stepsToRun = new List<DeploymentStepPlan>(wave.Steps.Count);
                    foreach (var s in wave.Steps)
                    {
                        var snapshot = snapshotSteps[s.Index];
                        var decision = StepConditionEvaluator.Evaluate(
                            snapshot.Condition,
                            snapshot.ConditionVariableExpression,
                            hasFailed,
                            varDict);
                        if (decision.Action == StepConditionEvaluator.Action.Skip)
                        {
                            await LogAndAuditStepSkippedAsync(
                                db, auditLog, logSeq, deployment, snapshot, decision, ct)
                                .ConfigureAwait(false);
                            continue;
                        }
                        stepsToRun.Add(s);
                    }

                    if (stepsToRun.Count == 0)
                    {
                        continue; // every step in the wave was skipped by Condition
                    }

                    var connectionId = registry.GetConnectionId(deployment.TargetId.Value);
                    if (connectionId is null)
                    {
                        await FailAsync(db, deployment, "Target is offline.", ct).ConfigureAwait(false);
                        return;
                    }

                    var (waveResult, waveTimedOut, perStepResults) = await DispatchTargetWaveAsync(
                        plan, stepsToRun, snapshotSteps, deployment, connectionId,
                        db, auditLog, logSeq, ct).ConfigureAwait(false);

                    // ── M14.4 Output-variable collision audits ─────────────
                    // Detect Same-name writes across the wave's parallel
                    // siblings and emit one audit per collision so operators
                    // see which step's value "lost" the last-writer-wins
                    // race in SortOrder. Collision storage is unchanged
                    // (per-step rows in DeploymentOutputVariable), this is
                    // purely a forensic signal.
                    await EmitWaveCollisionsAsync(
                        perStepResults, stepsToRun, deployment, db, auditLog, logSeq, ct)
                        .ConfigureAwait(false);

                    if (waveTimedOut)
                    {
                        // Emit a TimedOut audit for the first step with a
                        // non-zero TimeoutSeconds in the wave (the one the
                        // operator most likely configured).
                        var timeoutStep = stepsToRun
                            .Select(p => snapshotSteps[p.Index])
                            .FirstOrDefault(snap => snap.TimeoutSeconds > 0);
                        if (timeoutStep is not null)
                        {
                            await LogAndAuditStepTimedOutAsync(
                                db, auditLog, logSeq, deployment, timeoutStep, ct).ConfigureAwait(false);
                        }
                    }

                    if (!waveResult.Success)
                    {
                        // ── M14.4 Per-step Required gate ─────────────────
                        // Per-step boundary reports tell us EXACTLY which
                        // steps failed. Required-failure of any one short-
                        // circuits; non-required failures accumulate
                        // hasFailed and the deployment continues.
                        //
                        // Fallback: when the agent didn't report any per-
                        // step boundaries (e.g. it dropped offline before
                        // sending any), we conservatively treat any
                        // Required step in the wave as failed — same as
                        // the pre-M14.4 group-level behaviour.
                        var failedSteps = perStepResults.Where(r => !r.Success).ToList();
                        DeploymentStepPlan? firstRequiredFailure = null;
                        if (failedSteps.Count > 0)
                        {
                            foreach (var failed in failedSteps)
                            {
                                var snap = snapshotSteps[failed.StepIndex];
                                if (snap.Required)
                                {
                                    firstRequiredFailure = stepsToRun
                                        .FirstOrDefault(p => p.Index == failed.StepIndex);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            // No per-step reports — fall back to the M14.0..3
                            // group-level pessimistic gate.
                            firstRequiredFailure = stepsToRun
                                .FirstOrDefault(p => snapshotSteps[p.Index].Required);
                        }

                        if (firstRequiredFailure is not null)
                        {
                            await auditLog.RecordAsync(
                                AuditEventType.DeploymentRequiredStepFailed,
                                subjectType: "Deployment",
                                subjectId:   deployment.Id.ToString(),
                                details:     $"Step={firstRequiredFailure.Name}, " +
                                             $"Error={waveResult.ErrorMessage}",
                                ct: ct).ConfigureAwait(false);
                            await FailAsync(db, deployment,
                                waveResult.ErrorMessage ?? "Agent reported failure", ct)
                                .ConfigureAwait(false);
                            return;
                        }

                        // No Required failure — record non-required failures
                        // (for accurate audit detail) and continue.
                        if (failedSteps.Count > 0)
                        {
                            foreach (var failed in failedSteps)
                            {
                                await LogAndAuditStepFailedNonRequiredAsync(
                                    db, auditLog, logSeq, deployment,
                                    snapshotSteps[failed.StepIndex], ct)
                                    .ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            // Agent didn't report per-step boundaries — flag
                            // every step in the wave as non-required-failed
                            // (matches the pre-M14.4 fallback).
                            foreach (var p in stepsToRun)
                            {
                                await LogAndAuditStepFailedNonRequiredAsync(
                                    db, auditLog, logSeq, deployment,
                                    snapshotSteps[p.Index], ct).ConfigureAwait(false);
                            }
                        }
                        hasFailed = true;
                    }
                }
            }

            // ── M14.2 Finalisation ──────────────────────────────────────
            // hasFailed = true means at least one non-required step failed
            // along the way; the deployment terminates as
            // SucceededWithWarnings (Octopus's yellow-badge state) rather
            // than the pristine Succeeded.
            var terminalStatus = hasFailed
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
        var config = sp.GetRequiredService<IConfiguration>();
        var dataPath = config["DataPath"] ?? "data";

        var targetRoles = deployment.Target?.Roles ?? [];
        var rawVars = await variableService.ResolveAsync(
            deployment.Release.ProjectId,
            deployment.EnvironmentId,
            deployment.TargetId,
            targetRoles,
            deployment.TenantId,
            ct).ConfigureAwait(false);

        // Flatten variables to string dictionary (same as online path).
        var systemVars = OctopusSystemVariablesBuilder.BuildForDeployment(
            deployment,
            deployment.Release,
            deployment.Release.Project,
            deployment.Environment,
            deployment.Target,
            deployment.Tenant,
            deployment.Release.ProcessSnapshot,
            config["Server:BaseUrl"]);

        var flatVars = new Dictionary<string, string>(systemVars, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in rawVars)
        {
            flatVars[name] = value;
        }

        var bundlePath = await dropBundleService
            .GenerateAsync(deployment, flatVars, dataPath, ct)
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
        if (step.StepType.Equals(DeployReleaseStepRunner.StepType, StringComparison.OrdinalIgnoreCase))
        {
            return deployReleaseRunner.ExecuteAsync(deploymentId, step, flatVars, ct);
        }
        return serverRunner.ExecuteAsync(deploymentId, step, flatVars, ct);
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

    /// <summary>
    /// Applies Octostache variable substitution to all values in a step's Config dictionary.
    /// Keys are never substituted (they are well-known step-type contract strings).
    /// </summary>
    private static Dictionary<string, string> SubstituteConfig(
        Dictionary<string, string> config,
        VariableDictionary vars)
    {
        if (config.Count == 0)
        {
            return config;
        }

        return config.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var evaluated = vars.Evaluate(kv.Value);
                return evaluated ?? kv.Value;
            });
    }

    private static async Task FailAsync(
        KrakenDbContext db, Deployment deployment, string reason, CancellationToken ct)
    {
        deployment.Status = DeploymentStatus.Failed;
        deployment.CompletedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── M14.2 + M14.3 helpers ────────────────────────────────────────────

    /// <summary>
    /// Wraps <see cref="RunServerStepWithTimeoutAsync"/> with the M14.3
    /// retry loop for server-side steps. Target-side groups have their
    /// own equivalent retry loop inline in <c>DispatchAsync</c> because
    /// the sub-plan dispatch lifecycle (TCS + subPlans.Register + linked
    /// CTS) doesn't factor cleanly into a generic wrapper without
    /// adding more parameters than the readability gain justifies.
    ///
    /// <para>
    /// On each non-final attempt failure, logs a retry marker + emits
    /// <c>Deployment.StepRetried</c> audit + sleeps
    /// <see cref="StepSnapshot.RetryDelaySeconds"/> before the next try.
    /// Returns <c>(ok, timedOut)</c> reflecting the FINAL attempt only —
    /// the retry detail lives in the deployment-log entries + audit rows.
    /// </para>
    ///
    /// <para>
    /// <c>MaxRetries = 0</c> (default) makes this a single-attempt call,
    /// equivalent to <see cref="RunServerStepWithTimeoutAsync"/> directly.
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
    private async Task<(bool Ok, bool TimedOut)> RunServerStepWithRetriesAsync(
        Guid deploymentId,
        DeploymentStepPlan step,
        StepSnapshot snapshot,
        Deployment deployment,
        KrakenDbContext db,
        IAuditLog audit,
        LogSequencer logSeq,
        IReadOnlyDictionary<string, string> flatVars,
        CancellationToken ct)
    {
        var maxAttempts = snapshot.MaxRetries < 0 ? 0 : snapshot.MaxRetries;
        var delaySeconds = snapshot.RetryDelaySeconds < 0 ? 0 : snapshot.RetryDelaySeconds;

        var attempt = 0;
        while (true)
        {
            var (ok, timedOut) = await RunServerStepWithTimeoutAsync(
                deploymentId, step, snapshot, flatVars, ct).ConfigureAwait(false);

            if (ok)
            {
                if (attempt > 0)
                {
                    db.DeploymentLogEntries.Add(new DeploymentLogEntry
                    {
                        DeploymentId = deployment.Id,
                        Sequence     = logSeq.Next(),
                        Timestamp    = DateTimeOffset.UtcNow,
                        Level        = "info",
                        Message      = $"--- Step '{snapshot.Name}' succeeded on attempt " +
                                       $"{(attempt + 1).ToString(CultureInfo.InvariantCulture)} ---",
                    });
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                return (Ok: true, TimedOut: false);
            }

            if (attempt >= maxAttempts)
            {
                // Final attempt failed — caller applies Required gate.
                return (Ok: false, TimedOut: timedOut);
            }

            // Non-final attempt failed — emit retry marker + audit + delay.
            attempt++;
            var msg = delaySeconds > 0
                ? $"--- Step '{snapshot.Name}' attempt " +
                  $"{attempt.ToString(CultureInfo.InvariantCulture)} failed; retrying in " +
                  $"{delaySeconds.ToString(CultureInfo.InvariantCulture)}s " +
                  $"(attempt {(attempt + 1).ToString(CultureInfo.InvariantCulture)} of " +
                  $"{(maxAttempts + 1).ToString(CultureInfo.InvariantCulture)}) ---"
                : $"--- Step '{snapshot.Name}' attempt " +
                  $"{attempt.ToString(CultureInfo.InvariantCulture)} failed; retrying " +
                  $"(attempt {(attempt + 1).ToString(CultureInfo.InvariantCulture)} of " +
                  $"{(maxAttempts + 1).ToString(CultureInfo.InvariantCulture)}) ---";
            db.DeploymentLogEntries.Add(new DeploymentLogEntry
            {
                DeploymentId = deployment.Id,
                Sequence     = logSeq.Next(),
                Timestamp    = DateTimeOffset.UtcNow,
                Level        = "warning",
                Message      = msg,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordAsync(
                AuditEventType.DeploymentStepRetried,
                subjectType: "Deployment",
                subjectId:   deployment.Id.ToString(),
                details:     $"Step={snapshot.Name}, " +
                             $"Attempt={attempt.ToString(CultureInfo.InvariantCulture)}, " +
                             $"MaxRetries={maxAttempts.ToString(CultureInfo.InvariantCulture)}, " +
                             $"RetryDelaySeconds={delaySeconds.ToString(CultureInfo.InvariantCulture)}",
                ct: ct).ConfigureAwait(false);

            if (delaySeconds > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Deployment cancelled during retry-delay sleep — bail
                    // with the previous attempt's result rather than
                    // re-entering the loop. The outer ct semantics handle
                    // the cancellation reporting.
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Wraps <see cref="ExecuteServerStepAsync"/> with a per-step timeout
    /// from <see cref="StepSnapshot.TimeoutSeconds"/>. Returns
    /// <c>(ok, timedOut)</c> so the caller can distinguish a runner-side
    /// failure from a timeout-induced cancellation.
    /// <para>
    /// <c>TimeoutSeconds = 0</c> means unlimited — short-circuits without
    /// allocating the linked CTS.
    /// </para>
    /// </summary>
    private async Task<(bool Ok, bool TimedOut)> RunServerStepWithTimeoutAsync(
        Guid deploymentId,
        DeploymentStepPlan step,
        StepSnapshot snapshot,
        IReadOnlyDictionary<string, string> flatVars,
        CancellationToken ct)
    {
        if (snapshot.TimeoutSeconds <= 0)
        {
            var ok = await ExecuteServerStepAsync(deploymentId, step, flatVars, ct)
                .ConfigureAwait(false);
            return (ok, false);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(snapshot.TimeoutSeconds));
        try
        {
            var ok = await ExecuteServerStepAsync(
                deploymentId, step, flatVars, linkedCts.Token).ConfigureAwait(false);
            return (ok, false);
        }
        catch (OperationCanceledException)
        {
            // Distinguish per-step timeout from a deployment-level cancellation.
            if (!ct.IsCancellationRequested && linkedCts.IsCancellationRequested)
            {
                return (Ok: false, TimedOut: true);
            }
            throw;
        }
    }

    private static async Task LogAndAuditStepSkippedAsync(
        KrakenDbContext db, IAuditLog audit, LogSequencer logSeq,
        Deployment deployment, StepSnapshot snapshot,
        StepConditionEvaluator.Decision decision,
        CancellationToken ct)
    {
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
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

    private static async Task LogAndAuditStepTimedOutAsync(
        KrakenDbContext db, IAuditLog audit, LogSequencer logSeq,
        Deployment deployment, StepSnapshot snapshot,
        CancellationToken ct)
    {
        var msg = $"--- Step '{snapshot.Name}' timed out after " +
                  $"{snapshot.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}s ---";
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            DeploymentId = deployment.Id,
            Sequence     = logSeq.Next(),
            Timestamp    = DateTimeOffset.UtcNow,
            Level        = "error",
            Message      = msg,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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

    // ── M14.4 wave helpers ──────────────────────────────────────────────

    /// <summary>
    /// One server-side step's outcome inside a wave. <see cref="Skipped"/>
    /// flags steps that were filtered out by Run Condition or by the
    /// role-based <c>StepAppliesToTarget</c> gate so the outer loop can
    /// distinguish "didn't run" from "ran and failed".
    /// </summary>
    private sealed record ServerStepOutcome(
        DeploymentStepPlan Step,
        bool Skipped,
        bool Ok,
        bool TimedOut);

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
                skipped.Add(s);
                continue;
            }
            if (!StepAppliesToTarget(deployment, s))
            {
                skipped.Add(s);
                continue;
            }
            toRun.Add(s);
        }

        if (toRun.Count == 0)
        {
            return skipped.Select(s => new ServerStepOutcome(
                Step: s, Skipped: true, Ok: true, TimedOut: false)).ToList();
        }

        // Fire all surviving steps in parallel. Each Task wraps the M14.3
        // retry helper, which writes its own retry-marker log lines + audit
        // rows. We pass the SHARED db / logSeq into the retry helper —
        // safe today because:
        //   1. LogSequencer is internally locked (M14.3.1).
        //   2. The DbContext writes happen sequentially inside each helper
        //      call (each call awaits SaveChangesAsync before returning).
        //   3. Task.WhenAll resolves after all siblings finish so the worker
        //      doesn't issue new DbContext calls on the same instance from
        //      a different thread.
        // The third point matters: if a runner started a background save we
        // didn't await, this would break. Today's runners are linear.
        var stepTasks = toRun.Select(async s =>
        {
            var snap = snapshotSteps[s.Index];
            var (ok, timedOut) = await RunServerStepWithRetriesAsync(
                deployment.Id, s, snap, deployment, db, auditLog,
                logSeq, flatVars, ct).ConfigureAwait(false);
            if (timedOut)
            {
                await LogAndAuditStepTimedOutAsync(
                    db, auditLog, logSeq, deployment, snap, ct).ConfigureAwait(false);
            }
            return new ServerStepOutcome(
                Step: s, Skipped: false, Ok: ok, TimedOut: timedOut);
        }).ToArray();

        var outcomes = (await Task.WhenAll(stepTasks).ConfigureAwait(false)).ToList();
        outcomes.AddRange(skipped.Select(s => new ServerStepOutcome(
            Step: s, Skipped: true, Ok: true, TimedOut: false)));
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
            string connectionId,
            KrakenDbContext db,
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
            subPlans.Register(deployment.Id, tcs);

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
                lastPerStepResults = subPlans.DrainStepResults(deployment.Id);
                subPlans.Cancel(deployment.Id, "completed");
            }

            if (subPlanResult.Success)
            {
                timedOut = false;
                if (attempt > 0)
                {
                    db.DeploymentLogEntries.Add(new DeploymentLogEntry
                    {
                        DeploymentId = deployment.Id,
                        Sequence     = logSeq.Next(),
                        Timestamp    = DateTimeOffset.UtcNow,
                        Level        = "info",
                        Message      = $"--- Target wave [{waveNamesForAudit}] " +
                                       $"succeeded on attempt " +
                                       $"{(attempt + 1).ToString(CultureInfo.InvariantCulture)} ---",
                    });
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
            db.DeploymentLogEntries.Add(new DeploymentLogEntry
            {
                DeploymentId = deployment.Id,
                Sequence     = logSeq.Next(),
                Timestamp    = DateTimeOffset.UtcNow,
                Level        = "warning",
                Message      =
                    $"--- Target wave [{waveNamesForAudit}] attempt " +
                    $"{attempt.ToString(CultureInfo.InvariantCulture)} failed; retrying " +
                    $"(attempt {(attempt + 1).ToString(CultureInfo.InvariantCulture)} of " +
                    $"{(waveMaxRetries + 1).ToString(CultureInfo.InvariantCulture)})" +
                    (waveRetryDelaySeconds > 0
                        ? $" in {waveRetryDelaySeconds.ToString(CultureInfo.InvariantCulture)}s ---"
                        : " ---"),
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
}
