using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// Resolves the active Space for an HTTP request: reads the
/// <c>kraken-active-space</c> cookie, validates it against the caller's
/// accessible Spaces (<see cref="IPermissionEvaluator.GetAccessibleSpaceIdsAsync"/>),
/// and stamps the validated id into <c>HttpContext.Items</c> for
/// <see cref="HttpSpaceContext"/> to read. This is the async home for the cookie
/// validation that the synchronous <see cref="HttpSpaceContext.CurrentSpaceId"/>
/// property cannot do itself.
/// <para>
/// Only runs for requests that actually consume the Space context — document
/// navigations (the prerender, <c>Accept: text/html</c>) and the <c>/api</c>
/// surface — so static assets, the Blazor framework files, and the SignalR
/// negotiate don't pay a DB round-trip. Must be registered after
/// <c>UseAuthorization</c> (needs <c>HttpContext.User</c>) and before any
/// Space-aware middleware or endpoint.
/// </para>
/// </summary>
public sealed class ActiveSpaceResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IPermissionEvaluator perms)
    {
        if (ShouldResolve(context))
        {
            var cookie =
                context.Request.Cookies.TryGetValue(HttpSpaceContext.ActiveSpaceCookieName, out var raw)
                && Guid.TryParse(raw, out var parsed)
                    ? parsed
                    : (Guid?)null;

            var accessible = await perms
                .GetAccessibleSpaceIdsAsync(context.User, context.RequestAborted)
                .ConfigureAwait(false);

            var resolved = ActiveSpaceResolver.Resolve(cookie, accessible, WellKnown.DefaultSpaceId);
            context.Items[HttpSpaceContext.ResolvedSpaceItemKey] = resolved;

            // Self-heal a stale / inaccessible cookie so the switcher and later
            // requests agree on the active Space. Never write the fail-closed
            // sentinel (Guid.Empty) back as a cookie.
            if (resolved != Guid.Empty && cookie != resolved)
            {
                context.Response.Cookies.Append(
                    HttpSpaceContext.ActiveSpaceCookieName,
                    resolved.ToString(),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure   = context.Request.IsHttps,
                        SameSite = SameSiteMode.Lax,
                        Path     = "/",
                        Expires  = DateTimeOffset.UtcNow.AddDays(365),
                    });
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool ShouldResolve(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        // The API surface always reads the ambient Space.
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            return true;
        }

        // Document navigations (page prerender) ask for HTML; static assets,
        // _framework, and the /_blazor negotiate don't.
        foreach (var value in context.Request.Headers.Accept)
        {
            if (value is not null && value.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
