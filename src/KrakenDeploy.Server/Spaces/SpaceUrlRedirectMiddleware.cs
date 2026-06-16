using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// Normalizes app entry URLs to carry the active Space. The active Space lives in
/// the URL path (<c>/s/{slug}/…</c>) as a real <c>@page</c> route param, so a
/// request that already has the prefix routes straight through to the page.
/// <para>
/// A <b>bare</b> page path (no <c>/s/{slug}</c> — a clean entry URL, an old
/// bookmark, or a post-login returnUrl) is 302-redirected to the <b>Default</b>
/// Space, preserving the target page + query string. There is deliberately NO
/// cookie and NO "last-used Space": an explicitly chosen Space is carried only by
/// the URL (and the circuit it spawns); a clean URL always lands on the Default
/// Space. Skips the API/framework/auth/static surface
/// (<see cref="SpaceRouting.IsSpaceAgnostic"/>).
/// </para>
/// </summary>
public sealed class SpaceUrlRedirectMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!SpaceRouting.IsSpaceAgnostic(path))
        {
            var (slug, _) = SpaceRouting.Split(path.Value!);
            if (slug is null)
            {
                // No Space in the URL → send to the Default Space, keeping the page.
                context.Response.Redirect(
                    SpaceRouting.BuildPath(WellKnown.DefaultSpaceSlug, path.Value!)
                    + context.Request.QueryString);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
