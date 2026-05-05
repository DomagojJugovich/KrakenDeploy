using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.AspNetCore.Authorization;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// ASP.NET Core authorization requirement that succeeds when the current
/// principal has <see cref="Permission"/> within the request's
/// <see cref="ISpaceContext.CurrentSpaceId"/>.
/// <para>
/// Pair with <see cref="PermissionAuthorizationHandler"/> and
/// <see cref="PermissionPolicyProvider"/> — together they let endpoints and
/// Blazor components declare permissions by name without registering each
/// permission as a separate policy.
/// </para>
/// </summary>
public sealed class PermissionRequirement(Permission permission) : IAuthorizationRequirement
{
    public Permission Permission { get; } = permission;
}
