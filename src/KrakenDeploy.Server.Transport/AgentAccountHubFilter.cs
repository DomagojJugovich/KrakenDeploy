using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Multi-account only. Gives the agent transport its <b>account identity</b> by
/// resolving the business account from the connection's host (host-derived — the
/// agent connects to its account subdomain, <c>&lt;sub&gt;.&lt;base&gt;</c>) and pinning it on
/// <see cref="IAccountContext"/> for the duration of every <see cref="AgentHub"/>
/// lifecycle event and method invocation. The tenant <c>KrakenDbContext</c> the hub
/// opens then binds to the right account database via <c>WithAccount</c>'s ambient
/// <c>AsyncLocal</c> (the same mechanism the per-account background jobs use).
/// <para>
/// <b>Fail closed.</b> A connection whose host does not resolve to an active account
/// is aborted before the hub runs; an invocation that arrives without a cached
/// account is aborted rather than allowed to touch a tenant DB with no account.
/// </para>
/// <para>
/// Registered as a per-hub filter on <see cref="AgentHub"/> <b>only</b> when
/// <c>Deployment:Topology</c> is <c>Saas</c> — single-tenant topologies never see it and
/// run unchanged. The resolved account is cached on the connection
/// (<see cref="HubCallerContext.Items"/>), so only the connect event hits the
/// resolver; later invocations re-apply the cached value. Scoped services are
/// resolved from each call's <c>ServiceProvider</c>, so the filter's own lifetime
/// does not matter.
/// </para>
/// </summary>
public sealed class AgentAccountHubFilter(ILogger<AgentAccountHubFilter> logger) : IHubFilter
{
    private const string AccountItemKey = "kd.agent.account";

    public async Task OnConnectedAsync(
        HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var account = await ResolveAsync(context.Context, context.ServiceProvider).ConfigureAwait(false);
        if (account is null)
        {
            logger.LogWarning(
                "Agent connection {ConnectionId} from host {Host} did not resolve to an active " +
                "account; aborting (fail closed).",
                context.Context.ConnectionId, context.Context.GetHttpContext()?.Request.Host.Host);
            context.Context.Abort();
            return;
        }

        context.Context.Items[AccountItemKey] = account;
        var accountContext = context.ServiceProvider.GetRequiredService<IAccountContext>();
        using (accountContext.WithAccount(account))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var account = GetCached(invocationContext.Context);
        if (account is null)
        {
            // OnConnectedAsync resolves and caches the account; a missing value here
            // means a method ran on a connection that never resolved one — fail closed
            // rather than open a tenant DbContext with no account.
            logger.LogWarning(
                "Agent invocation {Method} on connection {ConnectionId} has no resolved account; aborting.",
                invocationContext.HubMethodName, invocationContext.Context.ConnectionId);
            invocationContext.Context.Abort();
            return null;
        }

        var accountContext = invocationContext.ServiceProvider.GetRequiredService<IAccountContext>();
        using (accountContext.WithAccount(account))
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        // Wrap the hub's OnDisconnectedAsync in the account scope so the deferred
        // "mark offline after grace" task it starts captures the account in its
        // ExecutionContext (the WithAccount AsyncLocal) and writes Offline to the
        // correct tenant database 30 s later.
        var account = GetCached(context.Context);
        if (account is null)
        {
            await next(context, exception).ConfigureAwait(false);
            return;
        }

        var accountContext = context.ServiceProvider.GetRequiredService<IAccountContext>();
        using (accountContext.WithAccount(account))
        {
            await next(context, exception).ConfigureAwait(false);
        }
    }

    private static ResolvedAccount? GetCached(HubCallerContext context) =>
        context.Items.TryGetValue(AccountItemKey, out var value) ? value as ResolvedAccount : null;

    private static async Task<ResolvedAccount?> ResolveAsync(
        HubCallerContext context, IServiceProvider services)
    {
        var host = context.GetHttpContext()?.Request.Host.Host;
        if (host is null)
        {
            return null;
        }

        var resolver = services.GetRequiredService<IAccountResolver>();
        return await resolver.ResolveAsync(host, context.ConnectionAborted).ConfigureAwait(false);
    }
}
