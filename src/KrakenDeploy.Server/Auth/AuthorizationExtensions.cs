using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Fluent helpers for declaring permission requirements on minimal-API
/// endpoints. Encapsulates the <c>"perm:..."</c> policy-name convention
/// so call sites stay readable.
/// </summary>
public static class AuthorizationExtensions
{
    /// <summary>
    /// Restricts the endpoint to callers with <paramref name="permission"/>
    /// in their currently active Space.
    /// <para>
    /// Equivalent to <c>RequireAuthorization(PermissionPolicyProvider.PolicyNameFor(permission))</c>.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// app.MapPost("/api/projects",
    ///     async (CreateProjectRequest req, ProjectService svc) => …)
    ///    .RequirePermission(Permission.ProjectCreate);
    /// </code>
    /// </example>
    public static TBuilder RequirePermission<TBuilder>(
        this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.RequireAuthorization(PermissionPolicyProvider.PolicyNameFor(permission));
    }

    // RequireAnyPermission (logical OR across multiple permissions) needs a
    // custom single-policy-with-OR-semantics — multiple RequireAuthorization
    // calls AND together. Defer until first concrete need; most endpoints
    // gate on a single permission.
}
