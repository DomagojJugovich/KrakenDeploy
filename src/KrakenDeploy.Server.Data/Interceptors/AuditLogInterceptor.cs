using System.Security.Claims;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using KrakenDeploy.Server.Core.Domain.Security;

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
    // "xmin" is the B5 concurrency token on server_tasks — a Postgres system
    // column, meaningless in an audit diff.
    private static readonly HashSet<string> AuditMetadataProperties =
    [
        "CreatedUtc",
        "ModifiedUtc",
        "xmin",
    ];

    // B5 — audit entries staged by the LAST SavingChanges per context, so a
    // FAILED save can detach them. Without this, a caller that catches the
    // failure and keeps using the context (the guarded status writer's
    // concurrency retry, or any recovery path) would persist the stale rows
    // with its next successful save: duplicates on a retry, and — worse —
    // audit records describing changes that never actually happened. Keyed
    // weakly so a context abandoned mid-failure doesn't leak.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, List<AuditEntry>>
        _stagedBySave = new();

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

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ForgetStaged(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ForgetStaged(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        DetachStaged(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        DetachStaged(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    // Concurrency conflicts surface through their own interception point
    // (ThrowingConcurrencyException), not only SaveChangesFailed — and the
    // guarded status writer's retry is exactly the caller that continues
    // using the context afterwards, so the staged cohort must be detached
    // here too.
    public override InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        DetachStaged(eventData.Context);
        return base.ThrowingConcurrencyException(eventData, result);
    }

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        DetachStaged(eventData.Context);
        return base.ThrowingConcurrencyExceptionAsync(eventData, result, cancellationToken);
    }

    private void ForgetStaged(DbContext? context)
    {
        if (context is not null)
        {
            _stagedBySave.Remove(context);
        }
    }

    /// <summary>The save rolled back — the changes these entries describe did
    /// NOT happen. Detach them so a later save on the same context (a retry, a
    /// recovery path) doesn't persist stale or duplicate audit records.</summary>
    private void DetachStaged(DbContext? context)
    {
        if (context is null || !_stagedBySave.TryGetValue(context, out var staged))
        {
            return;
        }
        foreach (var entry in staged)
        {
            context.Entry(entry).State = EntityState.Detached;
        }
        _stagedBySave.Remove(context);
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

        var staged = new List<AuditEntry>();

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
            else if (entry.Entity is Setting { ScopeType: SettingsScope.Space, ScopeId: { } settingSpaceId })
            {
                // The unified `settings` table isn't ISpaceScoped (no "SpaceId"
                // property); a Space-scoped document carries its Space in ScopeId.
                // Stamp it so per-Space audit views still attribute the change.
                spaceId = settingSpaceId;
            }

            // Non-null only for the unified settings table — drives payload scrubbing.
            var settingKey = (entry.Entity as Setting)?.Key;

            var beforeJson = entry.State is EntityState.Modified or EntityState.Deleted
                ? SerializeValues(entry.OriginalValues, settingKey)
                : null;

            var afterJson = entry.State is EntityState.Added or EntityState.Modified
                ? SerializeValues(entry.CurrentValues, settingKey)
                : null;

            // Modified entries can produce identical Before/After when a collection
            // property is reassigned with content-equal values but no ValueComparer
            // is configured — EF Core flags it as Modified anyway. Skip such no-ops
            // so the audit log only records real changes.
            if (entry.State is EntityState.Modified && beforeJson == afterJson)
            {
                continue;
            }

            var auditEntry = new AuditEntry
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
            };
            context.Set<AuditEntry>().Add(auditEntry);
            staged.Add(auditEntry);
        }

        // Remember THIS save's cohort so a failed save can detach it (see
        // DetachStaged). Replaces any previous cohort — that one either
        // committed (forgotten on SavedChanges) or was already detached.
        _stagedBySave.Remove(context);
        if (staged.Count > 0)
        {
            _stagedBySave.Add(context, staged);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>WP3-b — delegates to the one shared extraction in Core. This was one of
    /// FIVE private copies whose unknown-sentinels disagreed, so the same principal could
    /// be stamped under two different labels in two different tables.</summary>
    private static (Guid? userId, string userDisplay) ResolveUser(HttpContext? http)
        => http?.User.ResolveProvenance()
           ?? (null, ClaimsPrincipalExtensions.SystemLabel);

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
        Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues values,
        string? settingKey)
    {
        // The unified `settings` table stores secrets inside the opaque Payload
        // jsonb, which the name-based SensitiveProperties list can't reach. Scrub
        // the *Encrypted members out of a secret-bearing settings document's
        // payload before it lands in the (queryable, exportable, long-retained)
        // audit snapshot. settingKey is non-null only for Setting entities.
        var dict = new Dictionary<string, object?>();
        foreach (var prop in values.Properties)
        {
            if (SensitiveProperties.Contains(prop.Name) ||
                AuditMetadataProperties.Contains(prop.Name))
            {
                continue;
            }

            var val = values[prop];

            if (settingKey is not null && prop.Name == nameof(Setting.Payload) && val is string payload)
            {
                val = ScrubSettingPayload(settingKey, payload);
            }

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

    /// <summary>
    /// Returns the settings payload with every <c>*Encrypted</c> member nulled for
    /// a secret-bearing document, so ciphertext never reaches the audit log. Keeps
    /// the non-secret fields visible. Documents with no secrets pass through
    /// unchanged; anything unexpected falls back to a redaction marker.
    /// </summary>
    private static string ScrubSettingPayload(string? key, string payload)
    {
        if (string.IsNullOrEmpty(key))
        {
            return payload;
        }

        var descriptor = SettingsDocumentCatalog.Find(key);
        if (descriptor is null || descriptor.EncryptedMembers.Count == 0)
        {
            return payload;
        }

        try
        {
            var doc = JsonSerializer.Deserialize(payload, descriptor.ClrType, SettingsDocumentCatalog.JsonOptions);
            if (doc is null)
            {
                return payload;
            }

            foreach (var member in descriptor.EncryptedMembers)
            {
                if (member.GetValue(doc) is not null)
                {
                    member.SetValue(doc, null);
                }
            }

            return JsonSerializer.Serialize(doc, descriptor.ClrType, SettingsDocumentCatalog.JsonOptions);
        }
        catch
        {
            return "\"<redacted settings payload>\"";
        }
    }
}
