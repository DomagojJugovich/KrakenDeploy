using System.Net;
using System.Security.Claims;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Http;

namespace KrakenDeploy.Server.Maintenance;

/// <summary>
/// ASP.NET Core middleware that gates write requests when instance-wide
/// maintenance mode is on. Implementation contract:
///
/// <list type="number">
///   <item>Idle when <see cref="MaintenanceModeService.GetStateAsync"/>
///         returns <c>Enabled=false</c> — adds at most one cached lookup
///         per request.</item>
///   <item>GET / HEAD / OPTIONS always pass — the operator still needs
///         to read the page that turns maintenance off.</item>
///   <item><see cref="ExemptPathPrefixes"/> always pass — login,
///         logout, Blazor SignalR, agent transport, healthz,
///         diagnostics, hangfire dashboard. Without these, the gate
///         would lock everyone out (operator can't sign in to turn it
///         off; agents go offline; monitoring goes red).</item>
///   <item>Authenticated callers with <see cref="Permission.BypassMaintenance"/>
///         pass — the delegated-admin path so the operator running the
///         maintenance work isn't gated by their own switch.</item>
///   <item>Everything else gets <c>503 Service Unavailable</c> with a
///         JSON body that surfaces the operator-supplied
///         <see cref="MaintenanceState.Reason"/>.</item>
/// </list>
/// </summary>
public sealed class MaintenanceMiddleware(RequestDelegate next)
{
    /// <summary>Path prefixes that bypass the gate regardless of method
    /// or auth. Match is case-insensitive + prefix-based (so
    /// <c>/api/agents/foo/bar</c> matches <c>/api/agents</c>).</summary>
    internal static readonly string[] ExemptPathPrefixes =
    [
        "/login",                  // sign-in flow must stay reachable
        "/logout",                 // and sign-out
        "/_blazor",                // SignalR transport for Blazor server
        "/_framework",             // Blazor framework assets
        "/_content",               // Razor component-library assets
        "/api/agents",             // agent bootstrap: register / update-info / download
        "/hubs/agent",             // agent <-> server SignalR transport (the live link)
        "/healthz",                // monitoring keeps watching
        "/api/diagnostics",        // ops can still pull diagnostics zip
        "/hangfire",               // job dashboard for the operator
        "/configuration/maintenance", // the page that turns it off (Razor)
        "/api/maintenance",        // its API counterpart
    ];

    public async Task InvokeAsync(
        HttpContext context,
        MaintenanceModeService maintenanceService,
        IPermissionEvaluator permissionEvaluator)
    {
        // Cheap method check first — most requests are GETs, no point
        // hitting the cache for them.
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) ||
            HttpMethods.IsHead(method) ||
            HttpMethods.IsOptions(method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Exempt-path check before the maintenance lookup — cuts the
        // common cases (login POST, Blazor SignalR ping, agent
        // heartbeat) without touching the DB.
        var path = context.Request.Path.Value ?? string.Empty;
        if (IsExemptPath(path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var state = await maintenanceService.GetStateAsync(context.RequestAborted)
            .ConfigureAwait(false);
        if (!state.Enabled)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Maintenance is on — does the caller have a bypass?
        var allowed = await CallerHasBypassAsync(context, permissionEvaluator)
            .ConfigureAwait(false);
        if (allowed)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Block.
        context.Response.StatusCode  = (int)HttpStatusCode.ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";
        // Retry-After hint — clients with backoff can use this; we don't
        // know how long the window will be, so a generous default. The
        // body is the operator-actionable message.
        context.Response.Headers.RetryAfter = "300";
        var body = new
        {
            error  = "Instance is under maintenance.",
            reason = state.Reason ?? "No reason supplied.",
            since  = state.EnabledUtc,
        };
        await JsonSerializer.SerializeAsync(context.Response.Body, body,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    internal static bool IsExemptPath(string path)
    {
        if (ExemptPathPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Space-scoped pages ride at /s/{slug}/… — the bare exempt prefixes
        // (e.g. /configuration/maintenance) would otherwise never match the
        // real /s/acme/configuration/maintenance path, so the page that turns
        // maintenance OFF wouldn't be exempt. Strip the /s/{slug} prefix and
        // re-test. (Harmless if it exempts a non-existent /s/{slug}/login-style
        // path — that just 404s; maintenance mode is a write-guard, not auth.)
        var stripped = StripSpacePrefix(path);
        return stripped is not null
            && ExemptPathPrefixes.Any(p => stripped.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"/s/{slug}/rest" → "/rest"; null when there is no Space prefix.</summary>
    private static string? StripSpacePrefix(string path)
    {
        if (!path.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var slashAfterSlug = path.IndexOf('/', 3);
        return slashAfterSlug < 0 ? null : path[slashAfterSlug..];
    }

    private static async Task<bool> CallerHasBypassAsync(
        HttpContext context, IPermissionEvaluator evaluator)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true) { return false; }

        // System-wide bypass — no Space scope. Evaluator's AdministerSystem
        // implication makes sys-admins automatically pass.
        return await evaluator.HasPermissionAsync(
            user, Permission.BypassMaintenance, new PermissionScope())
            .ConfigureAwait(false);
    }
}

/// <summary>Extension for <c>app.UseMaintenanceMode()</c> at the host
/// composition root.</summary>
public static class MaintenanceMiddlewareExtensions
{
    public static IApplicationBuilder UseMaintenanceMode(this IApplicationBuilder app)
        => app.UseMiddleware<MaintenanceMiddleware>();
}
