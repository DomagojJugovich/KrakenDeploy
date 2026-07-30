using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Performance;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using KrakenDeploy.Server.Core.Domain.Security;

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
        // WP3-b — one shared extraction (Core ClaimsPrincipalExtensions), not a private
        // chain. This was one of five copies whose sentinels disagreed.
        var http = httpAccessor.HttpContext;
        if (userId is null && http is not null)
        {
            var (resolvedId, resolvedDisplay) = http.User.ResolveProvenance();
            userId = resolvedId;
            if (resolvedId is not null)
            {
                userDisplay ??= resolvedDisplay;
            }
        }

        userDisplay ??= ClaimsPrincipalExtensions.SystemLabel;

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
    /// <para>
    /// CHANGE-CONTROL entries (<c>InterruptionAuditEvents.ChangeControlEventTypes</c> —
    /// who approved or refused a production change) are held to their own, longer window
    /// instead: <paramref name="changeControlRetentionDays"/>, where zero or negative
    /// means keep indefinitely (WP3-b). They need it because they are the LAST copy of
    /// the approval — the <c>interruptions</c> row is CASCADE-deleted with its task and
    /// <c>RetentionService</c> hard-deletes tasks — and RH state-sector change-control
    /// obligations routinely exceed the ordinary 365-day audit window.
    /// </para>
    /// </summary>
    public async Task<int> PurgeOldEntriesAsync(
        int retentionDays = 365,
        int changeControlRetentionDays = PerformanceSettings.DefaultChangeControlAuditRetentionDays,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-retentionDays);
        var changeControl = InterruptionAuditEvents.ChangeControlEventTypes;

        // Deliberately NOT routed through the audit Space-visibility choke
        // point: this is the system-wide retention sweep — it deletes by age
        // across every Space and returns no row content to any caller.
        var deleted = await db.AuditEntries
            .Where(e => e.OccurredUtc < cutoff && !changeControl.Contains(e.EventType))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (changeControlRetentionDays > 0)
        {
            var ccCutoff = now.AddDays(-changeControlRetentionDays);
            deleted += await db.AuditEntries
                .Where(e => e.OccurredUtc < ccCutoff && changeControl.Contains(e.EventType))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        return deleted;
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
