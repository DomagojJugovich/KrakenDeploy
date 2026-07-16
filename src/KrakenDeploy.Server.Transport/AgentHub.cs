using System.Security.Claims;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
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
    IAccountContext accountContext,
    KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService encryption,
    KrakenDeploy.Server.Core.Domain.Audit.IAuditLog auditLog,
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

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        // The target id is the authenticated agent's own NameIdentifier; load it
        // filter-free since the hub has no ambient Space and the global filter
        // would otherwise hide a target that lives in a non-Default Space. In
        // multi-account mode AgentAccountHubFilter has already pinned this
        // connection's account, so this reads the correct tenant database.
        var target = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == targetId.Value)
            .ConfigureAwait(false);

        if (target is null)
        {
            // Fail closed: a connection whose target does not exist is rejected, not
            // left registered. It is either a stale credential (target deleted) or —
            // in multi-account — an agent that reached the wrong account: the target
            // id is globally unique, so a foreign target simply is not in this
            // account's database. (Pre-P3-8 this only logged and stayed connected.)
            logger.LogWarning(
                "Target {TargetId} connected (conn {ConnectionId}) but was not found; aborting.",
                targetId.Value, Context.ConnectionId);
            Context.Abort();
            return;
        }

        // Register only after the target is positively resolved (in the right account).
        // Record the connection's account (host-derived, pinned by AgentAccountHubFilter
        // before this runs; Guid.Empty single-instance) so dispatch can assert a target's
        // live connection belongs to the dispatching account (P3-8 Phase 5).
        var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
        registry.Add(
            Context.ConnectionId,
            targetId.Value,
            accountId,
            // A8/T1-12: lets a token revocation drop this live tunnel immediately.
            Context.Abort);

        target.Status = TargetStatus.Online;
        target.LastSeenUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync().ConfigureAwait(false);

        logger.LogInformation(
            "Target {TargetId} connected (conn {ConnectionId}); marked Online.",
            targetId.Value, Context.ConnectionId);

        await statusPublisher
            .PublishAsync(targetId.Value, TargetStatus.Online, target.LastSeenUtc, accountId)
            .ConfigureAwait(false);

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

            // Capture the account now, while AgentAccountHubFilter still has it pinned
            // (Guid.Empty single-instance) — the deferred task needs it to scope the UI
            // push to this tenant's group. The task's tenant DB write rides the same
            // account via the ambient AsyncLocal captured into its ExecutionContext.
            var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;

            // Fire-and-forget with 30 s grace period so brief reconnects don't flicker.
            _ = MarkOfflineAfterGraceAsync(
                targetId, accountId, scopeFactory, registry, statusPublisher, timeProvider, logger);
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

        // T1-7: an agent reports machine capabilities only. Authorization roles
        // drive secret scoping (VariableScope.Matches resolves against the
        // target's CURRENT roles at dispatch), so they are OPERATOR-assigned via
        // the registration wizard / target-edit UI / API — never self-declared.
        // A compromised low-trust agent registering with Roles=["prod-secrets"]
        // must not thereby receive those scoped secrets. request.Roles is ignored.
        target.MachineName = request.MachineName;
        target.OperatingSystem = request.OperatingSystem;
        target.AgentVersion = request.AgentVersion;

        target.Status = TargetStatus.Online;
        target.LastSeenUtc = timeProvider.GetUtcNow();

        await db.SaveChangesAsync().ConfigureAwait(false);

        // A non-empty Roles payload signals a tampered or outdated agent. Record
        // it (value ignored) so operators can spot the attempt.
        if (request.Roles.Count > 0)
        {
            var rejected = string.Join(", ", request.Roles);
            logger.LogWarning(
                "Target {TargetId} sent a Roles payload on registration; ignoring it " +
                "(roles are operator-assigned). Rejected: [{Roles}].",
                targetId.Value, rejected);
            await auditLog.RecordAsync(
                KrakenDeploy.Server.Core.Domain.Audit.AuditEventType.AgentRoleSelfAssignmentRejected,
                subjectType: "DeploymentTarget",
                subjectId:   targetId.Value.ToString(),
                subjectName: target.Name,
                details:     $"Ignored self-declared roles: [{rejected}]").ConfigureAwait(false);
        }

        logger.LogInformation(
            "Target {TargetId} registered: machine={Machine}, OS={OS}, agent={Version}.",
            targetId.Value,
            request.MachineName,
            request.OperatingSystem,
            request.AgentVersion);
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

    public async Task AppendLogAsync(Guid deploymentId, int stepIndex, string level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var connectionTargetId = GetTargetId();
        var timestamp = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // ONE lookup — deployments and runbook runs share the server_tasks spine
        // (no more Deployment-then-RunbookRun probe). Filter-free: the hub has no
        // ambient Space (DefaultSpaceId); ownership is enforced via the join below.
        var task = await db.ServerTasks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == deploymentId)
            .ConfigureAwait(false);
        if (task is null)
        {
            logger.LogWarning("AppendLog for unknown task {Id}; ignored.", deploymentId);
            return;
        }

        if (connectionTargetId is null
            || !await AgentDeploymentOwnership.ConnectionOwnsTaskAsync(
                db, task, connectionTargetId.Value).ConfigureAwait(false))
        {
            logger.LogWarning(
                "AppendLog rejected: target {Target} is not assigned to task {Id}.",
                connectionTargetId, deploymentId);
            return;
        }

        // DB-atomic sequence + staging insert (shared with the server-side path).
        var seq = await TaskLogService.AppendLiveAsync(
            db, deploymentId, stepIndex, connectionTargetId, level, message, timestamp)
            .ConfigureAwait(false);

        await uiHub.Clients.Group($"deployment:{deploymentId}")
            .DeploymentLogAppendedAsync(deploymentId, seq, timestamp, level, message)
            .ConfigureAwait(false);
    }

    public async Task CompleteDeploymentAsync(
        Guid deploymentId, Guid dispatchId, bool success, string? errorMessage)
    {
        // If this is a sub-plan completion (the worker is mid-orchestration
        // for a multi-target or mixed-side process), resolve the pending TCS
        // keyed by (deploymentId, this connection's target id) and stop here —
        // DeploymentWorker will finalize the deployment status once every
        // wave / target has completed.
        //
        // M-RollingDeployments Phase 1b: slot key is (deployment, target);
        // the target id comes from the connection's NameIdentifier claim.
        // B2 (B6.2): dispatchId pins the completion to the attempt that
        // produced it — the agent's at-least-once report outbox may deliver a
        // completion late (after the wave was cancelled/re-dispatched) or
        // twice (ack lost in a disconnect). Neither may resolve a DIFFERENT
        // attempt's TCS, and neither may fall through to the DB fallback
        // below, which would finalize a mid-flight deployment.
        var connectionTargetId = GetTargetId();
        if (connectionTargetId is not null)
        {
            var route = subPlans.RouteCompletion(
                deploymentId, connectionTargetId.Value, dispatchId,
                new SubPlanResult(success, errorMessage));

            if (route == SubPlanCompletionRoute.ResolvedPending)
            {
                logger.LogDebug(
                    "Sub-plan complete for deployment {Id} target {Target} dispatch {Dispatch} " +
                    "(success={Success}); orchestrator will continue.",
                    deploymentId, connectionTargetId.Value, dispatchId, success);
                return;
            }

            if (route == SubPlanCompletionRoute.StaleOrDuplicate)
            {
                logger.LogWarning(
                    "Stale or duplicate completion for deployment {Id} target {Target} " +
                    "dispatch {Dispatch} (success={Success}); swallowed.",
                    deploymentId, connectionTargetId.Value, dispatchId, success);
                return;
            }
        }

        var completedAt = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // ONE lookup on the unified spine. Reached only for a non-orchestrated
        // completion (the orchestrator resolves its waves via the sub-plan registry
        // above and finalises + compacts itself); kept robust as a fallback.
        var task = await db.ServerTasks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == deploymentId)
            .ConfigureAwait(false);
        if (task is null)
        {
            logger.LogWarning("CompleteDeployment for unknown task {Id}; ignored.", deploymentId);
            return;
        }

        if (connectionTargetId is null
            || !await AgentDeploymentOwnership.ConnectionOwnsTaskAsync(
                db, task, connectionTargetId.Value).ConfigureAwait(false))
        {
            logger.LogWarning(
                "CompleteDeployment rejected: target {Target} is not assigned to task {Id}.",
                connectionTargetId, deploymentId);
            return;
        }

        // B1: never overwrite a terminal status. A late agent callback can race
        // an operator cancel, or arrive after the dispatch reconciler already
        // failed this task as interrupted (its sub-plan TCS died with the old
        // process, so the completion routing above missed) — the recorded
        // verdict wins over a stale "success" that reflects only one target's
        // sub-plan. B5: the guarded writer makes the check atomic — a cancel
        // landing between the old status read and the save can no longer be
        // flipped back, and retention/compaction below never run for a write
        // this callback didn't win.
        var wrote = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task, t =>
            {
                t.Status = success ? DeploymentStatus.Succeeded : DeploymentStatus.Failed;
                t.CompletedUtc = completedAt;
                // B1: terminal — release the dispatch lease (runbook hand-off hygiene).
                t.ClaimedBy = null;
                t.LeaseUntil = null;
            }).ConfigureAwait(false);
        if (!wrote)
        {
            logger.LogWarning(
                "CompleteDeployment for task {Id} ignored: already terminal or pruned " +
                "(last read status: {Status}).",
                deploymentId, task.Status);
            return;
        }

        // Terminal: sweep any remaining staging lines into per-step blobs.
        await TaskLogService.CompactRemainingAsync(db, deploymentId, completedAt).ConfigureAwait(false);

        var statusStr = task.Status.ToString();
        await uiHub.Clients.Group($"deployment:{deploymentId}")
            .DeploymentStatusChangedAsync(deploymentId, statusStr)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Task {Id} completed: {Status}{Error}.",
            deploymentId, statusStr, errorMessage is null ? "" : $" — {errorMessage}");

        // Retention pruning for both kinds: deployments prune per the lifecycle
        // phase policy, runbook runs per a fixed keep (RetentionService). Both
        // cascade-delete their log/step/output children with the parent row.
        if (success)
        {
            _ = PruneRetentionAsync(deploymentId, task.Kind, scopeFactory, logger);
        }
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
        Guid dispatchId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        Dictionary<string, string> outputVariables,
        List<string> sensitiveOutputNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(outputVariables);
        // Older agents / offline JSON may omit the sensitive-name list.
        sensitiveOutputNames ??= [];

        // Register the per-step outcome with the sub-plan registry FIRST so
        // even if DB persistence fails, the orchestrator gets attribution
        // for the wave's per-step Required gate. Late reports for waves
        // that already resolved — and, B2, stale reports whose dispatchId
        // belongs to a previous attempt of a re-dispatched wave — are
        // dropped silently inside RecordStepResult.
        //
        // M-RollingDeployments Phase 1b: slot key is (deployment, this
        // connection's target id). The target id comes from the connection's
        // NameIdentifier claim.
        var connectionTargetId = GetTargetId();
        if (connectionTargetId is not null)
        {
            subPlans.RecordStepResult(
                deploymentId, connectionTargetId.Value, dispatchId,
                new SubPlanStepResult(
                    StepIndex:    stepIndex,
                    StepName:     stepName,
                    Success:      success,
                    ErrorMessage: errorMessage,
                    Outputs:      new Dictionary<string, string>(
                                      outputVariables, StringComparer.OrdinalIgnoreCase),
                    // B4: sensitivity rides into the registry so the online
                    // output merge can mask these in later waves' plans.
                    SensitiveOutputNames: sensitiveOutputNames));
        }

        var capturedAt = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // ONE lookup on the unified spine — output variables are now persisted for
        // runbook runs too (the pre-unification drop is fixed). Filter-free; stamp
        // the child rows' SpaceId from the loaded task.
        var scope = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == deploymentId)
            .Select(t => new { t.SpaceId })
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (scope is null)
        {
            logger.LogDebug(
                "ReportStepCompleted for unknown task {Id}; ignored.", deploymentId);
            return;
        }

        // Ownership: only a target assigned to this task may persist its outputs
        // or compact its logs — otherwise a foreign agent could inject outputs that
        // later steps consume.
        if (connectionTargetId is null
            || !await AgentDeploymentOwnership
                   .ConnectionOwnsTaskAsync(db, deploymentId, connectionTargetId.Value)
                   .ConfigureAwait(false))
        {
            logger.LogWarning(
                "ReportStepCompleted rejected: target {Target} is not assigned to task {Id}.",
                connectionTargetId, deploymentId);
            return;
        }

        // B4: single upsert path shared with the server-side capture fold
        // (DeploymentWorker / ServerScriptStepRunner) — same encryption rules.
        await TaskOutputVariableStore.UpsertAsync(
            db, deploymentId, scope.SpaceId, stepName,
            outputVariables, sensitiveOutputNames, capturedAt, encryption)
            .ConfigureAwait(false);

        // Step-completion compaction (decision 3): fold this (task, step, target)'s
        // staging log lines into a single blob. No-op when the step logged nothing.
        await TaskLogService.CompactStepAsync(
            db, deploymentId, stepIndex, connectionTargetId, capturedAt).ConfigureAwait(false);

        logger.LogInformation(
            "Step '{Step}' (index {Index}) of task {Id} completed: " +
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
        Guid taskId,
        ServerTaskKind kind,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var retention = scope.ServiceProvider
                .GetRequiredService<KrakenDeploy.Server.Data.Services.RetentionService>();
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
            logger.LogError(ex, "Error running retention pruning for task {Id} (kind {Kind}).", taskId, kind);
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
        Guid accountId,
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
                .PublishAsync(targetId, TargetStatus.Offline, target.LastSeenUtc, accountId)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled error in grace-period offline task for target {TargetId}.", targetId);
        }
    }
}
