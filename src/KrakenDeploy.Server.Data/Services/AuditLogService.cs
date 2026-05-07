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

        if (userId is null && http?.User?.Identity?.IsAuthenticated == true)
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
