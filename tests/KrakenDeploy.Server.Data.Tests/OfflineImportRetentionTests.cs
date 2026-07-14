using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Contracts.Offline;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.ArtifactStorage;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Regression: retention must also run on the OFFLINE-IMPORT completion path.
/// Offline-drop deployments are created online, exported, run on an air-gapped
/// box, and reconciled by <see cref="OfflineResultService.IngestAsync"/> — which
/// finalises the deployment WITHOUT going through the online orchestrator or the
/// AgentHub trigger. Before the fix, offline-drop deployments and their logs
/// therefore accumulated unbounded. This ingests a minimal successful result
/// bundle and asserts an older over-the-keep deployment is pruned.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OfflineImportRetentionTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Offline_import_success_prunes_old_deployments_beyond_keep()
    {
        Guid pendingId, oldId;
        await using (var db = postgres.CreateContext())
        {
            var env = new DeploymentEnvironment
            {
                SpaceId = WellKnown.DefaultSpaceId,
                Name = $"oie-{Guid.NewGuid():N}"[..12], Slug = $"oie-{Guid.NewGuid():N}"[..12],
                SortOrder = 1,
            };
            db.Environments.Add(env);
            await db.SaveChangesAsync();

            var lifecycle = new Lifecycle
            {
                SpaceId = WellKnown.DefaultSpaceId,
                Name    = "offline-ret-lc",
                Phases  = [new LifecyclePhase
                {
                    Name = "P", EnvironmentIds = [env.Id], RetentionKeepDeployments = 1,
                }],
            };
            db.Lifecycles.Add(lifecycle);
            await db.SaveChangesAsync();

            var project = new Project
            {
                SpaceId        = WellKnown.DefaultSpaceId,
                Name           = $"oip-{Guid.NewGuid():N}"[..12], Slug = $"oip-{Guid.NewGuid():N}"[..12],
                LifecycleId    = lifecycle.Id,
                ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var release = new Release
            {
                SpaceId                    = WellKnown.DefaultSpaceId,
                ProjectId                  = project.Id,
                Version                    = "1.0",
                ProcessSnapshot            = [],
                VariableSnapshot           = [],
                VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
            };
            db.Releases.Add(release);

            // Offline-drop target with NO OfflineDropConfig -> ingest skips the HMAC
            // + result-signature checks, so a minimal unsigned bundle is accepted.
            var target = new DeploymentTarget
            {
                SpaceId       = WellKnown.DefaultSpaceId,
                Name          = $"oit-{Guid.NewGuid():N}"[..12],
                Roles         = ["web"],
                TransportMode = TransportMode.OfflineDrop,
                Status        = TargetStatus.Unknown,
            };
            db.DeploymentTargets.Add(target);
            await db.SaveChangesAsync();

            var old = new Deployment
            {
                SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, ReleaseId = release.Id,
                EnvironmentId = env.Id, Status = DeploymentStatus.Succeeded,
                CompletedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            };
            var pending = new Deployment
            {
                SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, ReleaseId = release.Id,
                EnvironmentId = env.Id, Status = DeploymentStatus.PendingOfflineResult,
            };
            db.Deployments.AddRange(old, pending);
            await db.SaveChangesAsync();

            db.TaskTargetAssignments.Add(new TaskTargetAssignment
            {
                SpaceId = WellKnown.DefaultSpaceId, TaskId = pending.Id, TargetId = target.Id,
                AddedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            pendingId = pending.Id;
            oldId     = old.Id;
        }

        var service = new OfflineResultService(
            postgres,
            new UnusedArtifactStore(),
            TestCrypto.Service(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            new RetentionService(postgres, new DefaultSpaceContext(), NullLogger<RetentionService>.Instance),
            NullLogger<OfflineResultService>.Instance);

        await using var bundle = BuildSuccessBundle(pendingId);
        var ingested = await service.IngestAsync(pendingId, bundle);

        ingested.Status.Should().Be(DeploymentStatus.Succeeded);

        await using var check = postgres.CreateContext();
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == oldId))
            .Should().BeFalse(
                "offline import success must trigger lifecycle retention (keep=1) and prune the " +
                "older successful deployment — the offline path finalises outside the orchestrator/AgentHub");
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == pendingId))
            .Should().BeTrue("the just-imported deployment is retained as the newest");
    }

    /// <summary>Smallest bundle IngestAsync accepts: a manifest carrying the current
    /// bundle format + a successful, step-less result. No signatures (the target has
    /// no keys), no log, no artifacts.</summary>
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static MemoryStream BuildSuccessBundle(Guid deploymentId)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            {
                w.Write($"{{\"bundleFormat\":{DropBundleService.BundleFormat}}}");
            }

            var result = new OfflineDropResult
            {
                DeploymentId = deploymentId,
                Success      = true,
                CompletedUtc = DateTimeOffset.UtcNow,
                Steps        = [],
            };
            using (var w = new StreamWriter(zip.CreateEntry(OfflineBundleLayout.ResultFile).Open()))
            {
                w.Write(JsonSerializer.Serialize(result, WebJson));
            }
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>The minimal bundle has no artifacts, so the store is never touched.</summary>
    private sealed class UnusedArtifactStore : IArtifactStore
    {
        public Task<string> SaveAsync(
            Guid deploymentId, string stepName, string fileName, Stream content, CancellationToken ct = default)
            => throw new NotSupportedException("bundle has no artifacts");

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Delete(string storedPath) => throw new NotSupportedException();
    }
}
