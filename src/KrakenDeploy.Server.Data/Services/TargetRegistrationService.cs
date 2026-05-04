using System.Security.Cryptography;
using System.Text;
using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages one-time registration tokens for deployment targets.
/// The raw token is returned once (to show in the wizard); only its SHA-256
/// hash is persisted, giving the same security property as a password hash.
/// </summary>
public class TargetRegistrationService(KrakenDbContext db, TimeProvider timeProvider)
{
    private const int TokenByteLength = 32; // 256-bit → 43-char base64url
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    // ── Create ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="DeploymentTarget"/> with a fresh one-time
    /// registration token. Returns the target and the raw (unhashed) token.
    /// </summary>
    public async Task<(DeploymentTarget Target, string PlainToken)> CreateAsync(
        string name,
        IReadOnlyList<string> roles,
        TransportMode transportMode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(roles);

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
