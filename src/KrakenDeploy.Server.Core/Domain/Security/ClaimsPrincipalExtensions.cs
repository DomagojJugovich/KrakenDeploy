using System.Security.Claims;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Reads the acting user's id + display name from the current principal for
/// provenance/audit stamping. Every authenticated surface carries the same claims:
/// cookie/OIDC sessions AND API keys both stamp <see cref="ClaimTypes.NameIdentifier"/>
/// = the user id and <see cref="ClaimTypes.Name"/> = the user's UserName (the API-key
/// handler stamps the OWNING user's name).
/// <para>
/// Lives in <b>Core</b> so every layer can reach it. It previously sat in
/// <c>KrakenDeploy.Server/Auth</c>, which <c>Server.Data</c> does not reference — so
/// its own claim to have "centralised the extraction previously inlined in
/// AuditLogInterceptor / AuditLogService / PermissionEvaluator" was untrue: those
/// kept private copies with DIFFERENT fallback chains and different unknown-sentinels
/// (<c>"Unknown"</c> vs <c>"System"</c> vs a raw user id), so one principal could be
/// recorded under two different labels in two different tables. Core is referenced by
/// Data, Transport and Server, and already hosts <see cref="IPermissionEvaluator"/>,
/// which takes a <see cref="ClaimsPrincipal"/> — so this is where it belongs.
/// </para>
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The acting user's id, or <c>null</c> when the principal is
    /// unauthenticated or carries no parseable NameIdentifier.</summary>
    public static Guid? ResolveUserId(this ClaimsPrincipal? user)
        => Guid.TryParse(
            user?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>Human display name for provenance: UserName, else Email, else
    /// <c>null</c> (callers / <c>TaskInitiator</c> supply a fallback label).</summary>
    /// <remarks><c>FindFirst(...)?.Value</c> rather than <c>FindFirstValue</c>: the
    /// latter is an ASP.NET Core extension, and Core deliberately does not reference
    /// ASP.NET Core.</remarks>
    public static string? ResolveDisplayName(this ClaimsPrincipal? user)
        => user?.Identity?.Name ?? user?.FindFirst(ClaimTypes.Email)?.Value;

    /// <summary>Label for an unauthenticated actor — a background job, a sweeper, or
    /// any system-initiated write.</summary>
    public const string SystemLabel = "System";

    /// <summary>Label for an AUTHENTICATED actor carrying no usable name or email. A
    /// different fact from <see cref="SystemLabel"/>: somebody acted, and we cannot say
    /// who. Conflating the two is why this pair is resolved in one place.</summary>
    public const string UnknownLabel = "Unknown";

    /// <summary>
    /// The canonical (id, display) pair for provenance stamping — audit rows, task
    /// initiators, gate responders.
    /// <para>
    /// Every caller must use this rather than its own chain. Before WP3-b there were
    /// FIVE independent extractions (<c>AuditLogInterceptor</c>, <c>AuditLogService</c>,
    /// <c>PermissionEvaluator</c>, an alias shim in <c>KrakenDeploy.Server/Auth</c>, and
    /// <c>InterruptionService</c>) whose sentinels disagreed — <c>"Unknown"</c> vs
    /// <c>"System"</c> vs <c>"&lt;unknown&gt;"</c> — so the same principal could be
    /// recorded under two different labels in two different tables, and a reviewer
    /// reconciling an approval against the audit log saw two actors where there was one.
    /// </para>
    /// </summary>
    public static (Guid? UserId, string Display) ResolveProvenance(this ClaimsPrincipal? user)
        => user?.Identity?.IsAuthenticated != true
            ? (null, SystemLabel)
            : (user.ResolveUserId(), user.ResolveDisplayName() ?? UnknownLabel);
}
