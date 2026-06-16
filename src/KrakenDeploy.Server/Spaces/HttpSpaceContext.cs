using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;

namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// HTTP / circuit-aware <see cref="ISpaceContext"/> used by the Blazor server and
/// the minimal-API endpoints. Replaces <c>DefaultSpaceContext</c> (the fallback
/// that just returns <see cref="WellKnown.DefaultSpaceId"/>) when the HTTP
/// pipeline is available.
/// <para>
/// Resolution order (first match wins):
/// </para>
/// <list type="number">
///   <item>Explicit override pushed via <see cref="WithSpace"/> — used by
///         background workers, system-admin operations, and tests.</item>
///   <item>The resolved Space set via <see cref="SetResolved"/> — pushed by
///         <see cref="SpaceScopedComponentBase"/> from the <c>/s/{SpaceSlug}/…</c>
///         route param after validating it against the user's accessible Spaces,
///         on both the prerender (request scope) and the interactive circuit.</item>
///   <item><see cref="WellKnown.DefaultSpaceId"/> — for requests that never run a
///         Space-scoped page: the <c>/api</c> surface (CLI/agents are
///         Default-scoped) and unauthenticated/static requests.</item>
/// </list>
/// <para>
/// The URL slug is deliberately NOT trusted without the async accessibility check,
/// which lives in the page base (and the bare-path
/// <see cref="SpaceUrlRedirectMiddleware"/>) and flows in via
/// <see cref="SetResolved"/>.
/// </para>
/// </summary>
public sealed class HttpSpaceContext : ISpaceContext
{
    private readonly Stack<Guid> _overrides = new();

    // Scope-local memo. On the interactive circuit (and within a request scope) the
    // page base resolves + validates the Space once and pushes it here; subsequent
    // reads in the same scope reuse it.
    private Guid? _resolved;
    private string? _resolvedSlug;

    public Guid CurrentSpaceId
    {
        get
        {
            // 1. Explicit override (workers, tests, admin operations).
            if (_overrides.Count > 0)
            {
                return _overrides.Peek();
            }

            // 2. Resolved for this scope (page base, prerender or circuit).
            if (_resolved is { } memo)
            {
                return memo;
            }

            // 3. Fallback for requests that don't run a Space-scoped page
            //    (/api, static, unauthenticated).
            return WellKnown.DefaultSpaceId;
        }
    }

    public string CurrentSpaceSlug => _resolvedSlug ?? WellKnown.DefaultSpaceSlug;

    /// <summary>
    /// Sets the active Space (id + slug) for the current scope. Called by
    /// <see cref="SpaceScopedComponentBase"/> after re-validating the URL slug
    /// against the user's accessible Spaces — the URL is client-controlled, so it
    /// is only honoured once it survives that check.
    /// </summary>
    public void SetResolved(Guid spaceId, string slug)
    {
        _resolved = spaceId;
        _resolvedSlug = slug;
    }

    /// <summary>
    /// True when the Space has already been resolved to <paramref name="slug"/> on
    /// this scope. Lets the page base skip the validation round-trip when
    /// navigating between pages of the same Space.
    /// </summary>
    public bool IsResolvedTo(string slug) =>
        _resolved is not null && string.Equals(_resolvedSlug, slug, StringComparison.Ordinal);

    public IDisposable WithSpace(Guid spaceId)
    {
        _overrides.Push(spaceId);
        return new PopOnDispose(_overrides);
    }

    private sealed class PopOnDispose(Stack<Guid> stack) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (stack.Count > 0)
            {
                stack.Pop();
            }
        }
    }
}
