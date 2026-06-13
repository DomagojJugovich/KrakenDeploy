using System.Security.Claims;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// SignalR hub for agent ↔ server control-plane communication.
/// Authenticated with the "AgentJwt" bearer scheme; token is delivered
/// via query string (<c>?access_token=…</c>) because WebSocket upgrades
/// cannot carry custom headers.
/// </summary>
[Authorize(AuthenticationSchemes = "AgentJwt")]
public sealed class AgentHub(
    IAgentConnectionRegistry registry,
    IDbContextFactory<KrakenDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    TargetStatusPublisher statusPublisher,
    TimeProvider timeProvider,
    IHubContext<UiHub, IUiHubClient> uiHub,
    IPendingSubPlanRegistry subPlans,
    IPendingAdhocRegistry adhocPending,
    ILogger<AgentHub> logger)
    : Hub<IAgentHubClient>, IAgentHubServer
{
    // ── Connection lifecycle ────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var targetId = GetTargetId();
        if (targetId is null)
        {
            logger.LogWarning(
                "Agent connected (conn {ConnectionId}) with no valid NameIdentifier claim; aborting.",
                Context.ConnectionId);
            Context.Abort();
            return;
        }

        registry.Add(Context.ConnectionId, targetId.Value);

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        // The target id is the authenticated agent's own NameIdentifier; load it
        // filter-free since the hub has no ambient Space and the global filter
        // would otherwise hide a target that lives in a non-Default Space.
        var target = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == targetId.Value)
            .ConfigureAwait(false);

        if (target is not null)
        {
            target.Status = TargetStatus.Online;
            target.LastSeenUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync().ConfigureAwait(false);

            logger.LogInformation(
                "Target {TargetId} connected (conn {ConnectionId}); marked Online.",
                targetId.Value, Context.ConnectionId);

            await statusPublisher
                .PublishAsync(targetId.Value, TargetStatus.Online, target.LastSeenUtc)
                .ConfigureAwait(false);
        }
        else
        {
            logger.LogWarning(
                "Target {TargetId} connected but was not found in the database.",
                targetId.Value);
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (registry.TryRemove(Context.ConnectionId, out var targetId))
        {
            if (exception is not null)
            {
                logger.LogWarning(
                    exception,
                    "Target {TargetId} disconnected with error; scheduling offline mark.",
                    targetId);
            }
            else
            {
                logger.LogInformation(
                    "Target {TargetId} disconnected cleanly; scheduling offline mark.",
                    targetId);
            }

            // Fire-and-forget with 30 s grace period so brief reconnects don't flicker.
            _ = MarkOfflineAfterGraceAsync(
                targetId, scopeFactory, registry, statusPublisher, timeProvider, logger);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    // ── IAgentHubServer implementation ─────────────────────────────────────

    public async Task RegisterAsync(AgentRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetId = GetTargetId();
        if (targetId is null)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        // The target id is the authenticated agent's own NameIdentifier; load it
        // filter-free since the hub has no ambient Space and the global filter
        // would otherwise hide a target that lives in a non-Default Space.
        var target = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == targetId.Value)
            .ConfigureAwait(false);

        if (target is null)
        {
            return;
        }

        target.MachineName = request.MachineName;
        target.OperatingSystem = request.OperatingSystem;
        target.AgentVersion = request.AgentVersion;
        // Only overwrite roles when the agent sends a non-empty list; otherwise
        // preserve what was configured in the registration wizard.
        if (request.Roles.Count > 0)
        {
            target.Roles = request.Roles.ToList();
        }

        target.Status = TargetStatus.Online;
        target.LastSeenUtc = timeProvider.GetUtcNow();

        await db.SaveChangesAsync().ConfigureAwait(false);

        logger.LogInformation(
            "Target {TargetId} registered: machine={Machine}, OS={OS}, agent={Version}, roles=[{Roles}].",
            targetId.Value,
            request.MachineName,
            request.OperatingSystem,
            request.AgentVersion,
            string.Join(", ", request.Roles));
    }

    public async Task HeartbeatAsync(HeartbeatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetId = GetTargetId();
        if (targetId is null)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        // The target id is the authenticated agent's own NameIdentifier; load it
        // filter-free since the hub has no ambient Space and the global filter
        // would otherwise hide a target that lives in a non-Default Space.
        var target = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == targetId.Value)
            .ConfigureAwait(false);

        if (target is null)
        {
            return;
        }

        target.LastSeenUtc = timeProvider.GetUtcNow();
        if (request.MachineName is not null) { target.MachineName = request.MachineName; }
        if (request.OperatingSystem is not null) { target.OperatingSystem = request.OperatingSystem; }
        if (request.AgentVersion is not null) { target.AgentVersion = request.AgentVersion; }

        await db.SaveChangesAsync().ConfigureAwait(false);
        logger.LogDebug("Heartbeat from target {TargetId}.", targetId.Value);
    }

    public Task ReportStatusAsync(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var targetId = GetTargetId();
        logger.LogInformation(
            "Status report from target {TargetId}: {Status}.", targetId, status);
        return Task.CompletedTask;
    }

    public async Task AppendLogAsync(Guid deploymentId, string level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var connectionTargetId = GetTargetId();
        var timestamp = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Try Deployment first, then RunbookRun (same ID space, non-overlapping GUIDs).
        // The agent reports against a deployment/run id; the hub has no ambient
        // Space (DefaultSpaceId), so load filter-free — the global filter would
        // otherwise hide a deployment that lives in a non-Default Space. Writes
        // below stamp SpaceId explicitly from the loaded row.
        var deployment = await db.Deployments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deploymentId)
            .ConfigureAwait(false);

        if (deployment is not null)
        {
            if (connectionTargetId is null
                || !await AgentDeploymentOwnership.ConnectionOwnsDeploymentAsync(
                    db, deployment, connectionTargetId.Value).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "AppendLog rejected: target {Target} is not assigned to deployment {Id}.",
                    connectionTargetId, deploymentId);
                return;
            }

            var seq = deployment.NextLogSequence++;
            db.DeploymentLogEntries.Add(new KrakenDeploy.Server.Core.Domain.Deployments.DeploymentLogEntry
            {
                SpaceId = deployment.SpaceId,
                DeploymentId = deploymentId,
                Sequence = seq,
                Timestamp = timestamp,
                Message = message,
                Level = level,
            });
            await db.SaveChangesAsync().ConfigureAwait(false);

            await uiHub.Clients.Group($"deployment:{deploymentId}")
                .DeploymentLogAppendedAsync(deploymentId, seq, timestamp, level, message)
                .ConfigureAwait(false);
            return;
        }

        var run = await db.RunbookRuns
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == deploymentId)
            .ConfigureAwait(false);

        if (run is not null)
        {
            if (connectionTargetId is null || run.TargetId != connectionTargetId)
            {
                logger.LogWarning(
                    "AppendLog rejected: target {Target} does not own runbook run {Id}.",
                    connectionTargetId, deploymentId);
                return;
            }

            var seq = run.NextLogSequence++;
            db.RunbookRunLogEntries.Add(new KrakenDeploy.Server.Core.Domain.Runbooks.RunbookRunLogEntry
            {
                SpaceId = run.SpaceId,
                RunbookRunId = deploymentId,
                Sequence = seq,
                Timestamp = timestamp,
                Message = message,
                Level = level,
            });
            await db.SaveChangesAsync().ConfigureAwait(false);

            await uiHub.Clients.Group($"deployment:{deploymentId}")
                .DeploymentLogAppendedAsync(deploymentId, seq, timestamp, level, message)
                .ConfigureAwait(false);
            return;
        }

        logger.LogWarning("AppendLog for unknown run {Id}; ignored.", deploymentId);
    }

    public async Task CompleteDeploymentAsync(
        Guid deploymentId, bool success, string? errorMessage)
    {
        // If this is a sub-plan completion (the worker is mid-orchestration
        // for a multi-target or mixed-side process), resolve the pending TCS
        // keyed by (deploymentId, this connection's target id) and stop here —
        // DeploymentWorker will finalize the deployment status once every
        // wave / target has completed.
        //
        // M-RollingDeployments Phase 1b: slot key is (deployment, target);
        // the target id comes from the connection's NameIdentifier claim, so
        // no wire-contract change. Pre-1b single-target dispatch is reached
        // through the same path (the orchestrator registers under the same
        // single target id).
        var connectionTargetId = GetTargetId();
        if (connectionTargetId is not null
            && subPlans.TryResolve(
                deploymentId, connectionTargetId.Value,
                new SubPlanResult(success, errorMessage)))
        {
            logger.LogDebug(
                "Sub-plan complete for deployment {Id} target {Target} (success={Success}); " +
                "orchestrator will continue.",
                deploymentId, connectionTargetId.Value, success);
            return;
        }

        var completedAt = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Try Deployment first.
        // The agent reports against a deployment/run id; the hub has no ambient
        // Space (DefaultSpaceId), so load filter-free — the global filter would
        // otherwise hide a deployment that lives in a non-Default Space. Writes
        // below stamp SpaceId explicitly from the loaded row.
        var deployment = await db.Deployments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deploymentId)
            .ConfigureAwait(false);

        if (deployment is not null)
        {
            if (connectionTargetId is null
                || !await AgentDeploymentOwnership.ConnectionOwnsDeploymentAsync(
                    db, deployment, connectionTargetId.Value).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "CompleteDeployment rejected: target {Target} is not assigned to deployment {Id}.",
                    connectionTargetId, deploymentId);
                return;
            }

            deployment.Status = success
                ? KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Succeeded
                : KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Failed;
            deployment.CompletedUtc = completedAt;
            await db.SaveChangesAsync().ConfigureAwait(false);

            var statusStr = deployment.Status.ToString();
            await uiHub.Clients.Group($"deployment:{deploymentId}")
                .DeploymentStatusChangedAsync(deploymentId, statusStr)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Deployment {Id} completed: {Status}{Error}.",
                deploymentId, statusStr,
                errorMessage is null ? "" : $" — {errorMessage}");

            // Prune old deployments per retention policy.
            if (success)
            {
                _ = PruneRetentionAsync(deploymentId, scopeFactory, logger);
            }

            return;
        }

        // Try RunbookRun.
        var run = await db.RunbookRuns
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == deploymentId)
            .ConfigureAwait(false);

        if (run is not null)
        {
            if (connectionTargetId is null || run.TargetId != connectionTargetId)
            {
                logger.LogWarning(
                    "CompleteDeployment rejected: target {Target} does not own runbook run {Id}.",
                    connectionTargetId, deploymentId);
                return;
            }

            run.Status = success
                ? KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Succeeded
                : KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Failed;
            run.CompletedUtc = completedAt;
            await db.SaveChangesAsync().ConfigureAwait(false);

            var statusStr = run.Status.ToString();
            await uiHub.Clients.Group($"deployment:{deploymentId}")
                .DeploymentStatusChangedAsync(deploymentId, statusStr)
                .ConfigureAwait(false);

            logger.LogInformation(
                "RunbookRun {Id} completed: {Status}{Error}.",
                deploymentId, statusStr,
                errorMessage is null ? "" : $" — {errorMessage}");
            return;
        }

        logger.LogWarning("CompleteDeployment for unknown run {Id}; ignored.", deploymentId);
    }

    /// <summary>
    /// M14.4 — per-step boundary callback from the agent. Persists captured
    /// output variables (same upsert as the pre-M14.4 path) AND records the
    /// per-step outcome in <see cref="IPendingSubPlanRegistry"/> so
    /// <see cref="DeploymentWorker"/> can attribute Required failures to
    /// the actual failing step inside a parallel wave.
    ///
    /// <para>
    /// The DB shape is unchanged: <c>DeploymentOutputVariable</c> rows
    /// keyed by (DeploymentId, StepName, Name). Per-step attribution
    /// lives in the registry's in-memory bag rather than a new table —
    /// per-step state is only useful within the wave window, the audit
    /// log already captures forensic detail (Required/non-required
    /// failures, retries, timeouts), and persisting a per-step status
    /// column would add migration churn without operator benefit.
    /// </para>
    /// </summary>
    public async Task ReportStepCompletedAsync(
        Guid deploymentId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        Dictionary<string, string> outputVariables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(outputVariables);

        // Register the per-step outcome with the sub-plan registry FIRST so
        // even if DB persistence fails, the orchestrator gets attribution
        // for the wave's per-step Required gate. Late reports for waves
        // that already resolved are dropped silently inside RecordStepResult.
        //
        // M-RollingDeployments Phase 1b: slot key is (deployment, this
        // connection's target id). The target id comes from the connection's
        // NameIdentifier claim — no wire-contract change.
        var connectionTargetId = GetTargetId();
        if (connectionTargetId is not null)
        {
            subPlans.RecordStepResult(
                deploymentId, connectionTargetId.Value,
                new SubPlanStepResult(
                    StepIndex:    stepIndex,
                    StepName:     stepName,
                    Success:      success,
                    ErrorMessage: errorMessage,
                    Outputs:      new Dictionary<string, string>(
                                      outputVariables, StringComparer.OrdinalIgnoreCase)));
        }

        if (outputVariables.Count == 0)
        {
            // No outputs to persist; per-step outcome already recorded.
            return;
        }

        var capturedAt = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Resolve the deployment's Space directly (IgnoreQueryFilters — the hub
        // has no real Space context) both to confirm it exists and to stamp the
        // output-variable rows so they aren't mis-scoped to the Default Space.
        var deploymentScope = await db.Deployments.IgnoreQueryFilters()
            .Where(d => d.Id == deploymentId)
            .Select(d => new { d.SpaceId, d.TargetId })
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (deploymentScope is null)
        {
            // Runbook runs don't currently capture output variables — when they
            // do, route here using a parallel table. Until then, ignore.
            logger.LogDebug(
                "ReportStepCompleted for unknown deployment {Id}; ignored.", deploymentId);
            return;
        }

        // Ownership: only a target assigned to this deployment may persist its
        // output variables — otherwise a foreign agent could inject outputs that
        // later steps consume.
        if (connectionTargetId is null
            || !(deploymentScope.TargetId == connectionTargetId
                 || await db.DeploymentTargetAssignments.IgnoreQueryFilters()
                        .AnyAsync(a => a.DeploymentId == deploymentId
                                       && a.TargetId == connectionTargetId.Value)
                        .ConfigureAwait(false)))
        {
            logger.LogWarning(
                "ReportStepCompleted rejected: target {Target} is not assigned to deployment {Id}.",
                connectionTargetId, deploymentId);
            return;
        }

        var spaceId = deploymentScope.SpaceId;

        var existing = await db.DeploymentOutputVariables
            .Where(o => o.DeploymentId == deploymentId && o.StepName == stepName)
            .ToDictionaryAsync(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ConfigureAwait(false);

        foreach (var (name, value) in outputVariables)
        {
            if (existing.TryGetValue(name, out var row))
            {
                row.Value = value;
                row.CapturedUtc = capturedAt;
            }
            else
            {
                db.DeploymentOutputVariables.Add(
                    new KrakenDeploy.Server.Core.Domain.Deployments.DeploymentOutputVariable
                    {
                        SpaceId      = spaceId,
                        DeploymentId = deploymentId,
                        StepName     = stepName,
                        Name         = name,
                        Value        = value,
                        CapturedUtc  = capturedAt,
                    });
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        logger.LogInformation(
            "Step '{Step}' (index {Index}) of deployment {Id} completed: " +
            "success={Success}, outputs={Count}.",
            stepName, stepIndex, deploymentId, success, outputVariables.Count);
    }

    /// <summary>
    /// M11.E.7 — the agent's per-target adhoc-script outcome callback. The
    /// hub resolves the connection's target id from its NameIdentifier claim
    /// and routes the result to the matching slot in
    /// <see cref="IPendingAdhocRegistry"/>. Late reports for a slot that has
    /// already been cancelled (timeout / cleanup) are dropped silently — the
    /// dispatcher's TCS has already resolved with an AgentError.
    /// </summary>
    public Task ReportAdhocResultAsync(AdhocScriptResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var connectionTargetId = GetTargetId();
        if (connectionTargetId is null)
        {
            logger.LogWarning(
                "ReportAdhocResult from connection {ConnectionId} with no valid " +
                "NameIdentifier claim; dropping.", Context.ConnectionId);
            return Task.CompletedTask;
        }

        if (!adhocPending.TryResolve(
                result.SessionId, result.IterNumber, connectionTargetId.Value, result))
        {
            logger.LogDebug(
                "ReportAdhocResult for session {SessionId} iter {Iter} target {Target} " +
                "arrived after the slot was cancelled / unknown; dropping.",
                result.SessionId, result.IterNumber, connectionTargetId.Value);
        }
        return Task.CompletedTask;
    }

    private static async Task PruneRetentionAsync(
        Guid deploymentId,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var retention = scope.ServiceProvider
                .GetRequiredService<KrakenDeploy.Server.Data.Services.RetentionService>();
            await retention.PruneAfterDeploymentAsync(deploymentId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running retention pruning for deployment {Id}.", deploymentId);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private Guid? GetTargetId()
    {
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static async Task MarkOfflineAfterGraceAsync(
        Guid targetId,
        IServiceScopeFactory scopeFactory,
        IAgentConnectionRegistry registry,
        TargetStatusPublisher statusPublisher,
        TimeProvider timeProvider,
        ILogger logger)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            // If the agent reconnected during the grace period, do nothing.
            if (registry.HasConnectionFor(targetId))
            {
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

            // Filter-free: own-target by-id read in a Space-less background scope.
            var target = await db.DeploymentTargets
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == targetId)
                .ConfigureAwait(false);

            if (target is null || target.Status != TargetStatus.Online)
            {
                return;
            }

            target.Status = TargetStatus.Offline;
            target.LastSeenUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync().ConfigureAwait(false);

            logger.LogInformation(
                "Target {TargetId} marked Offline after 30 s grace period.", targetId);

            await statusPublisher
                .PublishAsync(targetId, TargetStatus.Offline, target.LastSeenUtc)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled error in grace-period offline task for target {TargetId}.", targetId);
        }
    }
}
