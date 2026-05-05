using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.AspNetCore.Authorization;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Resolves <see cref="PermissionRequirement"/>s by delegating to
/// <see cref="IPermissionEvaluator"/> with the current <see cref="ISpaceContext"/>'s
/// active Space.
/// <para>
/// Per-Space scope is automatic: every check is evaluated against the user's
/// active Space, so endpoints don't need to plumb SpaceId through. For
/// finer-grained Project / Environment / Tenant scopes, pass the relevant
/// <see cref="PermissionScope"/> via <c>AuthorizationHandlerContext.Resource</c>
/// — the handler reads it when present.
/// </para>
/// </summary>
public sealed class PermissionAuthorizationHandler(
    IPermissionEvaluator evaluator,
    ISpaceContext spaceContext)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // Resource may be a PermissionScope provided by the endpoint for
        // sub-Space checks (e.g. "ProjectEdit on project X"). Default to the
        // current Space-wide scope.
        var scope = context.Resource is PermissionScope explicitScope
            ? explicitScope with { SpaceId = explicitScope.SpaceId ?? spaceContext.CurrentSpaceId }
            : new PermissionScope(SpaceId: spaceContext.CurrentSpaceId);

        if (await evaluator.HasPermissionAsync(context.User, requirement.Permission, scope)
                .ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}
