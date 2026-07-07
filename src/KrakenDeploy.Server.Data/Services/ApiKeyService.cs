using System.Security.Cryptography;
using System.Text;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Issue / list / revoke per-user API keys (M13.C.4).
/// <para>
/// The raw token (<c>kd-{prefix}-{secret}</c>) is returned exactly once at
/// creation; only its SHA-256 hash is persisted — same contract as
/// <see cref="TargetRegistrationService"/> registration tokens. Permission
/// enforcement (own vs <c>ApiKeyViewAll</c>/<c>ApiKeyDeleteAll</c>/
/// <c>ApiKeyCreateOthers</c>) is the caller's job, per house convention —
/// UI handlers gate via <c>UiActionGuard</c>, endpoints via
/// <c>RequirePermission</c>; the CLI host is operator-trusted.
/// </para>
/// </summary>
public sealed class ApiKeyService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    TimeProvider time,
    IAuditLog audit)
{
    /// <summary>Raw secret entropy: 32 bytes → 43-char base64url.</summary>
    private const int SecretByteLength = 32;

    /// <summary>Display-prefix entropy: 4 bytes → 8 hex chars.</summary>
    private const int PrefixByteLength = 4;

    /// <summary>
    /// Mints a key for <paramref name="userId"/>. Returns the persisted row
    /// plus the raw token — the ONLY time the token is available.
    /// </summary>
    /// <param name="mintingCallerId">The interactive user requesting the mint,
    /// for the non-repudiation invariant. When non-null and different from
    /// <paramref name="userId"/>, the owner MUST be a service account — a key
    /// minted for another <b>human</b> is indistinguishable from them in the
    /// audit log. Null = trusted operator context (the CLI, which already has
    /// DB access) and skips the check.</param>
    /// <exception cref="ArgumentException">Blank name.</exception>
    /// <exception cref="InvalidOperationException">Unknown user, unknown
    /// Space restriction, duplicate name for this owner, expiry in the past,
    /// or minting for another human.</exception>
    public async Task<CreatedApiKey> CreateAsync(
        Guid userId,
        string name,
        DateTimeOffset? expiresUtc = null,
        Guid? spaceId = null,
        Guid? mintingCallerId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();

        var now = time.GetUtcNow();
        if (expiresUtc is not null && expiresUtc <= now)
        {
            throw new InvalidOperationException(
                "Expiry must be in the future (or null for a non-expiring key).");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var owner = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No user with id '{userId}'.");

        // Non-repudiation backstop enforced at the SERVICE boundary (not just
        // the dialog's client-passed flag): an interactive caller may mint only
        // for themselves or for a service account, never for another human.
        if (mintingCallerId is { } caller
            && caller != userId
            && owner.Kind != UserKind.ServiceAccount)
        {
            throw new InvalidOperationException(
                "API keys may only be minted for your own account or for a service account.");
        }

        if (spaceId is not null)
        {
            // Spaces are platform-level rows (not themselves Space-filtered),
            // but IgnoreQueryFilters keeps this lookup independent of the
            // caller's ambient Space just in case.
            var spaceExists = await db.Spaces.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(s => s.Id == spaceId, ct).ConfigureAwait(false);
            if (!spaceExists)
            {
                throw new InvalidOperationException($"No Space with id '{spaceId}'.");
            }
        }

        var duplicate = await db.ApiKeys.AsNoTracking()
            .AnyAsync(k => k.UserId == userId && k.Name == name, ct).ConfigureAwait(false);
        if (duplicate)
        {
            throw new InvalidOperationException(
                $"User already has an API key named '{name}'. Pick a distinct purpose label.");
        }

        var (plainToken, prefix, hash) = GenerateToken();

        var key = new ApiKey
        {
            UserId     = userId,
            Name       = name,
            Prefix     = prefix,
            KeyHash    = hash,
            Scope      = ApiKeyScope.Full,
            SpaceId    = spaceId,
            ExpiresUtc = expiresUtc,
        };

        db.ApiKeys.Add(key);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex))
        {
            // Lost a concurrent race past the AnyAsync pre-check (TOCTOU) — the
            // ix_api_keys_user_id_name unique index is the real backstop.
            // Surface the same friendly message the sequential path throws so
            // the dialog + CLI catch blocks (InvalidOperationException) render it.
            throw new InvalidOperationException(
                $"User already has an API key named '{name}'. Pick a distinct purpose label.", ex);
        }

        // Redact-the-secret audit pattern: intent + hint, never the token.
        await audit.RecordAsync(
            AuditEventType.ApiKeyCreated,
            subjectType: "ApiKey",
            subjectId:   key.Id.ToString(),
            subjectName: name,
            details:
                $"Owner={owner.UserName}, Prefix={prefix}, " +
                $"Expires={(expiresUtc is null ? "never" : expiresUtc.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}, " +
                $"SpaceRestriction={(spaceId is null ? "none" : spaceId.Value.ToString())}",
            ct: ct).ConfigureAwait(false);

        return new CreatedApiKey(key, plainToken);
    }

    /// <summary>The current user's keys, newest first.</summary>
    public async Task<List<ApiKeyInfo>> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Filter the ApiKey entity BEFORE projecting: EF can't translate a
        // .Where pushed onto a projection that contains correlated subqueries.
        return await ProjectInfos(db, db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Every user's keys (admin view — gate on <c>ApiKeyViewAll</c>).</summary>
    public async Task<List<ApiKeyInfo>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ProjectInfos(db, db.ApiKeys.AsNoTracking()).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes a key (idempotent — revoking twice keeps the first timestamp).
    /// Returns false when the key does not exist.
    /// </summary>
    public async Task<bool> RevokeAsync(Guid keyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct).ConfigureAwait(false);
        if (key is null)
        {
            return false;
        }

        if (key.RevokedUtc is null)
        {
            key.RevokedUtc = time.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var owner = await db.Users.AsNoTracking()
                .Where(u => u.Id == key.UserId)
                .Select(u => u.UserName)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            await audit.RecordAsync(
                AuditEventType.ApiKeyRevoked,
                subjectType: "ApiKey",
                subjectId:   key.Id.ToString(),
                subjectName: key.Name,
                details:     $"Owner={owner ?? key.UserId.ToString()}, Prefix={key.Prefix}",
                ct: ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Auth-time lookup: recompute the hash of the presented token and find
    /// the row. Returns the key regardless of revoked/expired state — the
    /// caller distinguishes those for precise failure logging. Null when no
    /// key matches (including malformed tokens).
    /// </summary>
    public async Task<ApiKey?> FindByTokenAsync(string plainToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            return null;
        }

        var hash = Hash(plainToken);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == hash, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The full authentication decision for a presented token, with the
    /// owner resolved in the same context. The handler turns the status into
    /// an <c>AuthenticateResult</c> + a precise log line; policy about WHICH
    /// scopes may authenticate (e.g. refusing <see cref="ApiKeyScope.Enroll"/>
    /// on the general surface) stays in the handler.
    /// </summary>
    public async Task<ApiKeyAuthResult> AuthenticateTokenAsync(
        string plainToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            return new ApiKeyAuthResult(ApiKeyAuthStatus.UnknownKey, null, null);
        }

        var hash = Hash(plainToken);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var key = await db.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == hash, ct).ConfigureAwait(false);
        if (key is null)
        {
            return new ApiKeyAuthResult(ApiKeyAuthStatus.UnknownKey, null, null);
        }

        if (key.RevokedUtc is not null)
        {
            return new ApiKeyAuthResult(ApiKeyAuthStatus.Revoked, key, null);
        }

        if (key.ExpiresUtc is not null && key.ExpiresUtc <= time.GetUtcNow())
        {
            return new ApiKeyAuthResult(ApiKeyAuthStatus.Expired, key, null);
        }

        var ownerName = await db.Users.AsNoTracking()
            .Where(u => u.Id == key.UserId)
            .Select(u => u.UserName)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (ownerName is null)
        {
            // Keys die with their owner (UserService.DeleteAsync), so this is
            // a should-never-happen — fail closed rather than authenticate a
            // ghost principal.
            return new ApiKeyAuthResult(ApiKeyAuthStatus.OwnerMissing, key, null);
        }

        // A restricted key pins the request's ambient Space — the handler
        // needs the slug to stamp HttpSpaceContext.SetResolved.
        string? spaceSlug = null;
        if (key.SpaceId is not null)
        {
            spaceSlug = await db.Spaces.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Id == key.SpaceId)
                .Select(s => s.Slug)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        return new ApiKeyAuthResult(ApiKeyAuthStatus.Active, key, ownerName, spaceSlug);
    }

    /// <summary>
    /// Writes <c>last_used_utc</c> for a successful authentication. Callers
    /// throttle via <see cref="ApiKeyUsageTracker"/> — this method itself is
    /// a single unconditional UPDATE.
    /// </summary>
    public async Task TouchLastUsedAsync(Guid keyId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ApiKeys
            .Where(k => k.Id == keyId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedUtc, now), ct)
            .ConfigureAwait(false);
    }

    // ── Token machinery ─────────────────────────────────────────────────────

    /// <summary>
    /// Token shape: <c>kd-{8 hex chars}-{43-char base64url secret}</c>.
    /// The stored display prefix is <c>kd-{hex}</c>; the hash covers the
    /// FULL token string so a leaked DB row can never reconstruct a token.
    /// </summary>
    internal static (string PlainToken, string Prefix, string Hash) GenerateToken()
    {
        var prefix = "kd-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(PrefixByteLength));

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretByteLength))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'); // URL-safe base64

        var plain = $"{prefix}-{secret}";
        return (plain, prefix, Hash(plain));
    }

    internal static string Hash(string plainToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexString(bytes).ToLowerInvariant(); // 64-char lowercase hex
    }

    /// <summary>
    /// True when <paramref name="ex"/> is a Postgres unique-violation (23505).
    /// Reflection-reads Npgsql's <c>SqlState</c> so Server.Data needs no hard
    /// driver reference, with a message fallback — mirrors
    /// <c>EventDispatcher.IsUniqueViolation</c>. On the <c>api_keys</c> insert
    /// the only realistic 23505 is the <c>(user_id, name)</c> index; a KeyHash
    /// collision (SHA-256 of 256 random bits) is not physically reachable.
    /// </summary>
    private static bool IsUniqueNameViolation(DbUpdateException ex)
    {
        var current = (Exception?)ex;
        while (current is not null)
        {
            var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
            if (sqlState == "23505"
                || current.Message.Contains("23505", StringComparison.Ordinal)
                || current.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }

    // Correlated subqueries rather than query-syntax LEFT JOINs: EF Core's
    // relational provider can't translate a manual GroupJoin+DefaultIfEmpty
    // chain keyed on the nullable SpaceId (k.SpaceId == (Guid?)s.Id), so the
    // page threw InvalidOperationException. Scalar subqueries translate cleanly.
    // UserName three-way semantics preserved: existing row → UserName or
    // "(unknown)"; no row → "(deleted user)". Spaces ignore query filters
    // because the key's bound Space may sit outside the caller's access set.
    // The source query is filtered by the caller BEFORE projection — a .Where
    // over this projection (it carries subqueries) does not translate.
    private static IQueryable<ApiKeyInfo> ProjectInfos(KrakenDbContext db, IQueryable<ApiKey> keys) =>
        keys
            .OrderByDescending(k => k.CreatedUtc)
            .Select(k => new ApiKeyInfo(
                k.Id,
                k.UserId,
                db.Users.AsNoTracking()
                    .Where(u => u.Id == k.UserId)
                    .Select(u => u.UserName ?? "(unknown)")
                    .FirstOrDefault() ?? "(deleted user)",
                k.Name,
                k.Prefix,
                k.Scope,
                k.SpaceId,
                k.SpaceId == null
                    ? null
                    : db.Spaces.IgnoreQueryFilters().AsNoTracking()
                        .Where(s => s.Id == k.SpaceId)
                        .Select(s => s.Name)
                        .FirstOrDefault(),
                k.CreatedUtc,
                k.ExpiresUtc,
                k.LastUsedUtc,
                k.RevokedUtc));
}

/// <summary>
/// Throttle gate for <c>last_used_utc</c> writes: at most one DB write per
/// key per <see cref="Threshold"/>, so busy CLI/MCP sessions don't turn
/// every request into an UPDATE. Singleton; safe across tenant DBs in
/// multi-account because key ids are globally unique and it stores only
/// timestamps (the actual write rides the request's account-routed context).
/// </summary>
public sealed class ApiKeyUsageTracker(TimeProvider time)
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _lastWritten = new();

    /// <summary>True when this key's last-used column is due a write; atomically
    /// claims the slot so concurrent requests don't double-write.</summary>
    public bool ShouldWrite(Guid keyId)
    {
        var now = time.GetUtcNow();
        var last = _lastWritten.GetOrAdd(keyId, DateTimeOffset.MinValue);
        return now - last >= Threshold && _lastWritten.TryUpdate(keyId, now, last);
    }
}

// ── DTOs ────────────────────────────────────────────────────────────────────

/// <summary>Creation result: the persisted row + the raw token (shown once).</summary>
public sealed record CreatedApiKey(ApiKey Key, string PlainToken);

/// <summary>Outcome of <see cref="ApiKeyService.AuthenticateTokenAsync"/>.
/// <c>Key</c> is null only for <see cref="ApiKeyAuthStatus.UnknownKey"/>;
/// <c>OwnerUserName</c> is non-null only for <see cref="ApiKeyAuthStatus.Active"/>;
/// <c>SpaceSlug</c> is non-null only for an Active Space-restricted key.</summary>
public sealed record ApiKeyAuthResult(
    ApiKeyAuthStatus Status, ApiKey? Key, string? OwnerUserName, string? SpaceSlug = null);

public enum ApiKeyAuthStatus
{
    /// <summary>No key row matches the recomputed hash (or blank token).</summary>
    UnknownKey = 0,
    /// <summary>Key exists but was revoked.</summary>
    Revoked = 1,
    /// <summary>Key exists but its expiry has passed.</summary>
    Expired = 2,
    /// <summary>Key row survived its owner — fail closed (should never happen).</summary>
    OwnerMissing = 3,
    /// <summary>Key is live; authenticate as the owner.</summary>
    Active = 4,
}

/// <summary>Grid row for the API Keys page (own + admin views).</summary>
public sealed record ApiKeyInfo(
    Guid Id,
    Guid UserId,
    string UserName,
    string Name,
    string Prefix,
    ApiKeyScope Scope,
    Guid? SpaceId,
    string? SpaceName,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset? LastUsedUtc,
    DateTimeOffset? RevokedUtc)
{
    /// <summary>Masked hint per TASKS.md M13.C.4: <c>kd-4F2A9C1B•••••••</c>.</summary>
    public string Hint => Prefix + "•••••••";

    public bool IsRevoked => RevokedUtc is not null;
    public bool IsExpired(DateTimeOffset now) => !IsRevoked && ExpiresUtc is not null && ExpiresUtc <= now;
    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);
}
