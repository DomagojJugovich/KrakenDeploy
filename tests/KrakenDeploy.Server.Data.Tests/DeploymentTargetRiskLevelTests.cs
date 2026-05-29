using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.E.11 — persistence tests for <see cref="DeploymentTarget.RiskLevel"/>.
/// Pins the fail-safe default (unclassified = Production) and the round-trip,
/// since the ad-hoc approval policy keys off this value.
/// </summary>
[Collection("Postgres")]
public sealed class DeploymentTargetRiskLevelTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.DeploymentTargets.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task New_target_defaults_to_Production_risk()
    {
        Guid id;
        await using (var db = postgres.CreateContext())
        {
            var t = new DeploymentTarget
            {
                Name = "web-01", Roles = ["web"], Status = TargetStatus.Online,
            };
            db.DeploymentTargets.Add(t);
            await db.SaveChangesAsync();
            id = t.Id;
        }

        await using (var db = postgres.CreateContext())
        {
            var loaded = await db.DeploymentTargets.SingleAsync(t => t.Id == id);
            loaded.RiskLevel.Should().Be(TargetRiskLevel.Production,
                "an unclassified target is fail-safe Production until downgraded");
        }
    }

    [Theory]
    [InlineData(TargetRiskLevel.Development)]
    [InlineData(TargetRiskLevel.Staging)]
    [InlineData(TargetRiskLevel.Production)]
    public async Task RiskLevel_round_trips(TargetRiskLevel level)
    {
        Guid id;
        await using (var db = postgres.CreateContext())
        {
            var t = new DeploymentTarget
            {
                Name = "t", Roles = [], Status = TargetStatus.Online, RiskLevel = level,
            };
            db.DeploymentTargets.Add(t);
            await db.SaveChangesAsync();
            id = t.Id;
        }

        await using (var db = postgres.CreateContext())
        {
            var loaded = await db.DeploymentTargets.SingleAsync(t => t.Id == id);
            loaded.RiskLevel.Should().Be(level);
        }
    }
}
