using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Issues HS256 JWT tokens for registered agents.
/// The token's <c>sub</c> claim carries the target's <see cref="Guid"/>;
/// <see cref="AgentHub"/> reads it back via <c>ClaimTypes.NameIdentifier</c>.
/// </summary>
public sealed class AgentJwtService
{
    private readonly SymmetricSecurityKey _key;
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(365);

    public AgentJwtService(IConfiguration configuration, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var raw = configuration["Agent:JwtSigningKey"]
            ?? throw new InvalidOperationException(
                "Agent:JwtSigningKey is not configured.");

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(raw));
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Creates a long-lived bearer token for the agent identified by
    /// <paramref name="targetId"/>. The token is valid for one year; rotate
    /// via the Targets UI to issue a shorter-lived replacement if needed.
    /// </summary>
    public string Issue(Guid targetId)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, targetId.ToString()),
            ]),
            NotBefore = now,
            Expires = now.Add(TokenLifetime),
            SigningCredentials = new SigningCredentials(
                _key,
                SecurityAlgorithms.HmacSha256),
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }
}
