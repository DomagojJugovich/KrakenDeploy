using System.Globalization;
using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Query-string → <see cref="AuditExportService.Filter"/> resolution for the
/// <c>/api/audit/export.csv|.json</c> endpoints, including the authorization
/// decisions the filter encodes.
/// <para>
/// The /api surface has no ambient Space (requests that never run a
/// Space-scoped page fall back to the Default Space), so a <c>perm:</c>
/// policy on these endpoints would evaluate EventView against the wrong
/// Space. Instead the caller names the Space explicitly (<c>space=</c>) and
/// this resolver enforces, in order:
/// </para>
/// <list type="number">
///   <item><c>space</c> must parse as a GUID — 400 otherwise.</item>
///   <item>The Space must be in the caller's accessible set
///         (<see cref="IPermissionEvaluator.GetAccessibleSpaceIdsAsync"/>) —
///         the hard tenant boundary; 403 otherwise.</item>
///   <item>The caller must hold <see cref="Permission.EventView"/> scoped to
///         that Space — 403 otherwise.</item>
///   <item><c>includeSystem=true</c> (rows with no Space) additionally
///         requires <see cref="Permission.AdministerSystem"/> — 403 otherwise.
///         A denied request fails loud rather than silently dropping rows the
///         caller explicitly asked for.</item>
/// </list>
/// </summary>
public static class AuditExportEndpoint
{
    /// <summary>
    /// Returns the validated filter, or an error result to short-circuit the
    /// response with (exactly one of the tuple members is non-null).
    /// </summary>
    public static async Task<(AuditExportService.Filter? Filter, IResult? Error)> ResolveFilterAsync(
        HttpRequest request,
        ClaimsPrincipal user,
        IPermissionEvaluator permissions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(permissions);

        if (!Guid.TryParse(request.Query["space"].FirstOrDefault(), out var spaceId))
        {
            return (null, Results.BadRequest(
                new { error = "space (GUID) query parameter is required." }));
        }

        var accessible = await permissions.GetAccessibleSpaceIdsAsync(user, ct)
            .ConfigureAwait(false);
        if (!accessible.Contains(spaceId))
        {
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        if (!await permissions.HasPermissionAsync(
                user, Permission.EventView, new PermissionScope(SpaceId: spaceId), ct: ct)
                .ConfigureAwait(false))
        {
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        var includeSystem = false;
        var includeRaw = request.Query["includeSystem"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(includeRaw))
        {
            if (!bool.TryParse(includeRaw, out includeSystem))
            {
                return (null, Results.BadRequest(
                    new { error = "includeSystem must be 'true' or 'false'." }));
            }
            if (includeSystem
                && !await permissions.HasPermissionAsync(
                        user, Permission.AdministerSystem, ct: ct).ConfigureAwait(false))
            {
                return (null, Results.StatusCode(StatusCodes.Status403Forbidden));
            }
        }

        // Malformed dates are a 400, not a silently-null filter: a null
        // boundary WIDENS the window, so swallowing a parse failure would
        // return more rows than the caller asked for.
        DateTimeOffset? from = null, to = null;
        var fromRaw = request.Query["from"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fromRaw))
        {
            if (!DateTimeOffset.TryParse(
                    fromRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var f))
            {
                return (null, Results.BadRequest(
                    new { error = "from must be an ISO-8601 timestamp." }));
            }
            from = f;
        }
        var toRaw = request.Query["to"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(toRaw))
        {
            if (!DateTimeOffset.TryParse(
                    toRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            {
                return (null, Results.BadRequest(
                    new { error = "to must be an ISO-8601 timestamp." }));
            }
            to = t;
        }

        // The page UI sends "to" as an inclusive day boundary; the service
        // treats it as exclusive (< to). The caller already adds the +1 day.
        return (new AuditExportService.Filter(
            SpaceIds:            [spaceId],
            IncludeSystemRows:   includeSystem,
            FromUtc:             from,
            ToUtcExclusive:      to,
            EventTypeContains:   request.Query["eventType"].FirstOrDefault(),
            UserDisplayContains: request.Query["user"].FirstOrDefault(),
            SubjectTypeContains: request.Query["subjectType"].FirstOrDefault()), null);
    }
}
