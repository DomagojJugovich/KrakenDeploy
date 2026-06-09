using System.Security.Cryptography;
using System.Text;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages one-time registration tokens for deployment targets.
/// The raw token is returned once (to show in the wizard); only its SHA-256
/// hash is persisted, giving the same security property as a password hash.
/// </summary>
public class TargetRegistrationService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    TimeProvider timeProvider,
    ILicenseGate licenseGate)
{
    private const int TokenByteLength = 32; // 256-bit → 43-char base64url
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    // ── Create ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="DeploymentTarget"/> with a fresh one-time
    /// registration token. Returns the target and the raw (unhashed) token.
    /// </summary>
    public Task<(DeploymentTarget Target, string PlainToken)> CreateAsync(
        string name,
        IReadOnlyList<string> roles,
        TransportMode transportMode,
        CancellationToken ct = default)
        => CreateAsync(name, roles, transportMode, bypassLicenseCheck: false, ct);

    /// <summary>
    /// As the public overload, but <paramref name="bypassLicenseCheck"/> skips
    /// the server-wide license quota gate. Only the dev-only
    /// <c>/api/dev/smoke-register</c> endpoint passes <c>true</c> — the CI smoke
    /// test runs against a fresh, license-less DB and just needs one target to
    /// prove agent connectivity. Every production path keeps license enforcement
    /// (the bool defaults to <c>false</c>).
    /// </summary>
    public async Task<(DeploymentTarget Target, string PlainToken)> CreateAsync(
        string name,
        IReadOnlyList<string> roles,
        TransportMode transportMode,
        bool bypassLicenseCheck,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(roles);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // License quota gate. Count across ALL Spaces — the license cap is
        // server-wide, not per-Space; an operator must not be able to bypass
        // it by hopping between Spaces. The count happens inside the same
        // DbContext for cheap-and-correct (any concurrent inserts will race,
        // but the cap is a soft business limit, not a security boundary —
        // worst case is +1 over the cap under heavy concurrent operator
        // activity, which the next attempt will block).
        if (!bypassLicenseCheck)
        {
            var currentTargets = await db.DeploymentTargets
                .IgnoreQueryFilters()
                .CountAsync(ct)
                .ConfigureAwait(false);
            var refusal = licenseGate.CheckTargetCreate(currentTargets);
            if (refusal is not null)
            {
                throw new LicenseLimitException(refusal);
            }
        }

        var (plainToken, hash) = GenerateToken();
        var now = timeProvider.GetUtcNow();

        var target = new DeploymentTarget
        {
            Name = name,
            Roles = [.. roles],
            TransportMode = transportMode,
            Status = transportMode == TransportMode.OfflineDrop
                ? TargetStatus.Offline
                : TargetStatus.Unknown,
            RegistrationKeyHash = transportMode == TransportMode.OfflineDrop ? null : hash,
            RegistrationTokenExpiresUtc = transportMode == TransportMode.OfflineDrop
                ? null
                : now.Add(TokenLifetime),
            OfflineDropConfig = transportMode == TransportMode.OfflineDrop
                ? new OfflineDropConfig()
                : null,
        };

        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (target, plainToken);
    }

    // ── Rotate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the registration token for an existing target.
    /// Returns the new raw token.
    /// </summary>
    public async Task<string> RotateTokenAsync(
        Guid targetId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var target = await db.DeploymentTargets
            .FindAsync(new object?[] { targetId }, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Target {targetId} not found.");

        var (plainToken, hash) = GenerateToken();
        target.RegistrationKeyHash = hash;
        target.RegistrationTokenExpiresUtc = timeProvider.GetUtcNow().Add(TokenLifetime);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return plainToken;
    }

    // ── Validate & consume ─────────────────────────────────────────────────

    /// <summary>
    /// Validates <paramref name="plainToken"/> against all targets.
    /// On success the token is consumed (hash cleared) and the target is
    /// returned. Returns <see langword="null"/> when the token is unknown
    /// or expired.
    /// </summary>
    public async Task<DeploymentTarget?> ValidateAndConsumeTokenAsync(
        string plainToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainToken);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var hash = Hash(plainToken);
        var now = timeProvider.GetUtcNow();

        var target = await db.DeploymentTargets
            .FirstOrDefaultAsync(
                t => t.RegistrationKeyHash == hash
                  && t.RegistrationTokenExpiresUtc > now,
                ct)
            .ConfigureAwait(false);

        if (target is null)
        {
            return null;
        }

        // Consume the token so it cannot be used again.
        target.RegistrationKeyHash = null;
        target.RegistrationTokenExpiresUtc = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return target;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (string PlainToken, string Hash) GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        var plain = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'); // URL-safe base64
        return (plain, Hash(plain));
    }

    private static string Hash(string plainToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
