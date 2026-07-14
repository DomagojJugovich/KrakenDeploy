using System.Security.Claims;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Reads the acting user's id + display name from the current principal for
/// provenance/audit stamping. Every authenticated surface carries the same claims:
/// cookie/OIDC sessions AND API keys both stamp <see cref="ClaimTypes.NameIdentifier"/>
/// = the user id and <see cref="ClaimTypes.Name"/> = the user's UserName (the API-key
/// handler stamps the OWNING user's name). Centralises the extraction previously
/// inlined in AuditLogInterceptor / AuditLogService / PermissionEvaluator.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The acting user's id, or <c>null</c> when the principal is
    /// unauthenticated or carries no parseable NameIdentifier.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal? user)
        => Guid.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>Human display name for provenance: UserName, else Email, else
    /// <c>null</c> (callers / <c>TaskInitiator</c> supply a fallback label).</summary>
    public static string? GetDisplayName(this ClaimsPrincipal? user)
        => user?.Identity?.Name ?? user?.FindFirstValue(ClaimTypes.Email);
}
