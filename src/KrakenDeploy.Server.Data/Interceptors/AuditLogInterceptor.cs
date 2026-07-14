using System.Security.Claims;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KrakenDeploy.Server.Data.Interceptors;

/// <summary>
/// EF Core <see cref="SaveChangesInterceptor"/> that automatically writes an
/// <see cref="AuditEntry"/> for every <see cref="AuditableEntity"/> that is
/// Added, Modified, or Deleted within the same transaction.
/// <para>
/// Registered as a singleton; uses <see cref="IHttpContextAccessor"/> (which
/// is also singleton-safe) to resolve the current user per call.
/// </para>
/// </summary>
public sealed class AuditLogInterceptor(
    IHttpContextAccessor httpAccessor,
    TimeProvider time) : SaveChangesInterceptor
{
    // Properties whose values must never appear in audit snapshots.
    private static readonly HashSet<string> SensitiveProperties =
    [
        "ClientSecretEncrypted",
        "PasswordHash",
        "SecurityStamp",
        "ConcurrencyStamp",
        "HmacKeyEncrypted",
        "KeyHash",
        "WrappedDek",
    ];

    // Audit-bookkeeping columns. Excluded from snapshots so they don't
    // (a) clutter the diff UI and (b) cause Before != After on otherwise
    // no-op updates (ModifiedUtc is bumped on every save by
    // AuditableEntityInterceptor before this one runs).
    private static readonly HashSet<string> AuditMetadataProperties =
    [
        "CreatedUtc",
        "ModifiedUtc",
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        AppendAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AppendAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // ── Core logic ────────────────────────────────────────────────────────────

    private void AppendAuditEntries(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now  = time.GetUtcNow();
        var http = httpAccessor.HttpContext;

        var (userId, userDisplay) = ResolveUser(http);
        var ip        = http?.Connection.RemoteIpAddress?.ToString();
        var userAgent = http?.Request.Headers.UserAgent.FirstOrDefault();

        // Snapshot changed AuditableEntity entries *before* calling base so
        // the change tracker still holds the original values.
        var changed = context.ChangeTracker
            .Entries<AuditableEntity>()
            .Where(e => e.State is EntityState.Added
                                or EntityState.Modified
                                or EntityState.Deleted)
            .ToList();

        foreach (var entry in changed)
        {
            var entityType = entry.Entity.GetType().Name;
            var suffix     = entry.State switch
            {
                EntityState.Added    => "Created",
                EntityState.Modified => "Updated",
                EntityState.Deleted  => "Deleted",
                _                   => "Changed",
            };

            var subjectId   = TryGetPropertyValue(entry, "Id")?.ToString();
            // Prefer a "Name" property; fall back to "Key" so keyed documents —
            // notably the unified `settings` rows (smtp/backup/ai/…) — produce
            // distinguishable audit entries instead of a null subject.
            var subjectName = TryGetPropertyValue(entry, "Name")?.ToString()
                           ?? TryGetPropertyValue(entry, "Key")?.ToString();

            Guid? spaceId = null;
            if (entry.Metadata.FindProperty("SpaceId") is not null)
            {
                spaceId = TryGetPropertyValue(entry, "SpaceId") as Guid?;
            }

            var beforeJson = entry.State is EntityState.Modified or EntityState.Deleted
                ? SerializeValues(entry.OriginalValues)
                : null;

            var afterJson = entry.State is EntityState.Added or EntityState.Modified
                ? SerializeValues(entry.CurrentValues)
                : null;

            // Modified entries can produce identical Before/After when a collection
            // property is reassigned with content-equal values but no ValueComparer
            // is configured — EF Core flags it as Modified anyway. Skip such no-ops
            // so the audit log only records real changes.
            if (entry.State is EntityState.Modified && beforeJson == afterJson)
            {
                continue;
            }

            context.Set<AuditEntry>().Add(new AuditEntry
            {
                OccurredUtc = now,
                SpaceId     = spaceId,
                UserId      = userId,
                UserDisplay = userDisplay,
                EventType   = $"{entityType}.{suffix}",
                SubjectType = entityType,
                SubjectId   = subjectId,
                SubjectName = subjectName,
                IpAddress   = ip,
                UserAgent   = userAgent,
                BeforeJson  = beforeJson,
                AfterJson   = afterJson,
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (Guid? userId, string userDisplay) ResolveUser(HttpContext? http)
    {
        if (http?.User?.Identity?.IsAuthenticated != true)
        {
            return (null, "System");
        }

        var idStr   = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userId  = Guid.TryParse(idStr, out var id) ? id : (Guid?)null;
        var display = http.User.Identity?.Name
                   ?? http.User.FindFirstValue(ClaimTypes.Email)
                   ?? "Unknown";
        return (userId, display);
    }

    private static object? TryGetPropertyValue(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string propertyName)
    {
        var prop = entry.Metadata.FindProperty(propertyName);
        if (prop is null)
        {
            return null;
        }

        return entry.State is EntityState.Deleted
            ? entry.OriginalValues[prop]
            : entry.CurrentValues[prop];
    }

    private static string SerializeValues(
        Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues values)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in values.Properties)
        {
            if (SensitiveProperties.Contains(prop.Name) ||
                AuditMetadataProperties.Contains(prop.Name))
            {
                continue;
            }

            var val = values[prop];

            // Skip navigation / shadow properties that can't be serialised.
            dict[prop.Name] = val switch
            {
                byte[] b  => Convert.ToBase64String(b),
                Guid g    => g.ToString(),
                DateTimeOffset dto => dto.ToString("O"),
                DateTime dt        => dt.ToString("O"),
                _                  => val,
            };
        }

        return JsonSerializer.Serialize(dict, JsonOpts);
    }
}
