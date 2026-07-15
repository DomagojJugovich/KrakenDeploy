using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Shared IPermissionEvaluator test doubles. <see cref="AllowAllPermissionEvaluator"/>
/// grants everything (for tests whose subject is NOT authorization — they just
/// need the service to construct and its scope check to pass);
/// <see cref="DenyAllPermissionEvaluator"/> denies everything.
/// </summary>
internal sealed class AllowAllPermissionEvaluator : IPermissionEvaluator
{
    public Task<bool> HasPermissionAsync(
        ClaimsPrincipal user, Permission permission, PermissionScope scope = default,
        bool bypassCache = false, bool strictScope = false, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<IReadOnlySet<Permission>> GetPermissionsAsync(
        ClaimsPrincipal user, PermissionScope scope = default, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Permission>>(
            new HashSet<Permission>(Enum.GetValues<Permission>()));

    public Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}

internal sealed class DenyAllPermissionEvaluator : IPermissionEvaluator
{
    public Task<bool> HasPermissionAsync(
        ClaimsPrincipal user, Permission permission, PermissionScope scope = default,
        bool bypassCache = false, bool strictScope = false, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<IReadOnlySet<Permission>> GetPermissionsAsync(
        ClaimsPrincipal user, PermissionScope scope = default, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>());

    public Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}
