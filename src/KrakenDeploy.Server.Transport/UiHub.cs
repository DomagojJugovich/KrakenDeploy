using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// SignalR hub at <c>/hubs/ui</c> that pushes real-time events to browser clients.
/// Authenticated via the application cookie scheme; all pushes originate from the
/// server (via <see cref="IHubContext{UiHub, IUiHubClient}"/>) — no client-to-server
/// methods are defined for M1.
/// </summary>
[Authorize]
public sealed class UiHub : Hub<IUiHubClient>
{
    // Stateless — all pushes originate from TargetStatusPublisher via IHubContext.
}
