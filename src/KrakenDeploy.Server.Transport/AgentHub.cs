using System.Security.Claims;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    KrakenDbContext db,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
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
            _ = MarkOfflineAfterGraceAsync(targetId, scopeFactory, registry, timeProvider, logger);
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

    public Task AppendLogAsync(Guid deploymentId, string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        // M1 stub — full log streaming lands in M2.
        logger.LogDebug(
            "Log chunk ({Bytes} bytes) from deployment {DeploymentId}.",
            chunk.Length, deploymentId);
        return Task.CompletedTask;
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
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled error in grace-period offline task for target {TargetId}.", targetId);
        }
    }
}
