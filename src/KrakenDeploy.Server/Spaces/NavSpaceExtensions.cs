using KrakenDeploy.Server.Core.Domain.Common;
using Microsoft.AspNetCore.Components;

namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// Space-URL helpers for shared chrome / child components (NavMenu, layout,
/// breadcrumbs, dialogs) that are NOT a routed page.
/// <para>
/// These derive the active Space slug from the <b>URL</b> (the authoritative
/// carrier), not from <see cref="ISpaceContext"/>: shared chrome renders before
/// the routed page's <see cref="SpaceScopedComponentBase"/> sets the ambient
/// Space, so reading the context there would race and yield a stale/Default slug.
/// Routed pages should instead use the base class's <c>Sp(...)</c> (backed by the
/// reliable <c>SpaceSlug</c> route param).
/// </para>
/// </summary>
public static class NavSpaceExtensions
{
    /// <summary>
    /// The active Space slug parsed from the current URL's <c>/s/{slug}/…</c>
    /// prefix, or <see cref="WellKnown.DefaultSpaceSlug"/> if the URL carries none.
    /// </summary>
    public static string CurrentSpaceSlug(this NavigationManager nav)
    {
        ArgumentNullException.ThrowIfNull(nav);
        var relative = "/" + nav.ToBaseRelativePath(nav.Uri).Split('?', '#')[0];
        return SpaceRouting.Split(relative).Slug ?? WellKnown.DefaultSpaceSlug;
    }

    /// <summary>
    /// Builds a Space-prefixed app URL for the current URL's Space:
    /// <c>Sp("/projects")</c> → <c>/s/{currentSlug}/projects</c>. Use for every
    /// in-app link / <c>NavigateTo</c> in shared chrome so navigation stays inside
    /// this tab's Space.
    /// </summary>
    public static string Sp(this NavigationManager nav, string relativePath) =>
        SpaceRouting.BuildPath(nav.CurrentSpaceSlug(), relativePath);
}
