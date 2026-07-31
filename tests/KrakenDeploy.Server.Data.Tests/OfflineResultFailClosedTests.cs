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
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Rejects_a_result_for_a_deployment_cancelled_mid_ingest()
    {
        // B5 (T1-1): the ingest's verify/extract phase is a long window between
        // the up-front PendingOfflineResult check and the final write. A cancel
        // landing inside it must reject the upload — the old shape resurrected
        // the cancelled deployment to a terminal Succeeded/Failed and persisted
        // the ingested children. The wrapper stream flips the row to Cancelled
        // on the FIRST read, which is after IngestAsync's load + status check
        // (the bundle copy is the first thing that touches the stream) — i.e.
        // exactly inside the old race window.
        var crypto = TestCrypto.Service(MasterKey);
        var id = await SeedPendingAsync(crypto, withBundleKey: true);
        var service = NewService(crypto);

        var cancelStamp = DateTimeOffset.UtcNow;
        await using var inner = BuildBundle(id, sign: true, tamper: false, steps:
        [
            new OfflineStepResult { StepIndex = 0, StepName = "s1", Success = true },
        ]);
        await using var bundle = new CancelOnFirstReadStream(inner, async () =>
        {
            await using var db = postgres.CreateContext();
            await db.Deployments
                .Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DeploymentStatus.Cancelled)
                    .SetProperty(d => d.CompletedUtc, cancelStamp));
        });

        var act = () => service.IngestAsync(id, bundle);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not 'PendingOfflineResult'*");

        await using var verify = postgres.CreateContext();
        var dep = await verify.Deployments.FirstAsync(d => d.Id == id);
        dep.Status.Should().Be(DeploymentStatus.Cancelled,
            "the cancel is the recorded verdict — a result upload must not resurrect the deployment");
        dep.CompletedUtc!.Value.Should().BeCloseTo(cancelStamp, TimeSpan.FromMilliseconds(1));
        (await verify.TaskStepOutcomes.CountAsync(o => o.TaskId == id))
            .Should().Be(0, "a rejected ingest must persist nothing");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private OfflineResultService NewService(AesEncryptionService crypto) =>
        new(postgres,
            new NoopArtifactStore(),
            crypto,
            RetentionTestFactory.NewService(postgres),
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

    private static MemoryStream BuildBundle(
        Guid deploymentId, bool sign, bool tamper, List<OfflineStepResult>? steps = null)
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
                DeploymentId = deploymentId, Success = true, CompletedUtc = DateTimeOffset.UtcNow,
                Steps = steps ?? [],
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

/// <summary>
/// B5 test seam: fires <c>onFirstRead</c> exactly once, before the first byte
/// is served. <c>IngestAsync</c> copies the uploaded bundle right after its
/// deployment load + status check, so the callback lands deterministically
/// inside the old check-then-write race window.
/// </summary>
file sealed class CancelOnFirstReadStream(Stream inner, Func<Task> onFirstRead) : Stream
{
    private bool _fired;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await FireOnceAsync().ConfigureAwait(false);
        return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        FireOnceAsync().GetAwaiter().GetResult();
        return inner.Read(buffer, offset, count);
    }

    private async Task FireOnceAsync()
    {
        if (_fired)
        {
            return;
        }
        _fired = true;
        await onFirstRead().ConfigureAwait(false);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
