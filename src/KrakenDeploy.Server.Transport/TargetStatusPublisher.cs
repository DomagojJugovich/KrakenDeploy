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
    /// <param name="accountId">
    /// The account the target belongs to (<see cref="Guid.Empty"/> single-instance). The
    /// external push is scoped to this account's SignalR group so no other tenant's
    /// browsers receive it. In-process notification is already tenant-safe (subscribers
    /// filter by their own loaded target list).
    /// </param>
    public async Task PublishAsync(
        Guid targetId,
        TargetStatus status,
        DateTimeOffset? lastSeenUtc,
        Guid accountId)
    {
        // ── In-process (Blazor Server circuits) ──────────────────────────
        notifier.Publish(targetId, status, lastSeenUtc);

        // ── External SignalR clients (browser WASM, webhooks, etc.) ──────
        // Push to the target's account group only — NOT Clients.All, which in
        // multi-account leaks one tenant's target existence/status to every other
        // tenant's browsers (all share one hub endpoint). Single-instance: accountId is
        // Guid.Empty and every UI connection is in that one group, so this reaches all
        // clients exactly as before.
        try
        {
            await uiHub.Clients.Group(UiHub.AccountGroup(accountId))
                .TargetStatusChangedAsync(targetId, status.ToString(), lastSeenUtc)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to push TargetStatusChanged to UI hub clients " +
                "(targetId={TargetId}, status={Status}, account={AccountId}).",
                targetId, status, accountId);
        }
    }
}
