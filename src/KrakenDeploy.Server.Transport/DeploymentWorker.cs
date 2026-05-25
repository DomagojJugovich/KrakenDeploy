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
                    Sequence     = deployment.NextLogSequence++,
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
                    Sequence     = deployment.NextLogSequence++,
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

            // ── 4. Partition steps into consecutive same-side groups ─────────
            // Each group is either entirely server-side or entirely target-
            // side; we run them in declared order, dispatching target groups
            // to the agent piecewise and awaiting completion before continuing.
            // This supports any ordering — e.g. target → server → target — by
            // making multiple round trips with the agent.
            var groups = PartitionIntoGroups(steps);

            // Transition to Running before doing any work so the UI updates immediately.
            deployment.Status     = DeploymentStatus.Running;
            deployment.StartedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var serverStepCount = groups.Where(g => g.IsServer).Sum(g => g.Steps.Length);
            var targetStepCount = groups.Where(g => !g.IsServer).Sum(g => g.Steps.Length);
            logger.LogInformation(
                "Deployment {DeploymentId}: {Groups} group(s), {ServerSteps} server step(s), " +
                "{TargetSteps} target step(s), {VarCount} variables.",
                deploymentId, groups.Count, serverStepCount, targetStepCount, flatVars.Count);

            // M14.2 — orchestrator now tracks `hasFailed` instead of
            // returning on first failure. Required steps still short-
            // circuit; non-required failures flip the flag and the loop
            // continues so Failure/Always-conditioned cleanup + finalisation
            // steps still run. The deployment's terminal status reflects
            // the final state: hasFailed → SucceededWithWarnings.
            var hasFailed = false;
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLog>();

            foreach (var group in groups)
            {
                if (group.IsServer)
                {
                    // Run each server step in-process. Honours
                    // "Server on behalf of each deployment target" via the
                    // role-filter helper. Dispatch by StepType so server-only
                    // orchestrator steps (e.g. Octopus.DeployRelease) route to
                    // a dedicated runner instead of the generic script runner.
                    foreach (var s in group.Steps)
                    {
                        var snapshot = snapshotSteps[s.Index];

                        // ── M14.2 Run Condition gate ──────────────────────
                        var decision = StepConditionEvaluator.Evaluate(
                            snapshot.Condition,
                            snapshot.ConditionVariableExpression,
                            hasFailed,
                            varDict);
                        if (decision.Action == StepConditionEvaluator.Action.Skip)
                        {
                            await LogAndAuditStepSkippedAsync(
                                db, auditLog, deployment, snapshot, decision, ct)
                                .ConfigureAwait(false);
                            continue;
                        }

                        if (!StepAppliesToTarget(deployment, s))
                        {
                            continue;
                        }

                        // ── M14.2 Per-step Timeout ───────────────────────
                        var (ok, timedOut) = await RunServerStepWithTimeoutAsync(
                            deployment.Id, s, snapshot, flatVars, ct)
                            .ConfigureAwait(false);
                        if (timedOut)
                        {
                            await LogAndAuditStepTimedOutAsync(
                                db, auditLog, deployment, snapshot, ct).ConfigureAwait(false);
                        }

                        if (!ok)
                        {
                            // ── M14.2 Required gate ──────────────────────
                            if (snapshot.Required)
                            {
                                await auditLog.RecordAsync(
                                    AuditEventType.DeploymentRequiredStepFailed,
                                    subjectType: "Deployment",
                                    subjectId:   deployment.Id.ToString(),
                                    details:     $"Step={snapshot.Name}",
                                    ct: ct).ConfigureAwait(false);
                                await FailAsync(db, deployment,
                                    $"Required step '{s.Name}' failed.", ct).ConfigureAwait(false);
                                return;
                            }
                            // Non-required failure — log + audit + continue.
                            await LogAndAuditStepFailedNonRequiredAsync(
                                db, auditLog, deployment, snapshot, ct).ConfigureAwait(false);
                            hasFailed = true;
                        }
                    }
                }
                else
                {
                    // Target group: filter steps by Condition first, then
                    // send a sub-plan to the agent. Per-group Timeout uses
                    // the longest TimeoutSeconds across the group's steps.
                    var stepsToRun = new List<DeploymentStepPlan>(group.Steps.Length);
                    foreach (var s in group.Steps)
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
                                db, auditLog, deployment, snapshot, decision, ct)
                                .ConfigureAwait(false);
                            continue;
                        }
                        stepsToRun.Add(s);
                    }

                    if (stepsToRun.Count == 0)
                    {
                        continue; // every step in the group was skipped by Condition
                    }

                    var connectionId = registry.GetConnectionId(deployment.TargetId.Value);
                    if (connectionId is null)
                    {
                        await FailAsync(db, deployment, "Target is offline.", ct).ConfigureAwait(false);
                        return;
                    }

                    var subPlan = plan with { Steps = stepsToRun.ToArray() };

                    // ── M14.2 Group Timeout ──────────────────────────────
                    // Use the longest per-step timeout in the group. 0 = unlimited.
                    var groupTimeoutSeconds = stepsToRun
                        .Select(p => snapshotSteps[p.Index].TimeoutSeconds)
                        .DefaultIfEmpty(0)
                        .Max();

                    var tcs = new TaskCompletionSource<SubPlanResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    subPlans.Register(deployment.Id, tcs);

                    SubPlanResult subPlanResult;
                    var timedOut = false;
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (groupTimeoutSeconds > 0)
                    {
                        linkedCts.CancelAfter(TimeSpan.FromSeconds(groupTimeoutSeconds));
                    }

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
                            // Timeout fired (or external cancel). Distinguish
                            // by whether the outer ct was cancelled too.
                            if (!ct.IsCancellationRequested && linkedCts.IsCancellationRequested)
                            {
                                timedOut = true;
                                subPlanResult = new SubPlanResult(
                                    Success: false,
                                    ErrorMessage:
                                        $"Target step group timed out after {groupTimeoutSeconds}s.");
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        subPlans.Cancel(deployment.Id, "completed");
                    }

                    if (timedOut)
                    {
                        // Emit a TimedOut audit for the first step with a
                        // non-zero TimeoutSeconds in the group (the one the
                        // operator most likely configured).
                        var timeoutStep = stepsToRun
                            .Select(p => snapshotSteps[p.Index])
                            .FirstOrDefault(snap => snap.TimeoutSeconds > 0);
                        if (timeoutStep is not null)
                        {
                            await LogAndAuditStepTimedOutAsync(
                                db, auditLog, deployment, timeoutStep, ct).ConfigureAwait(false);
                        }
                    }

                    if (!subPlanResult.Success)
                    {
                        // ── M14.2 Required gate at the group level ──────
                        // If ANY step in the dispatched group is Required,
                        // the whole sub-plan failure aborts the deployment.
                        // The simplification is conservative: it never
                        // continues past a Required failure, but may
                        // pessimistically abort even when the actually-
                        // failing step was non-required. Per-step attribution
                        // needs an agent contract change (M14.4-adjacent)
                        // for the agent to report which step failed.
                        var anyRequired = stepsToRun.Any(p =>
                            snapshotSteps[p.Index].Required);
                        if (anyRequired)
                        {
                            await auditLog.RecordAsync(
                                AuditEventType.DeploymentRequiredStepFailed,
                                subjectType: "Deployment",
                                subjectId:   deployment.Id.ToString(),
                                details:     $"TargetGroup steps=[{string.Join(", ",
                                                stepsToRun.Select(p => p.Name))}], " +
                                             $"Error={subPlanResult.ErrorMessage}",
                                ct: ct).ConfigureAwait(false);
                            await FailAsync(db, deployment,
                                subPlanResult.ErrorMessage ?? "Agent reported failure", ct)
                                .ConfigureAwait(false);
                            return;
                        }
                        // Whole group is non-required — record + continue.
                        foreach (var p in stepsToRun)
                        {
                            await LogAndAuditStepFailedNonRequiredAsync(
                                db, auditLog, deployment, snapshotSteps[p.Index], ct)
                                .ConfigureAwait(false);
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

    /// <summary>
    /// A consecutive run of same-side steps (all server-side or all target-side).
    /// </summary>
    private sealed record StepGroup(bool IsServer, DeploymentStepPlan[] Steps);

    /// <summary>
    /// Partition steps into consecutive same-side groups in declared order.
    /// E.g. [target, target, server, target] → [Target(2), Server(1), Target(1)].
    /// </summary>
    private static List<StepGroup> PartitionIntoGroups(DeploymentStepPlan[] steps)
    {
        var groups = new List<StepGroup>();
        if (steps.Length == 0)
        {
            return groups;
        }

        var ordered = steps.OrderBy(s => s.Index).ToArray();
        var current = new List<DeploymentStepPlan> { ordered[0] };
        var currentIsServer = IsServerStep(ordered[0]);

        for (var i = 1; i < ordered.Length; i++)
        {
            var s      = ordered[i];
            var isSrv  = IsServerStep(s);
            if (isSrv == currentIsServer)
            {
                current.Add(s);
            }
            else
            {
                groups.Add(new StepGroup(currentIsServer, [.. current]));
                current = [s];
                currentIsServer = isSrv;
            }
        }
        groups.Add(new StepGroup(currentIsServer, [.. current]));
        return groups;
    }

    /// <summary>
    /// True if the step's config marks it for server-side execution. A step is
    /// server-side when EITHER the config carries
    /// <c>Octopus.Action.RunOnServer = "true"</c> (the explicit Octopus marker),
    /// OR the <see cref="DeploymentStepPlan.StepType"/> is one of the
    /// intrinsically server-side orchestrator types (<see cref="ServerOnlyStepTypes"/>).
    /// </summary>
    private static bool IsServerStep(DeploymentStepPlan step)
    {
        if (step.Config.TryGetValue("Octopus.Action.RunOnServer", out var v)
            && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return ServerOnlyStepTypes.Contains(step.StepType);
    }

    /// <summary>
    /// Step types that always run on the server regardless of the
    /// <c>Octopus.Action.RunOnServer</c> flag — they coordinate other deployments
    /// or otherwise have no agent-side meaning.
    /// </summary>
    private static readonly HashSet<string> ServerOnlyStepTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            DeployReleaseStepRunner.StepType,
        };

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

    // ── M14.2 helpers ────────────────────────────────────────────────────

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
        KrakenDbContext db, IAuditLog audit,
        Deployment deployment, StepSnapshot snapshot,
        StepConditionEvaluator.Decision decision,
        CancellationToken ct)
    {
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            DeploymentId = deployment.Id,
            Sequence     = deployment.NextLogSequence++,
            Timestamp    = DateTimeOffset.UtcNow,
            Level        = "info",
            Message      = $"--- Step '{snapshot.Name}' skipped: {decision.Reason} ---",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Variable conditions get a dedicated audit type so operators can
        // filter for "deployments where the expression didn't resolve."
        // Other skips share the StepSkipped event type.
        var eventType = decision.Reason.Contains("unresolved", StringComparison.Ordinal)
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
        KrakenDbContext db, IAuditLog audit,
        Deployment deployment, StepSnapshot snapshot,
        CancellationToken ct)
    {
        var msg = $"--- Step '{snapshot.Name}' timed out after " +
                  $"{snapshot.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}s ---";
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            DeploymentId = deployment.Id,
            Sequence     = deployment.NextLogSequence++,
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
        KrakenDbContext db, IAuditLog audit,
        Deployment deployment, StepSnapshot snapshot,
        CancellationToken ct)
    {
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            DeploymentId = deployment.Id,
            Sequence     = deployment.NextLogSequence++,
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
}
