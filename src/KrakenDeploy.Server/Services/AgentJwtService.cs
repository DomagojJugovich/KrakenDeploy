using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Issues HS256 JWT tokens for registered agents.
/// The token's <c>sub</c> claim carries the target's <see cref="Guid"/>;
/// <see cref="AgentHub"/> reads it back via <c>ClaimTypes.NameIdentifier</c>.
/// The <c>atv</c> claim (<see cref="AgentTokenClaims.TokenVersion"/>) carries the
/// target's token version so the server can revoke it (A8/T1-12).
/// </summary>
public sealed class AgentJwtService
{
    /// <summary>Token issuer — stamped and (A8/T1-12) enforced on validation.</summary>
    public const string Issuer = "KrakenDeploy";

    /// <summary>Token audience — stamped and (A8/T1-12) enforced on validation.</summary>
    public const string Audience = "KrakenDeploy.Agent";

    private readonly SymmetricSecurityKey _key;
    private readonly TimeProvider _timeProvider;

    // A8/T1-12: 90 days (was 365). Short enough to bound an UNKNOWN leak, long
    // enough to avoid a refresh subsystem for a controlled agent fleet. A KNOWN
    // leak is revoked immediately via the token-version bump — expiry is the
    // backstop for leaks we never learn about.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(90);

    public AgentJwtService(IConfiguration configuration, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var raw = configuration["Agent:JwtSigningKey"]
            ?? throw new InvalidOperationException(
                "Agent:JwtSigningKey is not configured.");

        var keyBytes = Encoding.UTF8.GetBytes(raw);
        if (keyBytes.Length < 32)
        {
            // HS256 requires a >=256-bit key; refuse a weak (brute-forceable) one.
            throw new InvalidOperationException(
                "Agent:JwtSigningKey must be at least 32 bytes (256 bits) for HS256.");
        }

        _key = new SymmetricSecurityKey(keyBytes);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Creates a 90-day bearer token for the agent identified by
    /// <paramref name="targetId"/>. <paramref name="agentTokenVersion"/> is the
    /// target's current <c>AgentTokenVersion</c>; it is stamped into the
    /// <c>atv</c> claim and compared on every connect/call so the token can be
    /// revoked by bumping the target's version. Revoke + re-enroll via the
    /// Targets UI (or <c>POST /api/targets/{id}/revoke-agent-token</c>).
    /// </summary>
    public string Issue(Guid targetId, int agentTokenVersion)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, targetId.ToString()),
                new Claim(
                    AgentTokenClaims.TokenVersion,
                    agentTokenVersion.ToString(CultureInfo.InvariantCulture)),
            ]),
            NotBefore = now,
            Expires = now.Add(TokenLifetime),
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = new SigningCredentials(
                _key,
                SecurityAlgorithms.HmacSha256),
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }
}
