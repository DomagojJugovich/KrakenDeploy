using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// A8/T1-12 — the agent bearer token must carry the per-target revocation
/// version (<c>atv</c>) and the issuer/audience the validator now enforces, and
/// live only 90 days (not the old 365). Each test validates the token exactly as
/// the server's JwtBearer handler does (iss/aud/lifetime enforced), so a token
/// that fails to round-trip through validation is a real regression.
/// </summary>
public sealed class AgentJwtServiceTests
{
    // HS256 needs >= 32 bytes. Test-only key; never a real secret.
    private const string SigningKey = "kraken-unit-test-agent-jwt-signing-key-32b";

    private static AgentJwtService Build() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agent:JwtSigningKey"] = SigningKey,
                })
                .Build(),
            TimeProvider.System);

    // Mirrors Program.cs's TokenValidationParameters: enforce signature + iss +
    // aud + lifetime, exactly as the running server does.
    private static (ClaimsPrincipal Principal, JwtSecurityToken Token) Validate(string jwt)
    {
        var principal = new JwtSecurityTokenHandler().ValidateToken(jwt, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            ValidIssuer = AgentJwtService.Issuer,
            ValidAudience = AgentJwtService.Audience,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        }, out var validated);
        return (principal, (JwtSecurityToken)validated);
    }

    [Fact]
    public void Issue_stamps_target_id_and_token_version_claims()
    {
        var targetId = Guid.NewGuid();
        var jwt = Build().Issue(targetId, agentTokenVersion: 7);

        var (principal, _) = Validate(jwt);

        principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(targetId.ToString());
        principal.FindFirst(AgentTokenClaims.TokenVersion)!.Value.Should().Be("7");
    }

    [Fact]
    public void Issue_is_accepted_with_issuer_and_audience_enforced()
    {
        // If iss/aud were missing or wrong, Validate() (ValidateIssuer/Audience=true)
        // would throw — a successful round-trip is the assertion.
        var act = () => Validate(Build().Issue(Guid.NewGuid(), agentTokenVersion: 0));
        act.Should().NotThrow();
    }

    [Fact]
    public void Issue_token_lives_ninety_days()
    {
        var (_, token) = Validate(Build().Issue(Guid.NewGuid(), agentTokenVersion: 0));

        // Independent of wall-clock: the window between nbf and exp is exactly 90 days.
        (token.ValidTo - token.ValidFrom).Should().Be(TimeSpan.FromDays(90));
    }
}
