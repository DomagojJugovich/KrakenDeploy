using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Cross-Space regression for <see cref="RetentionService.PruneAfterDeploymentAsync"/>.
/// <para>
/// The method runs in a background DI scope (fire-and-forget from
/// <c>AgentHub.CompleteDeploymentAsync</c>) which has no active Space, so
/// <c>ISpaceContext.CurrentSpaceId</c> falls back to <c>WellKnown.DefaultSpaceId</c>.
/// Before the fix its by-id deployment load was space-filtered, so for a
/// deployment created in a non-Default Space the load returned null and the
/// whole method silently no-opped — retention never pruned and old deployments
/// accumulated past the keep limit. The service now resolves the deployment's
/// Space filter-free and runs the prune (lifecycle lookup, success-id query,
/// delete) under <c>ISpaceContext.WithSpace</c>.
/// </para>
/// <para>
/// The test drives the REAL service through a real DI container
/// (<c>AddKrakenDeployData</c>) so the scoped <see cref="ISpaceContext"/> is
/// shared between the service and its factory's <c>KrakenDbContext</c> — which
/// is what makes <c>WithSpace</c> flow into the query filter. The bare
/// <see cref="PostgresFixture"/> factory news up a fresh <c>DefaultSpaceContext</c>
/// per context and would NOT reproduce that wiring.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class RetentionServiceCrossSpaceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static readonly Guid NonDefaultSpaceId =
        Guid.Parse("0000ffff-0000-0000-0000-0000c0ffee01");

    [Fact]
    public async Task PruneAfterDeployment_prunes_within_a_non_default_Space()
    {
        var seeded = await SeedAsync();

        // Real DI container — the scoped ISpaceContext is shared between
        // RetentionService and its factory's DbContext, so WithSpace reaches the
        // query filter (the ambient Space here is DefaultSpaceId, exactly as in
        // the production background scope).
        var services = new ServiceCollection();
        services.AddKrakenDeployData(postgres.ConnectionString);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();

        await retention.PruneAfterDeploymentAsync(seeded.Newest);

        await using var db = postgres.CreateContext();
        var remaining = await db.Deployments.IgnoreQueryFilters()
            .Where(d => d.SpaceId == NonDefaultSpaceId && d.EnvironmentId == seeded.EnvId)
            .Select(d => d.Id)
            .ToListAsync();

        remaining.Should().ContainSingle(
                "keep=1 → the two older successful deployments in the non-Default " +
                "Space must be pruned (before the fix the method no-opped and all " +
                "three survived)")
            .Which.Should().Be(seeded.Newest,
                "retention keeps the newest successful deployment");
    }

    private sealed record Seeded(Guid Oldest, Guid Mid, Guid Newest, Guid EnvId);

    private async Task<Seeded> SeedAsync()
    {
        await using var db = postgres.CreateContext();

        if (!await db.Spaces.IgnoreQueryFilters().AnyAsync(s => s.Id == NonDefaultSpaceId))
        {
            db.Spaces.Add(new Space
            {
                Id = NonDefaultSpaceId, Slug = "ret-space", Name = "Retention Space",
            });
        }

        var env = new DeploymentEnvironment
        {
            SpaceId   = NonDefaultSpaceId,
            Name      = $"re-{Guid.NewGuid():N}"[..12],
            Slug      = $"re-{Guid.NewGuid():N}"[..12],
            SortOrder = 1,
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var lifecycle = new Lifecycle
        {
            SpaceId = NonDefaultSpaceId,
            Name    = "ret-lc",
            Phases  =
            [
                new LifecyclePhase
                {
                    Name                     = "Prod",
                    EnvironmentIds           = [env.Id],
                    RetentionKeepDeployments = 1,
                },
            ],
        };
        db.Lifecycles.Add(lifecycle);
        await db.SaveChangesAsync();

        var project = new Project
        {
            SpaceId     = NonDefaultSpaceId,
            Name        = $"rp-{Guid.NewGuid():N}"[..12],
            Slug        = $"rp-{Guid.NewGuid():N}"[..12],
            LifecycleId = lifecycle.Id,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId                    = NonDefaultSpaceId,
            ProjectId                  = project.Id,
            Version                    = "1.0",
            ProcessSnapshot            = [],
            VariableSnapshot           = [],
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        // Three successful deployments with distinct CompletedUtc so retention's
        // newest-first ordering is deterministic. keep=1 prunes the two oldest.
        var baseUtc = DateTimeOffset.UtcNow;
        var oldest = NewSuccessful(release.Id, env.Id, baseUtc.AddHours(-2));
        var mid    = NewSuccessful(release.Id, env.Id, baseUtc.AddHours(-1));
        var newest = NewSuccessful(release.Id, env.Id, baseUtc);
        db.Deployments.AddRange(oldest, mid, newest);
        await db.SaveChangesAsync();

        return new Seeded(oldest.Id, mid.Id, newest.Id, env.Id);

        static Deployment NewSuccessful(Guid releaseId, Guid envId, DateTimeOffset completedUtc)
            => new()
            {
                SpaceId       = NonDefaultSpaceId,
                ReleaseId     = releaseId,
                EnvironmentId = envId,
                Status        = DeploymentStatus.Succeeded,
                CompletedUtc  = completedUtc,
            };
    }
}
