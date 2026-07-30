using System.Text;
using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.ControlPlane.Provisioning;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Accounts;

/// <summary>
/// Resolves the active business account from the request host subdomain and pins it
/// onto <see cref="IAccountContext"/> (via <c>HttpContext.Items</c>, read back by
/// <c>HttpAccountContext</c>). This is the cross-customer authorization boundary and
/// <b>fails closed</b>:
/// <list type="bullet">
///   <item>A tenant subdomain mapping to an active account → set the account, continue.</item>
///   <item>An apex / control-plane host (no tenant subdomain) → serve the control-plane
///         landing for navigational requests (no tenant DB), else pass through.</item>
///   <item>A tenant subdomain that is unknown or not active → <c>404</c>, never a default DB.</item>
/// </list>
/// Must run AFTER authentication/authorization-independent host resolution and BEFORE any
/// tenant <c>DbContext</c> is built (registered before <c>UseAuthentication</c>, because
/// Identity loads the per-account user from the tenant DB).
/// </summary>
public sealed class AccountResolutionMiddleware(
    RequestDelegate next,
    ILogger<AccountResolutionMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IAccountResolver resolver,
        IAccountContext accountContext,
        ICatalogStore catalog,
        IOptions<MultiAccountOptions> options)
    {
        var host = context.Request.Host.Host;

        var resolved = await resolver.ResolveAsync(host, context.RequestAborted)
            .ConfigureAwait(false);

        if (resolved is not null)
        {
            // Stash on HttpContext.Items so every DI scope in this request sees it —
            // Blazor SSR renders components across multiple scopes, so a value set on
            // one scoped IAccountContext instance is invisible to the others.
            context.Items[HttpAccountContext.ItemsKey] = resolved;
            accountContext.SetResolved(resolved);
            await next(context).ConfigureAwait(false);
            return;
        }

        // No tenant account resolved.

        // Agent transport is tenant-scoped and host-derived: an agent MUST connect to its
        // account subdomain, so a hub / gRPC / enrollment request arriving with no resolved
        // account targeted the apex or a host outside the account model. Fail closed at the
        // boundary rather than pass it through to the AgentAccountHubFilter (a second gate,
        // hub-only) or — for gRPC + enrollment, which have NO transport backstop — let it
        // fault at the tenant DbContext. This makes the middleware a complete boundary for
        // the agent surface. The platform-global binary download (/api/agents/download) is
        // intentionally excluded — it serves shared binaries and touches no tenant DB.
        if (IsTenantScopedAgentPath(context.Request.Path))
        {
            logger.LogWarning(
                "Rejected agent request {Path} on host {Host} with no resolved account — " +
                "agents must target their account subdomain.", context.Request.Path, host);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Unknown account.").ConfigureAwait(false);
            return;
        }

        if (HostParser.ExtractSubdomain(host, options.Value.BaseDomain) is null)
        {
            // Apex / control-plane host (no tenant subdomain). Serve the control-plane
            // landing for navigational (HTML GET) requests — it touches no tenant DB —
            // and pass everything else (assets, framework, API) through.
            if (IsNavigational(context.Request))
            {
                await WriteControlPlaneLandingAsync(context, catalog, options.Value.BaseDomain)
                    .ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        // A tenant subdomain that does not map to an active account → fail closed.
        logger.LogWarning(
            "Rejected request for unknown or inactive account subdomain {Host}", host);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Unknown account.").ConfigureAwait(false);
    }

    /// <summary>
    /// Tenant-scoped agent-transport paths whose account is host-derived: the SignalR
    /// agent hub, the gRPC delivery services (proto package <c>krakendeploy.v1</c>),
    /// anonymous agent enrollment, and agent auto-update <c>update-info</c> (which reads
    /// the per-target <c>AutoUpdateEnabled</c> flag from the tenant DB, so it too needs a
    /// resolved account), agent auto-update <c>task-in-flight</c> (F5 — it queries the
    /// tenant DB for non-terminal tasks assigned to the calling target) and agent
    /// auto-update <c>update-status</c> (F5 — it reads the calling target and WRITES an
    /// audit row, so an unresolved account would file it against the wrong database).
    /// Deliberately EXCLUDES the platform-global binary download
    /// (<c>/api/agents/download</c>), which serves shared binaries and touches no tenant
    /// DB. New gRPC packages / tenant-scoped agent routes must be reviewed here.
    /// </summary>
    private static bool IsTenantScopedAgentPath(PathString path) =>
        path.StartsWithSegments("/hubs/agent", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/agents/register", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/agents/update-info", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/agents/task-in-flight", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/agents/update-status", StringComparison.OrdinalIgnoreCase)
        || (path.HasValue
            && path.Value.StartsWith("/krakendeploy.v1.", StringComparison.OrdinalIgnoreCase));

    private static bool IsNavigational(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Minimal control-plane landing for the apex host: lists active accounts with a
    /// link to each one's sign-in. No tenant DB is touched (catalog only). A real
    /// signup/admin portal (Phase 4) replaces this.
    /// </summary>
    private static async Task WriteControlPlaneLandingAsync(
        HttpContext context, ICatalogStore catalog, string baseDomain)
    {
        var accounts = await catalog.ListAsync(AccountStatus.Active, context.RequestAborted)
            .ConfigureAwait(false);

        var port = context.Request.Host.Port;
        var portSuffix = port is null ? string.Empty : $":{port}";
        var scheme = context.Request.Scheme;

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>KrakenDeploy — Control Plane</title><style>");
        sb.Append("body{font-family:system-ui,Segoe UI,sans-serif;max-width:640px;margin:64px auto;padding:0 24px;color:#16242f}");
        sb.Append("h1{font-size:1.4rem;margin:0 0 .25rem}p{color:#5b6b78}ul{list-style:none;padding:0}");
        sb.Append("li{margin:.5rem 0;padding:.75rem 1rem;border:1px solid #e2e8ee;border-radius:8px}");
        sb.Append("a{color:#0a7d63;text-decoration:none;font-weight:600}code{color:#5b6b78}");
        sb.Append("</style></head><body>");
        sb.Append("<h1>KrakenDeploy</h1><p>Platform control plane. Sign in to a business account:</p><ul>");
        foreach (var account in accounts)
        {
            var url = $"{scheme}://{account.Subdomain}.{baseDomain}{portSuffix}/login";
            sb.Append("<li><a href=\"").Append(url).Append("\">")
              .Append(System.Net.WebUtility.HtmlEncode(account.DisplayName)).Append("</a> &middot; <code>")
              .Append(account.Subdomain).Append('.').Append(baseDomain).Append(portSuffix).Append("</code></li>");
        }

        if (accounts.Count == 0)
        {
            sb.Append("<li><em>No active accounts yet.</em></li>");
        }

        sb.Append("</ul></body></html>");

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(sb.ToString(), context.RequestAborted).ConfigureAwait(false);
    }
}
