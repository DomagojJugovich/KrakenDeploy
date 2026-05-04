using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;

namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// HTTP-aware <see cref="ISpaceContext"/> used by the Blazor server and the
/// minimal-API endpoints. Replaces <c>DefaultSpaceContext</c> (the simpler
/// fallback that just returns <see cref="WellKnown.DefaultSpaceId"/>) when the
/// HTTP pipeline is available.
/// <para>
/// Resolution order (first match wins):
/// </para>
/// <list type="number">
///   <item>Explicit override pushed via <see cref="WithSpace"/> — used by
///         background workers, system admin operations, and tests.</item>
///   <item>The <c>kraken-active-space</c> cookie set by the Space switcher.
///         A separate cookie (rather than an auth claim) so switching Spaces
///         doesn't require re-issuing the auth cookie.</item>
///   <item><see cref="WellKnown.DefaultSpaceId"/> — fallback for unauthenticated
///         requests, single-Space on-prem installs, and any pre-switcher state.</item>
/// </list>
/// <para>
/// Slug-based routing (<c>/s/{spaceSlug}/...</c>) is not yet wired — that's
/// turned on later when the cloud SaaS deployment model needs per-tenant URLs.
/// </para>
/// </summary>
public sealed class HttpSpaceContext(IHttpContextAccessor httpContextAccessor) : ISpaceContext
{
    /// <summary>Cookie name carrying the user's currently-selected Space ID.</summary>
    public const string ActiveSpaceCookieName = "kraken-active-space";

    /// <summary>
    /// Role claim that grants system-wide admin privileges. Will be replaced by
    /// the M10 Permission/Role/Team model once it lands; for now any user in
    /// this role bypasses Space restrictions.
    /// </summary>
    public const string SystemAdminRole = "SystemAdministrator";

    private readonly Stack<Guid> _overrides = new();

    public Guid CurrentSpaceId
    {
        get
        {
            // 1. Explicit override (workers, tests, admin operations)
            if (_overrides.Count > 0)
            {
                return _overrides.Peek();
            }

            var ctx = httpContextAccessor.HttpContext;
            if (ctx is null)
            {
                return WellKnown.DefaultSpaceId;
            }

            // 2. Cookie set by the Space switcher
            if (ctx.Request.Cookies.TryGetValue(ActiveSpaceCookieName, out var cookieValue) &&
                Guid.TryParse(cookieValue, out var fromCookie))
            {
                return fromCookie;
            }

            // 3. Fallback
            return WellKnown.DefaultSpaceId;
        }
    }

    public bool IsSystemAdmin
    {
        get
        {
            var ctx = httpContextAccessor.HttpContext;
            if (ctx?.User is not ClaimsPrincipal user || user.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            return user.IsInRole(SystemAdminRole);
        }
    }

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
