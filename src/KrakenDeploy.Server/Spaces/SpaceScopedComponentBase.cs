using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// Base class for every Space-scoped page. Each page's route carries the active
/// Space as its first segment (<c>/s/{SpaceSlug}/…</c>), so the slug is a route
/// parameter that BOTH the static prerender and the interactive
/// <c>&lt;Router&gt;</c> re-parse from the browser URL — the Space rides in the URL
/// (per-tab, refresh-stable, bookmarkable) with no cookie and no
/// <c>PersistentComponentState</c> hand-off.
/// <para>
/// We resolve + validate the slug in <see cref="SetParametersAsync"/>, BEFORE the
/// component lifecycle runs, so the ambient <see cref="ISpaceContext"/> is set
/// before the page's <c>OnInitializedAsync</c> issues any Space-scoped query.
/// (The default lifecycle order is <c>OnInitialized</c> → <c>OnParametersSet</c>,
/// so resolving in <c>OnParametersSet</c> would be too late.)
/// </para>
/// <para>
/// Validation is the hard tenant boundary: a slug the caller can't access (or an
/// unknown/inactive Space) never sets the context — the user is bounced to a Space
/// they can use. The <see cref="SpaceUrlRedirectMiddleware"/> guards the initial
/// HTTP entry; this guards interactive URL edits where no HTTP request occurs.
/// </para>
/// </summary>
public abstract class SpaceScopedComponentBase : ComponentBase
{
    /// <summary>The active Space slug, bound from the <c>/s/{SpaceSlug}/…</c> route segment.</summary>
    [Parameter] public string? SpaceSlug { get; set; }

    // All injected dependencies are private so a page that injects its own member
    // of the same name (e.g. `@inject ISpaceContext SpaceCtx`) doesn't trip CS0108
    // by hiding an inherited member — private base members aren't inherited-visible.
    [Inject] private HttpSpaceContext SpaceCtx { get; set; } = default!;
    [Inject] private SpaceService Spaces { get; set; } = default!;
    [Inject] private IPermissionEvaluator Perms { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthProvider { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private bool _redirecting;

    /// <summary>
    /// Builds a Space-prefixed app URL for the page's active Space:
    /// <c>Sp("/projects")</c> → <c>/s/{SpaceSlug}/projects</c>. Use for every
    /// in-app link / <c>NavigateTo</c> so navigation stays inside this tab's Space
    /// (the URL carries it). Pass the existing unprefixed app path.
    /// </summary>
    protected string Sp(string relativePath) =>
        SpaceRouting.BuildPath(SpaceSlug ?? WellKnown.DefaultSpaceSlug, relativePath);

    // NOTE: no .ConfigureAwait(false) anywhere in this component. Lifecycle awaits
    // must resume on the renderer's SynchronizationContext (the Dispatcher);
    // resuming on a threadpool thread makes the follow-up render throw
    // "The current thread is not associated with the Dispatcher."
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        // Assign route/component parameters WITHOUT triggering the lifecycle yet…
        parameters.SetParameterProperties(this);

        // …resolve the ambient Space first…
        await ResolveSpaceAsync();

        // …then run the normal lifecycle (OnInitialized → OnParametersSet) with the
        // Space already in place. Skip rendering entirely if we issued a redirect.
        if (!_redirecting)
        {
            await base.SetParametersAsync(ParameterView.Empty);
        }
    }

    private async Task ResolveSpaceAsync()
    {
        // Prefixed routes always carry the slug; a missing one (unreachable in
        // practice) is treated as a request for the Default Space — and still
        // validated below, so it never grants Default access without a real grant.
        var slug = string.IsNullOrEmpty(SpaceSlug) ? WellKnown.DefaultSpaceSlug : SpaceSlug;

        // Already resolved to this slug on this circuit → reuse it; navigating
        // between pages of the same Space must not re-hit the DB every click.
        if (SpaceCtx.IsResolvedTo(slug))
        {
            return;
        }

        var space = await Spaces.GetBySlugAsync(slug);
        System.Security.Claims.ClaimsPrincipal? user = null;
        IReadOnlySet<Guid>? accessible = null;
        if (space is not null && space.Status == SpaceStatus.Active)
        {
            user = (await AuthProvider.GetAuthenticationStateAsync()).User;
            accessible = await Perms.GetAccessibleSpaceIdsAsync(user);
            if (accessible.Contains(space.Id))
            {
                SpaceCtx.SetResolved(space.Id, space.Slug);
                return;
            }
        }

        // Unknown / inactive / inaccessible slug → bounce to a Space the user can
        // actually use. forceLoad so the request re-runs and a clean circuit
        // resolves the new Space; the URL never lies about the rendered Space.
        // Reuse the already-resolved auth state + accessible set so the known-but-
        // inaccessible path doesn't recompute the (non-trivial) permission query.
        await RedirectToUsableSpaceAsync(slug, user, accessible);
    }

    private async Task RedirectToUsableSpaceAsync(
        string requestedSlug,
        System.Security.Claims.ClaimsPrincipal? user = null,
        IReadOnlySet<Guid>? accessible = null)
    {
        user ??= (await AuthProvider.GetAuthenticationStateAsync()).User;
        accessible ??= await Perms.GetAccessibleSpaceIdsAsync(user);
        var fallback = await SpaceResolution.ResolveAccessibleSlugAsync(Spaces, accessible);

        if (fallback is null)
        {
            // The user can reach NO Space — fail CLOSED. Guid.Empty matches no
            // SpaceId in the global query filter (real Spaces have non-empty ids),
            // so every Space-scoped query returns empty instead of leaking the
            // Default Space's data to a user with no grant. Pages render their
            // empty/forbidden state. (NOT WellKnown.DefaultSpaceId — that would
            // serve Default's rows to a fully-deprovisioned user.)
            SpaceCtx.SetResolved(Guid.Empty, requestedSlug);
            return;
        }

        _redirecting = true;
        Nav.NavigateTo(SpaceRouting.BuildPath(fallback, "/"), forceLoad: true);
    }
}
