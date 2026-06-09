using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Contracts.Offline;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// P4c — the offline bundle carries the SAME <see cref="DeploymentPlan"/> the
/// online path dispatches, AES-GCM-encrypted as <c>plan.enc</c>, plus the
/// step-handler archives it pins. Pins the encrypt→decrypt roundtrip and the
/// embedding so the offline runner can reconstruct + execute the plan.
/// </summary>
[Collection("Postgres")]
public sealed class DropBundlePlanTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string DevMasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private DropBundleService NewSvc()
        => new(postgres, new NoopPackageStore(),
               new AesEncryptionService(DevMasterKey),
               NullLogger<DropBundleService>.Instance);

    [Fact]
    public async Task Bundle_carries_encrypted_plan_that_roundtrips_and_embeds_step_package()
    {
        var bundleKey = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var deploymentId = Guid.NewGuid();
        var plan = new DeploymentPlan(
            DeploymentId: deploymentId,
            EnvironmentName: "Production",
            Steps:
            [
                new DeploymentStepPlan(
                    Index: 0,
                    Name: "Run script",
                    StepType: "Kraken.Script",
                    PackageId: "",
                    PackageVersion: "",
                    Config: new Dictionary<string, string> { ["Octopus.Action.Script.ScriptBody"] = "Write-Host 'hi'" },
                    StepPackageName: "kraken.script",
                    StepPackageVersion: "1.0.0"),
            ],
            Variables: new Dictionary<string, string> { ["Greeting"] = "Hello" },
            ArrayVariables: new Dictionary<string, string[]>());

        var deployment = BuildDeployment(deploymentId);

        // A fake step-package archive on disk for the resolver to point at.
        var archiveDir = Path.Combine(Path.GetTempPath(), "kraken-steppkg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveDir);
        var archivePath = Path.Combine(archiveDir, "package.kdeploy-step");
        await File.WriteAllTextAsync(archivePath, "dummy-archive-bytes");

        var dataPath = Path.Combine(Path.GetTempPath(), "kraken-drop-plan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            var rel = await NewSvc().GenerateAsync(
                deployment, plan, bundleKey,
                stepPackageArchivePath: (n, v) => n == "kraken.script" && v == "1.0.0" ? archivePath : null,
                dataPath);

            using var zip = ZipFile.OpenRead(DropBundleService.GetFullPath(rel, dataPath));

            // plan.enc decrypts back to the exact plan.
            var enc = ReadEntry(zip, OfflineBundleLayout.EncryptedPlanFile);
            var planJson = AesGcmCipher.Decrypt(bundleKey, enc);
            var roundtripped = JsonSerializer.Deserialize<DeploymentPlan>(planJson, Web)!;

            roundtripped.DeploymentId.Should().Be(deploymentId);
            roundtripped.EnvironmentName.Should().Be("Production");
            roundtripped.Steps.Should().ContainSingle();
            roundtripped.Steps[0].Name.Should().Be("Run script");
            roundtripped.Steps[0].Config["Octopus.Action.Script.ScriptBody"].Should().Be("Write-Host 'hi'");
            roundtripped.Variables["Greeting"].Should().Be("Hello");

            // Step-handler archive embedded under step-packages/.
            zip.GetEntry("step-packages/kraken.script/1.0.0/package.kdeploy-step")
               .Should().NotBeNull();

            // Manifest is the new plan-based format and leaks no script config.
            var manifest = JsonSerializer.Deserialize<JsonElement>(ReadEntry(zip, "manifest.json"));
            manifest.GetProperty("bundleFormat").GetInt32().Should().Be(DropBundleService.BundleFormat);
            ReadEntry(zip, "manifest.json").Should().NotContain("ScriptBody");
        }
        finally
        {
            TryDelete(dataPath);
            TryDelete(archiveDir);
        }
    }

    [Fact]
    public async Task Wrong_key_fails_to_decrypt_plan()
    {
        var bundleKey = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var deploymentId = Guid.NewGuid();
        var plan = new DeploymentPlan(deploymentId, "Production",
            [new DeploymentStepPlan(0, "S", "Kraken.Script", "", "",
                new Dictionary<string, string>(), StepPackageName: "kraken.script", StepPackageVersion: "1.0.0")],
            new Dictionary<string, string>(), new Dictionary<string, string[]>());

        var archiveDir = Path.Combine(Path.GetTempPath(), "kraken-steppkg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveDir);
        var archivePath = Path.Combine(archiveDir, "package.kdeploy-step");
        await File.WriteAllTextAsync(archivePath, "x");

        var dataPath = Path.Combine(Path.GetTempPath(), "kraken-drop-plan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            var rel = await NewSvc().GenerateAsync(
                BuildDeployment(deploymentId), plan, bundleKey,
                (_, _) => archivePath, dataPath);
            using var zip = ZipFile.OpenRead(DropBundleService.GetFullPath(rel, dataPath));
            var enc = ReadEntry(zip, OfflineBundleLayout.EncryptedPlanFile);

            var wrongKey = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
            var act = () => AesGcmCipher.Decrypt(wrongKey, enc);
            act.Should().Throw<CryptographicException>();
        }
        finally
        {
            TryDelete(dataPath);
            TryDelete(archiveDir);
        }
    }

    [Fact]
    public async Task Embeds_bootstrap_readme_and_staged_runner()
    {
        var bundleKey = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var deploymentId = Guid.NewGuid();
        var plan = new DeploymentPlan(deploymentId, "Production",
            [new DeploymentStepPlan(0, "S", "Kraken.Script", "", "",
                new Dictionary<string, string>(), StepPackageName: "kraken.script", StepPackageVersion: "1.0.0")],
            new Dictionary<string, string>(), new Dictionary<string, string[]>());

        var archiveDir = Path.Combine(Path.GetTempPath(), "kraken-steppkg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveDir);
        var archivePath = Path.Combine(archiveDir, "package.kdeploy-step");
        await File.WriteAllTextAsync(archivePath, "x");

        // A staged "self-contained runner" (just a couple of fake files).
        var stageDir = Path.Combine(Path.GetTempPath(), "kraken-runner-stage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageDir);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "KrakenDeploy.Agent.exe"), "fake-exe");
        await File.WriteAllTextAsync(Path.Combine(stageDir, "KrakenDeploy.Agent.dll"), "fake-dll");

        var dataPath = Path.Combine(Path.GetTempPath(), "kraken-drop-plan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            var rel = await NewSvc().GenerateAsync(
                BuildDeployment(deploymentId), plan, bundleKey, (_, _) => archivePath,
                dataPath, runnerStageDir: stageDir);
            using var zip = ZipFile.OpenRead(DropBundleService.GetFullPath(rel, dataPath));

            zip.GetEntry("run.cmd").Should().NotBeNull();
            zip.GetEntry("run.sh").Should().NotBeNull();
            zip.GetEntry("README.txt").Should().NotBeNull();
            zip.GetEntry("runner/KrakenDeploy.Agent.exe").Should().NotBeNull();
            zip.GetEntry("runner/KrakenDeploy.Agent.dll").Should().NotBeNull();
        }
        finally
        {
            TryDelete(dataPath);
            TryDelete(archiveDir);
            TryDelete(stageDir);
        }
    }

    [Fact]
    public async Task No_staged_runner_still_writes_bootstrap_without_runner_dir()
    {
        var bundleKey = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var deploymentId = Guid.NewGuid();
        var plan = new DeploymentPlan(deploymentId, "Production",
            [new DeploymentStepPlan(0, "S", "Kraken.Script", "", "",
                new Dictionary<string, string>(), StepPackageName: "kraken.script", StepPackageVersion: "1.0.0")],
            new Dictionary<string, string>(), new Dictionary<string, string[]>());

        var archiveDir = Path.Combine(Path.GetTempPath(), "kraken-steppkg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveDir);
        await File.WriteAllTextAsync(Path.Combine(archiveDir, "package.kdeploy-step"), "x");

        var dataPath = Path.Combine(Path.GetTempPath(), "kraken-drop-plan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            // runnerStageDir points at a non-existent dir → embedding skipped.
            var rel = await NewSvc().GenerateAsync(
                BuildDeployment(deploymentId), plan, bundleKey,
                (_, _) => Path.Combine(archiveDir, "package.kdeploy-step"),
                dataPath, runnerStageDir: Path.Combine(dataPath, "no-runner"));
            using var zip = ZipFile.OpenRead(DropBundleService.GetFullPath(rel, dataPath));

            zip.GetEntry("run.cmd").Should().NotBeNull();
            zip.GetEntry("run.sh").Should().NotBeNull();
            zip.Entries.Should().NotContain(e => e.FullName.StartsWith("runner/", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(dataPath);
            TryDelete(archiveDir);
        }
    }

    [Fact]
    public async Task Missing_step_package_archive_is_fatal()
    {
        var bundleKey = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var deploymentId = Guid.NewGuid();
        var plan = new DeploymentPlan(deploymentId, "Production",
            [new DeploymentStepPlan(0, "S", "Kraken.Script", "", "",
                new Dictionary<string, string>(), StepPackageName: "kraken.script", StepPackageVersion: "1.0.0")],
            new Dictionary<string, string>(), new Dictionary<string, string[]>());

        var dataPath = Path.Combine(Path.GetTempPath(), "kraken-drop-plan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            // Resolver returns null → no archive on the server.
            var act = async () => await NewSvc().GenerateAsync(
                BuildDeployment(deploymentId), plan, bundleKey, (_, _) => null, dataPath);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            TryDelete(dataPath);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name)
            ?? throw new InvalidOperationException($"Bundle is missing '{name}'.");
        using var r = new StreamReader(entry.Open());
        return r.ReadToEnd();
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static Deployment BuildDeployment(Guid deploymentId)
    {
        var project = new Project { Slug = "proj", Name = "Proj", SpaceId = Guid.NewGuid() };
        var release = new Release { ProjectId = project.Id, Project = project, Version = "1.0.0" };
        var env = new DeploymentEnvironment { Name = "Production", Slug = "production" };
        var target = new DeploymentTarget { Name = "OfflineBox", TransportMode = TransportMode.OfflineDrop };
        return new Deployment
        {
            Id = deploymentId,
            ReleaseId = release.Id,
            Release = release,
            EnvironmentId = env.Id,
            Environment = env,
            TargetId = target.Id,
            Target = target,
        };
    }

    private sealed class NoopPackageStore : IPackageStore
    {
        public Task<string> StoreAsync(string packageId, string version, string fileName, Stream content, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct)
            => throw new NotSupportedException();
        public string GetFullPath(string storedPath) => storedPath;
        public Task DeleteAsync(string storedPath, CancellationToken ct) => Task.CompletedTask;
    }
}
