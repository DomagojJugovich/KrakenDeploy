using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// B6 — <see cref="IAgentCancelPusher"/> over the agent hub: resolves the
/// task's target set, finds each target's live connection and pushes
/// <c>CancelDeploymentAsync</c>. Targets without a live connection are simply
/// skipped (offline agents fall back to wave-boundary semantics). Never throws
/// — the DB verdict is already recorded; this only accelerates the stop.
/// <para>
/// Singleton over <see cref="IServiceScopeFactory"/>, NOT over the context
/// factory: <c>IDbContextFactory</c> is SCOPED in this app (multi-account
/// routing resolves the tenant database per scope), so capturing it here would
/// be the exact captive-dependency cascade Dev's ValidateOnBuild refuses. The
/// per-push scope also rides the caller's ambient account (AsyncLocal), so the
/// target lookup reads the right tenant database.
/// </para>
/// </summary>
public sealed class AgentCancelPusher(
    IServiceScopeFactory scopeFactory,
    IAgentConnectionRegistry registry,
    IHubContext<AgentHub, IAgentHubClient> hub,
    ILogger<AgentCancelPusher> logger) : IAgentCancelPusher
{
    public async Task PushCancelAsync(Guid taskId, string? reason, CancellationToken ct = default)
    {
        try
        {
            List<Guid> targetIds;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var dbFactory = scope.ServiceProvider
                    .GetRequiredService<IDbContextFactory<KrakenDbContext>>();
                await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                // Filter-free: the caller's Space scoping already authorized the
                // cancel; the push must reach the targets regardless of ambient
                // Space. Target ids are globally unique, so the connection lookup
                // cannot cross accounts.
                targetIds = await db.TaskTargetAssignments
                    .IgnoreQueryFilters()
                    .Where(a => a.TaskId == taskId)
                    .Select(a => a.TargetId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }

            var pushed = 0;
            foreach (var targetId in targetIds)
            {
                if (registry.GetConnectionId(targetId) is not { } connectionId)
                {
                    continue;
                }
                await hub.Clients.Client(connectionId)
                    .CancelDeploymentAsync(taskId, reason)
                    .ConfigureAwait(false);
                pushed++;
            }

            logger.LogInformation(
                "Cancel push for task {TaskId}: notified {Pushed}/{Total} target connection(s).",
                taskId, pushed, targetIds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Cancel push for task {TaskId} failed; the recorded Cancelled verdict stands " +
                "and the task stops at the next wave boundary instead.", taskId);
        }
    }
}
