using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// A8/T1-12 — the revocation trigger. <see cref="TargetService.RevokeAgentTokenAsync"/>
/// atomically bumps the target's <c>AgentTokenVersion</c> (so every outstanding
/// agent token is rejected by <see cref="AgentTokenValidator"/>), returns the new
/// version, and is a no-op (null) for a missing target.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class TargetServiceRevokeTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RevokeAgentTokenAsync_increments_version()
    {
        var svc = new TargetService(postgres);
        var id = await SeedTargetAsync(agentTokenVersion: 0);

        var v1 = await svc.RevokeAgentTokenAsync(id);
        var v2 = await svc.RevokeAgentTokenAsync(id);

        v1.Should().Be(1);
        v2.Should().Be(2);

        await using var db = postgres.CreateContext();
        var persisted = await db.DeploymentTargets
            .Where(t => t.Id == id)
            .Select(t => t.AgentTokenVersion)
            .FirstAsync();
        persisted.Should().Be(2, "the bump must be persisted so the next connect fails the version check");
    }

    [Fact]
    public async Task RevokeAgentTokenAsync_returns_null_for_missing_target()
    {
        var svc = new TargetService(postgres);

        (await svc.RevokeAgentTokenAsync(Guid.NewGuid())).Should().BeNull();
    }

    private async Task<Guid> SeedTargetAsync(int agentTokenVersion)
    {
        await using var db = postgres.CreateContext();
        var target = new DeploymentTarget
        {
            SpaceId           = WellKnown.DefaultSpaceId,
            Name              = $"rev-{Guid.NewGuid():N}"[..16],
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
