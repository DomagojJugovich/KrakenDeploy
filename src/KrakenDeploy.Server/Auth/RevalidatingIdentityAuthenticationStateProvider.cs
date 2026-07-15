using System.Security.Claims;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Revalidates the Blazor Server circuit's principal against the database on a
/// fixed interval (A7 / T1-13). The framework default provider captures the
/// principal once at circuit start and never rechecks it, so a password reset,
/// offboard, or security-stamp bump could not terminate a live circuit. This
/// provider re-runs on <see cref="RevalidationInterval"/> and tears the circuit's
/// auth down when the user's security stamp changed or the account was disabled.
/// <para>
/// This is the circuit-side half of session revocation; the cookie side is
/// <c>SecurityStampValidator</c> wired onto the auth cookie's
/// <c>OnValidatePrincipal</c> (both share the same interval).
/// </para>
/// </summary>
internal sealed class RevalidatingIdentityAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options,
    TimeSpan revalidationInterval)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    private readonly IdentityOptions _options = options.Value;

    protected override TimeSpan RevalidationInterval { get; } = revalidationInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // A per-revalidation scope: UserManager is scoped and this runs on a
        // timer outside any request scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await ValidateSecurityStampAsync(userManager, authenticationState.User)
            .ConfigureAwait(false);
    }

    private async Task<bool> ValidateSecurityStampAsync(
        UserManager<ApplicationUser> userManager, ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal).ConfigureAwait(false);
        if (user is null)
        {
            return false; // deleted account -> tear the circuit down
        }

        // A7/T1-13: an administratively disabled account loses its live circuit on
        // the next revalidation, independent of the stamp check below.
        if (user.IsDisabled)
        {
            return false;
        }

        if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }

        var principalStamp = principal.FindFirstValue(_options.ClaimsIdentity.SecurityStampClaimType);
        var userStamp = await userManager.GetSecurityStampAsync(user).ConfigureAwait(false);
        return principalStamp == userStamp;
    }
}
