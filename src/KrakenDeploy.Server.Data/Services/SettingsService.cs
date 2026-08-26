using System.Collections.Concurrent;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// The single accessor for the unified <c>settings</c> table (fix 7 of the
/// 2026-07-10 schema hardening). Every read/write of a settings document goes
/// through here; an architecture test asserts no other code references the
/// <c>Setting</c> DbSet, so the Space-caging done here (System documents key on
/// <c>scope_id = NULL</c>; Space documents on the caller-supplied Space id) can
/// never be bypassed by a stray direct query.
///
/// <para>
/// <strong>Scoping</strong>: <typeparamref name="T"/> declares its scope
/// statically. System documents ignore <c>scopeId</c>; Space documents require a
/// non-empty <c>scopeId</c> from the caller (the caller owns the request's
/// <c>ISpaceContext</c> — this service opens its own DbContext scope for the read,
/// so it cannot resolve the caller's ambient Space itself).
/// </para>
/// <para>
/// <strong>Caching</strong>: a per-(scope, key) <see cref="ConcurrentDictionary"/>
/// of the raw payload string with a short TTL; each read deserializes a fresh
/// instance so callers may freely mutate the result (e.g. the SMTP service nulls
/// the ciphertext before returning). Registered Singleton on single-instance
/// (process-wide cache) and Scoped under multi-account so the cache is per-request
/// and can never serve one account's document to another via the shared
/// Default-Space id.
/// </para>
/// <para>
/// <strong>Concurrency</strong>: the <c>settings</c> row carries a PostgreSQL
/// <c>xmin</c> token, so a lost read-modify-write on a multi-key document (the
/// feature-flags overrides map) surfaces as <see cref="DbUpdateConcurrencyException"/>
/// and is retried against a fresh read by <see cref="MutateAsync{T}"/>.
/// </para>
/// </summary>
public sealed class SettingsService(IServiceScopeFactory scopeFactory, TimeProvider time)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    // Bounded optimistic-concurrency retry. Settings writes are rare (admin
    // actions), so real contention is low; the bound is generous enough that a
    // burst of concurrent feature-flag toggles all converge, yet finite so a
    // genuinely stuck write fails fast instead of livelocking.
    private const int MaxWriteAttempts = 10;

    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();

    private readonly record struct CacheKey(SettingsScope ScopeType, Guid? ScopeId, string Key);

    // Payload is the raw json string, or null when no row exists (the absence is
    // cached too, so a Space that never configured AI doesn't re-query every read).
    private sealed record CacheEntry(string? Payload, DateTimeOffset RefreshedUtc);

    /// <summary>
    /// Returns the document for <paramref name="scopeId"/>, or a default-shaped
    /// <c>new T()</c> when no row exists (property initializers are the backfill).
    /// </summary>
    public async Task<T> GetAsync<T>(Guid? scopeId = null, CancellationToken ct = default)
        where T : class, ISettingsDocument, new()
        => await TryGetAsync<T>(scopeId, ct).ConfigureAwait(false) ?? new T();

    /// <summary>
    /// Returns the persisted document, or <c>null</c> when no row exists. Use this
    /// (not <see cref="GetAsync{T}"/>) where "operator never saved" must be
    /// distinguished from "operator saved defaults" — e.g. the retention jobs'
    /// DB-wins-over-appsettings precedence.
    /// </summary>
    public async Task<T?> TryGetAsync<T>(Guid? scopeId = null, CancellationToken ct = default)
        where T : class, ISettingsDocument, new()
    {
        var key = ResolveKey<T>(scopeId);
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(key, out var entry) && (now - entry.RefreshedUtc) < CacheTtl)
        {
            return entry.Payload is null ? null : Deserialize<T>(entry.Payload);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var payload = await ReadPayloadAsync(db, key, ct).ConfigureAwait(false);
        _cache[key] = new CacheEntry(payload, time.GetUtcNow());
        return payload is null ? null : Deserialize<T>(payload);
    }

    /// <summary>Upserts <paramref name="document"/> as the full payload for its scope+key.</summary>
    public Task SaveAsync<T>(T document, Guid? scopeId = null, CancellationToken ct = default)
        where T : class, ISettingsDocument, new()
    {
        ArgumentNullException.ThrowIfNull(document);
        return MutateAsync<T>(scopeId, _ => document, ct);
    }

    /// <summary>
    /// Read-modify-write of a document under optimistic concurrency: loads (or
    /// default-constructs) the document, applies <paramref name="mutate"/>,
    /// persists, and retries on a concurrent write or a lost first-insert race so
    /// a multi-key document (feature-flag overrides) never loses an update.
    /// Returns the persisted document.
    /// </summary>
    public async Task<T> MutateAsync<T>(Guid? scopeId, Func<T, T> mutate, CancellationToken ct = default)
        where T : class, ISettingsDocument, new()
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var cacheKey = ResolveKey<T>(scopeId);

        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

            var row = await FindRowAsync(db, cacheKey, ct).ConfigureAwait(false);
            var current = row is null ? new T() : Deserialize<T>(row.Payload);
            var updated = mutate(current)
                ?? throw new InvalidOperationException("Settings mutation returned null.");
            var payload = Serialize(updated);

            if (row is null)
            {
                db.Set<Setting>().Add(new Setting
                {
                    ScopeType = cacheKey.ScopeType,
                    ScopeId = cacheKey.ScopeId,
                    Key = cacheKey.Key,
                    Payload = payload,
                });
            }
            else
            {
                row.Payload = payload;
            }

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                _cache[cacheKey] = new CacheEntry(payload, time.GetUtcNow());
                return updated;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWriteAttempts)
            {
                // xmin mismatch — another writer committed first. Reload + reapply.
            }
            catch (DbUpdateException ex) when (attempt < MaxWriteAttempts && IsUniqueViolation(ex))
            {
                // Concurrent first-time insert lost the unique race — retry as an update.
            }
        }
    }

    /// <summary>Drops the cached document for a scope+key (e.g. after an out-of-band write).</summary>
    public void Invalidate<T>(Guid? scopeId = null) where T : class, ISettingsDocument, new()
        => _cache.TryRemove(ResolveKey<T>(scopeId), out _);

    // ── Static helpers (the only other places that may touch db.Set<Setting>) ──

    /// <summary>
    /// Reads a document off a caller-provided context (no cache, no scope of its
    /// own). Used by the pre-DI startup path (Hangfire worker count) that has a
    /// bare <see cref="KrakenDbContext"/> before the container is built.
    /// </summary>
    public static async Task<T> ReadOrDefaultAsync<T>(
        KrakenDbContext db, Guid? scopeId = null, CancellationToken ct = default)
        where T : class, ISettingsDocument, new()
    {
        var key = ResolveKey<T>(scopeId);
        var payload = await ReadPayloadAsync(db, key, ct).ConfigureAwait(false);
        return payload is null ? new T() : Deserialize<T>(payload);
    }

    /// <summary>
    /// Reads a document off a caller-provided context and preserves the distinction
    /// between an absent row and a persisted document. Composition roots use this
    /// before DI is available to apply DB-over-file startup precedence.
    /// </summary>
    public static async Task<T?> TryReadAsync<T>(
        KrakenDbContext db, Guid? scopeId = null, CancellationToken ct = default)
        where T : class, ISettingsDocument, new()
    {
        var key = ResolveKey<T>(scopeId);
        var payload = await ReadPayloadAsync(db, key, ct).ConfigureAwait(false);
        return payload is null ? null : Deserialize<T>(payload);
    }

    /// <summary>
    /// Re-encrypts every <c>*Encrypted</c> member of every settings document on
    /// the supplied tracked context, applying <paramref name="reEncrypt"/> to each
    /// non-empty ciphertext. Does NOT save — the DEK-rotation walk owns the
    /// transaction. Returns the number of members rewritten.
    /// </summary>
    public static async Task<int> ReEncryptSettingsForRotationAsync(
        KrakenDbContext db, Func<string, string> reEncrypt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reEncrypt);

        var count = 0;
        var rows = await db.Set<Setting>().IgnoreQueryFilters().ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var descriptor = SettingsDocumentCatalog.Find(row.Key);
            if (descriptor is null || descriptor.EncryptedMembers.Count == 0)
            {
                continue;
            }

            var doc = JsonSerializer.Deserialize(row.Payload, descriptor.ClrType, SettingsDocumentCatalog.JsonOptions);
            if (doc is null)
            {
                continue;
            }

            var touched = false;
            foreach (var member in descriptor.EncryptedMembers)
            {
                if (member.GetValue(doc) is string cipher && cipher.Length > 0)
                {
                    member.SetValue(doc, reEncrypt(cipher));
                    touched = true;
                    count++;
                }
            }

            if (touched)
            {
                row.Payload = JsonSerializer.Serialize(doc, descriptor.ClrType, SettingsDocumentCatalog.JsonOptions);
            }
        }

        return count;
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private static Task<string?> ReadPayloadAsync(KrakenDbContext db, CacheKey key, CancellationToken ct)
        => key.ScopeType == SettingsScope.System
            ? db.Set<Setting>()
                .Where(s => s.ScopeType == key.ScopeType && s.ScopeId == null && s.Key == key.Key)
                .Select(s => (string?)s.Payload)
                .FirstOrDefaultAsync(ct)
            : db.Set<Setting>()
                .Where(s => s.ScopeType == key.ScopeType && s.ScopeId == key.ScopeId && s.Key == key.Key)
                .Select(s => (string?)s.Payload)
                .FirstOrDefaultAsync(ct);

    private static Task<Setting?> FindRowAsync(KrakenDbContext db, CacheKey key, CancellationToken ct)
        => key.ScopeType == SettingsScope.System
            ? db.Set<Setting>().FirstOrDefaultAsync(
                s => s.ScopeType == key.ScopeType && s.ScopeId == null && s.Key == key.Key, ct)
            : db.Set<Setting>().FirstOrDefaultAsync(
                s => s.ScopeType == key.ScopeType && s.ScopeId == key.ScopeId && s.Key == key.Key, ct);

    private static CacheKey ResolveKey<T>(Guid? scopeId) where T : class, ISettingsDocument, new()
    {
        var scopeType = T.Scope;
        var key = T.Key;
        if (scopeType == SettingsScope.System)
        {
            return new CacheKey(scopeType, null, key);
        }

        if (scopeId is null || scopeId.Value == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Settings document '{key}' is {scopeType}-scoped; a non-empty scopeId is required.");
        }

        return new CacheKey(scopeType, scopeId, key);
    }

    private static string Serialize<T>(T document) where T : class, ISettingsDocument
        => JsonSerializer.Serialize(document, SettingsDocumentCatalog.JsonOptions);

    private static T Deserialize<T>(string payload) where T : class, ISettingsDocument, new()
        => JsonSerializer.Deserialize<T>(payload, SettingsDocumentCatalog.JsonOptions) ?? new T();

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
