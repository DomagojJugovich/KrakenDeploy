using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.ArtifactStorage;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Regression coverage for the cross-space IDOR remediation: the transport-
/// written child entities (deployment artifacts / log entries / step outcomes /
/// output variables) are now <c>ISpaceScoped</c>. A row created for a
/// deployment in another Space must (a) be stamped that Space, and (b) be
/// invisible to a query running under the (default) Space's global filter — so
/// one Space can no longer read another's deployment children by GUID.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class CrossSpaceScopingTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static readonly Guid OtherSpaceId = Guid.Parse("0000ffff-0000-0000-0000-00000000beef");

    [Fact]
    public async Task Artifact_saved_for_other_space_deployment_is_stamped_and_invisible_to_default_space()
    {
        var deploymentId = await SeedOtherSpaceDeploymentAsync();

        var svc = new ArtifactService(postgres, new FakeArtifactStore());
        using var content = new MemoryStream("hello"u8.ToArray());
        var artifact = await svc.SaveAsync(
            deploymentId, "deploy", "out.txt", "text/plain", 5, content);

        // Write site stamped the artifact with the PARENT deployment's Space,
        // not the worker context's Default Space.
        artifact.SpaceId.Should().Be(OtherSpaceId);

        // Read path runs under the default Space → the other Space's artifact
        // must not leak (this is the IDOR that was open before the fix).
        var visible = await svc.GetByDeploymentAsync(deploymentId);
        visible.Should().BeEmpty();

        // It does exist though — just walled off by Space.
        await using var raw = postgres.CreateContext();
        var all = await raw.DeploymentArtifacts.IgnoreQueryFilters()
            .Where(a => a.DeploymentId == deploymentId)
            .ToListAsync();
        all.Should().ContainSingle().Which.SpaceId.Should().Be(OtherSpaceId);
    }

    [Fact]
    public async Task Other_space_deployment_children_are_filtered_out_under_default_space()
    {
        var deploymentId = await SeedOtherSpaceDeploymentAsync();

        // Seed one of each transport-written child directly in the other Space
        // (explicit SpaceId — the interceptor preserves caller-set values, the
        // same contract every agent write site relies on).
        await using (var seed = postgres.CreateContext())
        {
            seed.DeploymentLogEntries.Add(new DeploymentLogEntry
            {
                SpaceId = OtherSpaceId, DeploymentId = deploymentId,
                Sequence = 0, Timestamp = DateTimeOffset.UtcNow, Level = "info", Message = "x",
            });
            seed.DeploymentStepOutcomes.Add(new DeploymentStepOutcome
            {
                SpaceId = OtherSpaceId, DeploymentId = deploymentId,
                StepIndex = 0, StepName = "s", Outcome = StepOutcomeKind.Succeeded,
                AttemptCount = 1, CompletedUtc = DateTimeOffset.UtcNow,
            });
            seed.DeploymentOutputVariables.Add(new DeploymentOutputVariable
            {
                SpaceId = OtherSpaceId, DeploymentId = deploymentId,
                StepName = "s", Name = "k", Value = "v", CapturedUtc = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        // Default-Space context: the global filter hides all three.
        await using var db = postgres.CreateContext();
        (await db.DeploymentLogEntries.CountAsync(l => l.DeploymentId == deploymentId))
            .Should().Be(0);
        (await db.DeploymentStepOutcomes.CountAsync(o => o.DeploymentId == deploymentId))
            .Should().Be(0);
        (await db.DeploymentOutputVariables.CountAsync(v => v.DeploymentId == deploymentId))
            .Should().Be(0);

        // …but they're present cross-Space and correctly stamped.
        (await db.DeploymentLogEntries.IgnoreQueryFilters()
            .CountAsync(l => l.DeploymentId == deploymentId && l.SpaceId == OtherSpaceId))
            .Should().Be(1);
    }

    /// <summary>
    /// Creates a Space + a minimal Project/Release/Environment/Deployment graph
    /// all stamped with <see cref="OtherSpaceId"/> (explicit, so the interceptor
    /// leaves them alone). Returns the deployment id.
    /// </summary>
    private async Task<Guid> SeedOtherSpaceDeploymentAsync()
    {
        await using var db = postgres.CreateContext();

        if (!await db.Spaces.IgnoreQueryFilters().AnyAsync(s => s.Id == OtherSpaceId))
        {
            db.Spaces.Add(new Space
            {
                Id = OtherSpaceId,
                Slug = "other-space",
                Name = "Other Space",
            });
        }

        var project = new Project
        {
            SpaceId = OtherSpaceId, Name = "other-proj", Slug = $"other-proj-{Guid.NewGuid():N}",
        };
        var env = new DeploymentEnvironment
        {
            SpaceId = OtherSpaceId, Name = "other-env", Slug = $"other-env-{Guid.NewGuid():N}", SortOrder = 1,
        };
        db.Projects.Add(project);
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId = OtherSpaceId, ProjectId = project.Id, Version = "1.0.0",
            ProcessSnapshot = [], VariableSnapshot = [], VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        var deployment = new Deployment
        {
            SpaceId = OtherSpaceId, ReleaseId = release.Id, EnvironmentId = env.Id,
            Status = DeploymentStatus.Succeeded,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        return deployment.Id;
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        public Task<string> SaveAsync(
            Guid deploymentId, string stepName, string fileName, Stream content,
            CancellationToken ct = default)
            => Task.FromResult($"{deploymentId}/{stepName}/{fileName}");

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public void Delete(string storedPath) { }
    }
}
