using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Singleton service that publishes target status changes through two channels:
/// <list type="bullet">
///   <item>The in-process <see cref="ITargetStatusNotifier"/> for Blazor Server components.</item>
///   <item><see cref="IHubContext{UiHub, IUiHubClient}"/> for external SignalR subscribers.</item>
/// </list>
/// Callers inject this and call <see cref="PublishAsync"/> — no need to know which
/// channels are active.
/// </summary>
public sealed class TargetStatusPublisher(
    ITargetStatusNotifier notifier,
    IHubContext<UiHub, IUiHubClient> uiHub,
    ILogger<TargetStatusPublisher> logger)
{
    public async Task PublishAsync(
        Guid targetId,
        TargetStatus status,
        DateTimeOffset? lastSeenUtc)
    {
        // ── In-process (Blazor Server circuits) ──────────────────────────
        notifier.Publish(targetId, status, lastSeenUtc);

        // ── External SignalR clients (browser WASM, webhooks, etc.) ──────
        try
        {
            await uiHub.Clients.All
                .TargetStatusChangedAsync(targetId, status.ToString(), lastSeenUtc)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to push TargetStatusChanged to UI hub clients " +
                "(targetId={TargetId}, status={Status}).", targetId, status);
        }
    }
}
