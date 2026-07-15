using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.AspNetCore.Components.Authorization;
using Radzen;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Execution-time authorization guard for interactive Blazor handlers.
/// <para>
/// Privileged button handlers (delete / upload / save …) run over the SignalR
/// circuit and call services directly — they are <em>not</em> behind the HTTP
/// authorization middleware that protects the REST endpoints. Relying solely on
/// the <c>&lt;RequirePermission&gt;</c> UI gate to hide the button is therefore
/// not enough: the gate is rendered once and its result is cached for the
/// circuit, so a revoked user could still trigger the handler. This guard
/// re-checks the permission server-side at action time with
/// <c>bypassCache: true</c> (an authoritative, never-stale read) and surfaces a
/// denial via a notification.
/// </para>
/// <para>
/// Usage — guard the mutation as the first line of the handler:
/// <code>
/// private async Task DeleteAsync(Package p)
/// {
///     if (!await Guard.AllowAsync(Permission.PackageDelete)) return;
///     // ... privileged work ...
/// }
/// </code>
/// </para>
/// </summary>
public sealed class UiActionGuard(
    AuthenticationStateProvider authState,
    ISpaceContext spaceContext,
    IPermissionEvaluator evaluator,
    NotificationService notifications)
{
    /// <summary>
    /// Authoritative (uncached) permission check for an interactive action.
    /// Returns <c>true</c> when allowed; on denial fires an error notification
    /// and returns <c>false</c> so the caller can early-return.
    /// </summary>
    /// <param name="permission">The permission the action requires.</param>
    /// <param name="scope">
    /// Optional finer-grained scope (Project / Environment / Tenant …). Defaults
    /// to the active Space, mirroring what the page's <c>&lt;RequirePermission&gt;</c>
    /// uses. Pass the same scope the UI gate uses for sub-Space actions.
    /// </param>
    public async Task<bool> AllowAsync(
        Permission permission, PermissionScope? scope = null, CancellationToken ct = default)
    {
        var state = await authState.GetAuthenticationStateAsync().ConfigureAwait(false);
        var effective = scope ?? new PermissionScope(SpaceId: spaceContext.CurrentSpaceId);

        if (await evaluator.HasPermissionAsync(state.User, permission, effective, bypassCache: true, ct: ct)
                .ConfigureAwait(false))
        {
            return true;
        }

        notifications.Notify(NotificationSeverity.Error, "Not allowed",
            "You no longer have permission to perform this action.", 5000);
        return false;
    }
}
