using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

[Trait("Category", "Docker")]
[Collection("Postgres")]
public class MigrationsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Migrations_apply_idempotently()
    {
        await using var context = postgres.CreateContext();

        var pending = await context.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty(because: "fixture initialization already applied all migrations");

        var applied = await context.Database.GetAppliedMigrationsAsync();
        applied.Should().NotBeEmpty();
        applied.Should().Contain(name => name.EndsWith("_Initial", StringComparison.Ordinal));
    }

    [Fact]
    public void Model_has_no_pending_changes_against_snapshot()
    {
        using var context = postgres.CreateContext();

        context.Database.HasPendingModelChanges().Should().BeFalse(
            because: "the EF model and the latest migration snapshot must stay in sync — " +
                     "if this fails, run `dotnet ef migrations add <Name>`.");
    }

    [Fact]
    public async Task Project_roundtrips_with_audit_timestamps_set_by_interceptor()
    {
        await using var context = postgres.CreateContext();

        var project = new Project
        {
            Slug = $"smoke-{Guid.NewGuid():N}",
            Name = "Smoke Project",
            Description = "Phase 2 integration test",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(
                context, KrakenDeploy.Server.Core.Domain.Common.WellKnown.DefaultSpaceId)
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync();

        var loaded = await context.Projects.AsNoTracking().FirstAsync(p => p.Id == project.Id);
        loaded.Name.Should().Be("Smoke Project");
        loaded.CreatedUtc.Should().NotBe(default);
        loaded.ModifiedUtc.Should().BeNull();
    }

    [Fact]
    public async Task DeploymentTarget_roundtrips_role_array_through_text_array_column()
    {
        await using var context = postgres.CreateContext();

        var target = new DeploymentTarget
        {
            Name = $"smoke-target-{Guid.NewGuid():N}",
            Roles = ["web-server", "worker", "smoke-test"],
            TransportMode = TransportMode.Reverse,
            Status = TargetStatus.Unknown
        };

        context.DeploymentTargets.Add(target);
        await context.SaveChangesAsync();

        var loaded = await context.DeploymentTargets.AsNoTracking()
            .FirstAsync(t => t.Id == target.Id);
        loaded.Roles.Should().Equal("web-server", "worker", "smoke-test");
        loaded.TransportMode.Should().Be(TransportMode.Reverse);
    }
}
