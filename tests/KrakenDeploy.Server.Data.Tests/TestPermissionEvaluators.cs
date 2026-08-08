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
    /// <summary>
    /// Permissions this "allow all" double nonetheless DENIES. Needed to express
    /// "holds the respond permission but is not a system administrator" — WP3's gate
    /// grants AdministerSystem a break-glass override, so a blanket allow-all would
    /// pass every authorization test for the wrong reason.
    /// </summary>
    public HashSet<Permission> Denied { get; init; } = [];

    public Task<bool> HasPermissionAsync(
        ClaimsPrincipal user, Permission permission, PermissionScope scope = default,
        bool bypassCache = false, bool strictScope = false, CancellationToken ct = default)
        => Task.FromResult(!Denied.Contains(permission));

    public Task<IReadOnlySet<Permission>> GetPermissionsAsync(
        ClaimsPrincipal user, PermissionScope scope = default, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Permission>>(
            new HashSet<Permission>(Enum.GetValues<Permission>().Except(Denied)));

    public Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    /// <summary>
    /// Team memberships this fake reports (WP3 — the manual-intervention gate checks
    /// responsible-team membership directly, not through a permission). Empty by
    /// default, which is correct for the many tests that use an empty responsible-team
    /// list: an empty list means "anyone with the permission may respond".
    /// </summary>
    public HashSet<Guid> TeamIds { get; init; } = [];

    public Task<IReadOnlySet<Guid>> GetUserTeamIdsAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Guid>>(TeamIds);
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

    public Task<IReadOnlySet<Guid>> GetUserTeamIdsAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}
