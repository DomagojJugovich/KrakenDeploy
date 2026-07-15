using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// A8/T1-12 — the agent-token revocation gate. <see cref="AgentTokenValidator"/>
/// runs after the JWT signature/lifetime/iss/aud are already valid and must
/// fail CLOSED: only a token whose <c>atv</c> claim still equals the target's
/// current <c>AgentTokenVersion</c> is accepted. This pins the production auth
/// path against a real database (the same code Program.cs's OnTokenValidated
/// calls).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AgentTokenValidatorTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Matching_version_is_valid()
    {
        var id = await SeedTargetAsync(agentTokenVersion: 3);

        var outcome = await AgentTokenValidator.ValidateAsync(
            Principal(id, tokenVersion: 3), postgres);

        outcome.Should().Be(AgentTokenValidator.Outcome.Valid);
    }

    [Fact]
    public async Task Stale_version_is_rejected()
    {
        // The token was issued at version 2; the target has since been revoked (→ 3).
        var id = await SeedTargetAsync(agentTokenVersion: 3);

        var outcome = await AgentTokenValidator.ValidateAsync(
            Principal(id, tokenVersion: 2), postgres);

        outcome.Should().Be(AgentTokenValidator.Outcome.VersionMismatch);
    }

    [Fact]
    public async Task Unknown_target_is_rejected()
    {
        var outcome = await AgentTokenValidator.ValidateAsync(
            Principal(Guid.NewGuid(), tokenVersion: 0), postgres);

        outcome.Should().Be(AgentTokenValidator.Outcome.TargetNotFound);
    }

    [Fact]
    public async Task Missing_version_claim_is_rejected()
    {
        var id = await SeedTargetAsync(agentTokenVersion: 0);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())], "AgentJwt"));

        var outcome = await AgentTokenValidator.ValidateAsync(principal, postgres);

        outcome.Should().Be(AgentTokenValidator.Outcome.MissingClaims);
    }

    [Fact]
    public async Task Missing_subject_claim_is_rejected()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AgentTokenClaims.TokenVersion, "0")], "AgentJwt"));

        var outcome = await AgentTokenValidator.ValidateAsync(principal, postgres);

        outcome.Should().Be(AgentTokenValidator.Outcome.MissingClaims);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ClaimsPrincipal Principal(Guid targetId, int tokenVersion) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, targetId.ToString()),
            new Claim(AgentTokenClaims.TokenVersion, tokenVersion.ToString(CultureInfo.InvariantCulture)),
        ], "AgentJwt"));

    private async Task<Guid> SeedTargetAsync(int agentTokenVersion)
    {
        await using var db = postgres.CreateContext();
        var target = new DeploymentTarget
        {
            SpaceId           = WellKnown.DefaultSpaceId,
            Name              = $"atv-{Guid.NewGuid():N}"[..16],
            Roles             = ["web"],
            TransportMode     = TransportMode.Reverse,
            Status            = TargetStatus.Unknown,
            AgentTokenVersion = agentTokenVersion,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();
        return target.Id;
    }
}
