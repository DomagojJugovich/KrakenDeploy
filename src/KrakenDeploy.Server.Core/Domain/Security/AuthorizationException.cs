namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Thrown by the authoritative service-layer scope check (T1-8) when the acting
/// user lacks the required permission at the resolved
/// <see cref="PermissionScope"/>. Distinct from a coarse HTTP 401/authorization-
/// policy failure: the caller authenticated and cleared the Space-level policy,
/// but is not authorized for this specific Project/Environment/Tenant.
/// <para>
/// Entry layers map it to <c>403 Forbidden</c> (REST) / an MCP error. The
/// message is deliberately generic — it never names the entity ids or the grant,
/// so a probe can't confirm which scope a user lacks.
/// </para>
/// </summary>
public sealed class AuthorizationException : Exception
{
    public AuthorizationException()
        : base("You do not have permission to perform this action in the requested scope.")
    {
    }

    public AuthorizationException(string message) : base(message)
    {
    }

    public AuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
