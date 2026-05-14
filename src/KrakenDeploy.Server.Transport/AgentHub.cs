using System.Security.Claims;
using KrakenDeploy.Contracts;
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
        var target = await db.DeploymentTargets
            .FindAsync(new object?[] { targetId.Value })
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
        var target = await db.DeploymentTargets
            .FindAsync(new object?[] { targetId.Value })
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
        var target = await db.DeploymentTargets
            .FindAsync(new object?[] { targetId.Value })
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

        var timestamp = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Try Deployment first, then RunbookRun (same ID space, non-overlapping GUIDs).
        var deployment = await db.Deployments
            .FindAsync(new object?[] { deploymentId })
            .ConfigureAwait(false);

        if (deployment is not null)
        {
            var seq = deployment.NextLogSequence++;
            db.DeploymentLogEntries.Add(new KrakenDeploy.Server.Core.Domain.Deployments.DeploymentLogEntry
            {
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
            .FindAsync(new object?[] { deploymentId })
            .ConfigureAwait(false);

        if (run is not null)
        {
            var seq = run.NextLogSequence++;
            db.RunbookRunLogEntries.Add(new KrakenDeploy.Server.Core.Domain.Runbooks.RunbookRunLogEntry
            {
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
        // for a mixed-side process), resolve the pending TCS and stop here —
        // DeploymentWorker will finalize the deployment status once every
        // group has completed.
        if (subPlans.TryResolve(deploymentId, new SubPlanResult(success, errorMessage)))
        {
            logger.LogDebug(
                "Sub-plan complete for deployment {Id} (success={Success}); orchestrator will continue.",
                deploymentId, success);
            return;
        }

        var completedAt = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Try Deployment first.
        var deployment = await db.Deployments
            .FindAsync(new object?[] { deploymentId })
            .ConfigureAwait(false);

        if (deployment is not null)
        {
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
            .FindAsync(new object?[] { deploymentId })
            .ConfigureAwait(false);

        if (run is not null)
        {
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
    /// Persists output variables captured during a step via Set-OctopusVariable
    /// stdout markers. Upsert by (DeploymentId, StepName, Name) so a step that
    /// reassigns the same variable wins. Output variables are surfaced on the
    /// deployment detail page and are merged into subsequent steps' variables
    /// agent-side as <c>Octopus.Action[StepName].Output.X</c>.
    /// </summary>
    public async Task ReportStepOutputVariablesAsync(
        Guid deploymentId, string stepName, Dictionary<string, string> outputVariables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(outputVariables);
        if (outputVariables.Count == 0)
        {
            return;
        }

        var capturedAt = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var deploymentExists = await db.Deployments
            .AnyAsync(d => d.Id == deploymentId).ConfigureAwait(false);
        if (!deploymentExists)
        {
            // Runbook runs don't currently capture output variables — when they
            // do, route here using a parallel table. Until then, ignore.
            logger.LogDebug(
                "ReportStepOutputVariables for unknown deployment {Id}; ignored.", deploymentId);
            return;
        }

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
            "Captured {Count} output variable(s) for step '{Step}' of deployment {Id}.",
            outputVariables.Count, stepName, deploymentId);
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

            var target = await db.DeploymentTargets
                .FindAsync(new object?[] { targetId })
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
