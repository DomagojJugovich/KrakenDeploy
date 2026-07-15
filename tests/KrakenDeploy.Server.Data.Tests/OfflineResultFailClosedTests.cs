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
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// A8/T1-15 — the offline-result trust boundary. A result bundle returns over an
/// UNTRUSTED channel and drives status / step-outcome / output-variable DB writes,
/// so ingestion must fail CLOSED: an offline-drop target with no bundle key, a
/// bundle missing its signature, or a bad signature must all be rejected. The
/// correctly-keyed happy path still succeeds.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OfflineResultFailClosedTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static readonly string MasterKey = Convert.ToBase64String(new byte[32]);
    private static readonly byte[] BundleKey = SHA256.HashData("kraken-failclosed-bundle-key"u8.ToArray());

    [Fact]
    public async Task Rejects_offline_target_with_no_bundle_key()
    {
        var crypto = TestCrypto.Service(MasterKey);
        var id = await SeedPendingAsync(crypto, withBundleKey: false);
        var service = NewService(crypto);

        await using var bundle = BuildBundle(id, sign: true, tamper: false);
        var act = () => service.IngestAsync(id, bundle);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bundle signing key*");
    }

    [Fact]
    public async Task Rejects_missing_result_signature()
    {
        var crypto = TestCrypto.Service(MasterKey);
        var id = await SeedPendingAsync(crypto, withBundleKey: true);
        var service = NewService(crypto);

        await using var bundle = BuildBundle(id, sign: false, tamper: false);
        var act = () => service.IngestAsync(id, bundle);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*result-signature.bin*");
    }

    [Fact]
    public async Task Rejects_tampered_result()
    {
        var crypto = TestCrypto.Service(MasterKey);
        var id = await SeedPendingAsync(crypto, withBundleKey: true);
        var service = NewService(crypto);

        // Signature computed over the original bytes; the stored result is then altered.
        await using var bundle = BuildBundle(id, sign: true, tamper: true);
        var act = () => service.IngestAsync(id, bundle);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*signature verification failed*");
    }

    [Fact]
    public async Task Accepts_a_valid_signed_result()
    {
        var crypto = TestCrypto.Service(MasterKey);
        var id = await SeedPendingAsync(crypto, withBundleKey: true);
        var service = NewService(crypto);

        await using var bundle = BuildBundle(id, sign: true, tamper: false);
        var ingested = await service.IngestAsync(id, bundle);

        ingested.Status.Should().Be(DeploymentStatus.Succeeded);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private OfflineResultService NewService(AesEncryptionService crypto) =>
        new(postgres,
            new NoopArtifactStore(),
            crypto,
            new RetentionService(postgres, new DefaultSpaceContext(), NullLogger<RetentionService>.Instance),
            NullLogger<OfflineResultService>.Instance);

    private async Task<Guid> SeedPendingAsync(AesEncryptionService crypto, bool withBundleKey)
    {
        await using var db = postgres.CreateContext();

        var env = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"fce-{Guid.NewGuid():N}"[..12], Slug = $"fce-{Guid.NewGuid():N}"[..12],
            SortOrder = 1,
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var lifecycle = new Lifecycle
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = "fc-lc",
            Phases = [new LifecyclePhase { Name = "P", EnvironmentIds = [env.Id], RetentionKeepDeployments = 5 }],
        };
        db.Lifecycles.Add(lifecycle);
        await db.SaveChangesAsync();

        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"fcp-{Guid.NewGuid():N}"[..12], Slug = $"fcp-{Guid.NewGuid():N}"[..12],
            LifecycleId = lifecycle.Id,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, Version = "1.0",
            ProcessSnapshot = [], VariableSnapshot = [], VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);

        var target = new DeploymentTarget
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"fct-{Guid.NewGuid():N}"[..12],
            Roles = ["web"],
            TransportMode = TransportMode.OfflineDrop,
            Status = TargetStatus.Unknown,
            OfflineDropConfig = withBundleKey
                ? new OfflineDropConfig { BundleKeyEncrypted = crypto.Encrypt(Convert.ToBase64String(BundleKey)) }
                : new OfflineDropConfig(),
        };
        db.DeploymentTargets.Add(target);

        var pending = new Deployment
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, ReleaseId = release.Id,
            EnvironmentId = env.Id, Status = DeploymentStatus.PendingOfflineResult,
        };
        db.Deployments.Add(pending);
        await db.SaveChangesAsync();

        db.TaskTargetAssignments.Add(new TaskTargetAssignment
        {
            SpaceId = WellKnown.DefaultSpaceId, TaskId = pending.Id, TargetId = target.Id,
            AddedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return pending.Id;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static MemoryStream BuildBundle(Guid deploymentId, bool sign, bool tamper)
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
                DeploymentId = deploymentId, Success = true, CompletedUtc = DateTimeOffset.UtcNow, Steps = [],
            };
            var resultBytes = JsonSerializer.SerializeToUtf8Bytes(result, WebJson);

            // Sign the ORIGINAL bytes; if tampering, write altered bytes so the
            // stored result no longer matches the signature.
            var sig = OfflineResultSigner.Sign(BundleKey, resultBytes);
            var written = tamper
                ? JsonSerializer.SerializeToUtf8Bytes(
                    result with { CompletedUtc = result.CompletedUtc!.Value.AddSeconds(1) }, WebJson)
                : resultBytes;

            using (var s = zip.CreateEntry(OfflineBundleLayout.ResultFile).Open())
            {
                s.Write(written, 0, written.Length);
            }

            if (sign)
            {
                using var s = zip.CreateEntry(OfflineBundleLayout.ResultSignatureFile).Open();
                s.Write(sig, 0, sig.Length);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class NoopArtifactStore : IArtifactStore
    {
        public Task<string> SaveAsync(
            Guid deploymentId, string stepName, string fileName, Stream content, CancellationToken ct = default)
            => throw new NotSupportedException("bundle has no artifacts");

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Delete(string storedPath) => throw new NotSupportedException();
    }
}
