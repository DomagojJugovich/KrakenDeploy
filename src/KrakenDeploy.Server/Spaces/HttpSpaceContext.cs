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
///   <item>The circuit-resolved Space set via <see cref="SetResolved"/> — used by
///         <c>SpaceContextBoundary</c> on the interactive circuit, where
///         <see cref="IHttpContextAccessor.HttpContext"/> (and thus the cookie)
///         is unavailable.</item>
///   <item><c>HttpContext.Items[ResolvedSpaceItemKey]</c> — the
///         <c>ActiveSpaceResolutionMiddleware</c> validated the
///         <c>kraken-active-space</c> cookie against the user's accessible Spaces
///         and stored the result here for the duration of the HTTP request
///         (prerender + minimal-API).</item>
///   <item><see cref="WellKnown.DefaultSpaceId"/> — only for requests the
///         resolution middleware doesn't touch (static assets, unauthenticated),
///         none of which read Space-scoped data.</item>
/// </list>
/// <para>
/// The raw cookie is deliberately NOT read here: it is never trusted without the
/// async accessibility check, which lives in the middleware / boundary and
/// flows in via <c>Items</c> / <see cref="SetResolved"/>.
/// </para>
/// </summary>
public sealed class HttpSpaceContext(IHttpContextAccessor httpContextAccessor) : ISpaceContext
{
    /// <summary>Cookie name carrying the user's currently-selected Space ID.</summary>
    public const string ActiveSpaceCookieName = "kraken-active-space";

    /// <summary>
    /// <c>HttpContext.Items</c> key under which <c>ActiveSpaceResolutionMiddleware</c>
    /// stores the validated active Space id for the current request.
    /// </summary>
    public const string ResolvedSpaceItemKey = "kraken-resolved-space";

    private readonly Stack<Guid> _overrides = new();

    // Circuit-scoped memo. On the interactive circuit HttpContext is null, so the
    // boundary resolves+validates the carried Space once and pushes it here;
    // subsequent reads in the same circuit reuse it. In a request scope it caches
    // the Items lookup.
    private Guid? _resolved;

    public Guid CurrentSpaceId
    {
        get
        {
            // 1. Explicit override (workers, tests, admin operations).
            if (_overrides.Count > 0)
            {
                return _overrides.Peek();
            }

            // 2. Already resolved for this scope (circuit boundary, or a prior
            //    Items read in this request).
            if (_resolved is { } memo)
            {
                return memo;
            }

            // 3. Validated active Space stamped by the resolution middleware.
            var ctx = httpContextAccessor.HttpContext;
            if (ctx is not null
                && ctx.Items.TryGetValue(ResolvedSpaceItemKey, out var item)
                && item is Guid fromItems)
            {
                _resolved = fromItems;
                return fromItems;
            }

            // 4. Fallback for requests the middleware doesn't process (static
            //    assets, unauthenticated) — none of which read Space-scoped data.
            //    The interactive circuit reaches its validated Space via
            //    SetResolved before any Space-scoped query runs.
            return WellKnown.DefaultSpaceId;
        }
    }

    /// <summary>
    /// Sets the active Space for the current circuit. Called once by
    /// <c>SpaceContextBoundary</c> after re-validating the carried candidate
    /// against the user's accessible Spaces — the persisted circuit hint is
    /// client-tamperable, so it is only honoured if it survives that check.
    /// </summary>
    public void SetResolved(Guid spaceId) => _resolved = spaceId;

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
