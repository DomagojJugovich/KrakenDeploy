using System.Security.Claims;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Carries the authorization identity of whoever triggered a mutating service
/// operation, so the authoritative service-layer scope check (T1-8) runs for
/// every surface — REST, CLI (via REST), and MCP all converge on the service.
/// <para>
/// Two explicit shapes, never a fail-open default:
/// </para>
/// <list type="bullet">
///   <item><see cref="ForUser"/> — a real principal; the service MUST re-check
///     the permission at the resolved scope.</item>
///   <item><see cref="System"/> — a system-initiated call (a parent
///     <c>Octopus.DeployRelease</c> step, a subscription/scheduled trigger)
///     that was already authorized when the originating action was created; the
///     scope check is skipped. Choosing this is a deliberate, visible decision
///     at the call site, so a user path can't silently bypass the check.</item>
/// </list>
/// </summary>
public sealed class CallerAuthorization
{
    private CallerAuthorization(ClaimsPrincipal? user) => User = user;

    /// <summary>The acting principal, or <c>null</c> for a system-initiated call.</summary>
    public ClaimsPrincipal? User { get; }

    /// <summary>True when the call is system-initiated (no user to authorize).</summary>
    public bool IsSystem => User is null;

    /// <summary>A user-initiated call — the service re-checks scope against this principal.</summary>
    public static CallerAuthorization ForUser(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new CallerAuthorization(user);
    }

    /// <summary>
    /// A system-initiated call (parent step, subscription, schedule) — authorized
    /// at origin, so the service-layer scope check is deliberately skipped.
    /// </summary>
    public static readonly CallerAuthorization System = new(user: null);
}
