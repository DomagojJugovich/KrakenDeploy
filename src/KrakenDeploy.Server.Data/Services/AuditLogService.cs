using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// EF Core-backed implementation of <see cref="IAuditLog"/> for non-EF audit
/// events (sign-in, permission denied, etc.).  EF entity changes are handled
/// automatically by <c>AuditLogInterceptor</c>.
/// </summary>
public sealed class AuditLogService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IHttpContextAccessor httpAccessor,
    ISpaceContext spaceCtx,
    TimeProvider time) : IAuditLog
{
    /// <summary>Audit entries about one subject (newest first, bounded) —
    /// powers per-entity "Events" tabs (e.g. target detail).
    /// <para>
    /// Caged to <paramref name="spaceId"/> via the audit choke point
    /// (<see cref="AuditExportService.ApplySpaceVisibility"/>): the subject id
    /// comes from the page URL, so without the Space predicate a caller could
    /// probe another Space's entity ids and read their audit snapshots.
    /// System rows (<c>SpaceId IS NULL</c>) are excluded — platform events are
    /// not entity history. Pass the page's validated active Space.
    /// </para>
    /// <para>
    /// <paramref name="subjectType"/> narrows to one entity type and lets the
    /// query use the (subject_type, subject_id, occurred_utc) index instead of
    /// seq-scanning the largest table.
    /// </para></summary>
    public async Task<List<AuditEntry>> GetForSubjectAsync(
        string subjectType, string subjectId, Guid spaceId,
        int limit = 100, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await AuditExportService.ApplySpaceVisibility(
                db.AuditEntries.AsNoTracking(), [spaceId], includeSystemRows: false)
            .Where(e => e.SubjectType == subjectType && e.SubjectId == subjectId)
            .OrderByDescending(e => e.OccurredUtc)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task RecordAsync(
        string eventType,
        string? subjectType  = null,
        string? subjectId    = null,
        string? subjectName  = null,
        string? details      = null,
        Guid?   userId       = null,
        string? userDisplay  = null,
        CancellationToken ct = default)
    {
        var http = httpAccessor.HttpContext;

        // Ambient attribution, and ONLY when the caller supplied none. A caller that
        // passes userDisplay is declaring who (or what) acted, so the fallback must not
        // override it — the reachable case is a request authenticated as something other
        // than a user. The agent wire-contract gate runs on a request whose principal is an
        // AGENT: its NameIdentifier is a DeploymentTarget id, so the fallback would stamp
        // UserId with a target GUID that resolves to no user and renders as "Unknown".
        // Passing userDisplay: "System" is how such a call site opts out.
        if (userId is null && userDisplay is null
            && http?.User?.Identity?.IsAuthenticated == true)
        {
            var raw = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(raw, out var uid))
            {
                userId = uid;
            }

            userDisplay ??= http.User.Identity?.Name
                         ?? http.User.FindFirstValue(ClaimTypes.Email)
                         ?? "Unknown";
        }

        userDisplay ??= "System";

        var entry = new AuditEntry
        {
            OccurredUtc = time.GetUtcNow(),
            SpaceId     = TryGetSpaceId(),
            UserId      = userId,
            UserDisplay = userDisplay,
            EventType   = eventType,
            SubjectType = subjectType,
            SubjectId   = subjectId,
            SubjectName = subjectName,
            IpAddress   = http?.Connection.RemoteIpAddress?.ToString(),
            UserAgent   = http?.Request.Headers.UserAgent.FirstOrDefault(),
            Details     = details,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes audit entries older than <paramref name="retentionDays"/> days.
    /// Called by the Hangfire retention sweep (Slice I).
    /// </summary>
    public async Task<int> PurgeOldEntriesAsync(
        int retentionDays = 365,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var cutoff = time.GetUtcNow().AddDays(-retentionDays);
        // Deliberately NOT routed through the audit Space-visibility choke
        // point: this is the system-wide retention sweep — it deletes by age
        // across every Space and returns no row content to any caller.
        return await db.AuditEntries
            .Where(e => e.OccurredUtc < cutoff)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Guid? TryGetSpaceId()
    {
        try
        {
            return spaceCtx.CurrentSpaceId;
        }
        catch
        {
            return null;
        }
    }
}
