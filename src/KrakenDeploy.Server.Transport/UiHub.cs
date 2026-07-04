using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// SignalR hub at <c>/hubs/ui</c> that pushes real-time events to browser clients.
/// Authenticated via the application cookie scheme; all pushes originate from the
/// server (via <see cref="IHubContext{UiHub, IUiHubClient}"/>) — no client-to-server
/// methods are defined.
/// <para>
/// Multi-account isolation: on connect each connection joins a per-account SignalR
/// group (<see cref="AccountGroup"/>), and server pushes target that group rather than
/// <c>Clients.All</c> — so a tenant's browser never receives another tenant's events
/// (all tenants share one process and one hub endpoint). The account is host-derived
/// (the browser connects to its account subdomain), resolved via <see cref="IAccountResolver"/>
/// exactly as the agent hub does. Single-instance resolves to <see cref="Guid.Empty"/>,
/// so every connection lands in the one group and behaviour is unchanged. SignalR
/// removes a connection from its groups automatically on disconnect.
/// </para>
/// </summary>
[Authorize]
public sealed class UiHub(IAccountResolver accountResolver) : Hub<IUiHubClient>
{
    /// <summary>
    /// SignalR group name carrying a single tenant's UI pushes, keyed by account id
    /// (<see cref="Guid.Empty"/> in single-instance mode). Server-side pushers use this
    /// so an event reaches only the account it belongs to.
    /// </summary>
    public static string AccountGroup(Guid accountId) => $"account:{accountId}";

    public override async Task OnConnectedAsync()
    {
        // Host-derived account (browser connects to its account subdomain). Guid.Empty
        // single-instance (NullAccountResolver returns null) or apex hosts — those land
        // in one shared group, which is correct for single-instance and harmless
        // otherwise (no tenant events are published to the empty group in multi-account).
        var host = Context.GetHttpContext()?.Request.Host.Host;
        var account = host is null
            ? null
            : await accountResolver.ResolveAsync(host, Context.ConnectionAborted).ConfigureAwait(false);

        await Groups
            .AddToGroupAsync(Context.ConnectionId, AccountGroup(account?.Id ?? Guid.Empty), Context.ConnectionAborted)
            .ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}
