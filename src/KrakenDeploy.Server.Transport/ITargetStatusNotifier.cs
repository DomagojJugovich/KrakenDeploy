using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// In-process event bus for target status changes.
/// Blazor Server components subscribe to <see cref="TargetStatusChanged"/> and
/// call <c>InvokeAsync(StateHasChanged)</c> to update the UI without a second
/// network round-trip.
/// </summary>
public interface ITargetStatusNotifier
{
    /// <summary>
    /// Raised when a target's online state changes.
    /// Handlers are called synchronously on the publishing thread; keep them short
    /// and dispatch any async work via <c>InvokeAsync</c>.
    /// </summary>
    event Action<Guid, TargetStatus, DateTimeOffset?> TargetStatusChanged;

    /// <summary>Publishes a status change to all current subscribers.</summary>
    void Publish(Guid targetId, TargetStatus status, DateTimeOffset? lastSeenUtc);
}
