namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Authoritative service-layer authorization helpers (T1-8). The mutating
/// services call <see cref="EnsureScopedAsync"/> after resolving the target
/// entity's real Project/Environment/Tenant, so every surface (REST, CLI, MCP)
/// is enforced at the one place they all converge.
/// </summary>
public static class PermissionEvaluatorExtensions
{
    /// <summary>
    /// Throws <see cref="AuthorizationException"/> unless the caller holds
    /// <paramref name="permission"/> at <paramref name="scope"/>. Uses the strict,
    /// never-stale check: a scope dimension the grant restricts but the caller
    /// left null fails closed, so under-specified write checks can't leak. A
    /// <see cref="CallerAuthorization.System"/> caller is skipped (authorized at
    /// origin).
    /// </summary>
    public static async Task EnsureScopedAsync(
        this IPermissionEvaluator evaluator,
        CallerAuthorization caller,
        Permission permission,
        PermissionScope scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(caller);

        if (caller.IsSystem)
        {
            return;
        }

        var ok = await evaluator.HasPermissionAsync(
            caller.User!, permission, scope,
            bypassCache: true, strictScope: true, ct).ConfigureAwait(false);

        if (!ok)
        {
            throw new AuthorizationException();
        }
    }
}
