using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Cli.Commands;
using KrakenDeploy.Contracts.StepPackages;

namespace KrakenDeploy.Cli.Tests;

/// <summary>
/// Tests for the <c>kraken pack</c> verb. The build leg is exercised in the
/// authoring guide's smoke test — here we focus on the signing leg (which
/// is the only thing <c>kraken pack</c> adds on top of <c>dotnet build</c>).
/// We stage a fake <c>.kdeploy-step</c>, sign it with a fresh RSA key, then
/// re-verify with <see cref="StepPackageSigner.Verify"/> to prove the
/// canonical recipe round-trips.
/// </summary>
public sealed class PackCommandsTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"kraken-pack-test-{Guid.NewGuid():N}");

    public PackCommandsTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SignArchive_round_trips_through_StepPackageSigner_Verify()
    {
        var archivePath = StageFakeArchive(out var executorBytes);
        var pemPath     = StagePrivateKeyPem(out var rsa);
        try
        {
            PackCommands.SignArchive(archivePath, archivePath, pemPath);

            var (signedManifest, dllOnDisk) = ReadManifestAndStageExecutor(archivePath);
            var verify = StepPackageSigner.Verify(signedManifest, dllOnDisk, rsa);

            verify.IsValid.Should().BeTrue(verify.Reason);
            signedManifest.Signature.Should().NotBe("unsigned-dev-build",
                "the dev sentinel must be replaced by the real RSA signature");
            File.ReadAllBytes(dllOnDisk).Should().Equal(executorBytes,
                "signing must not touch the executor DLL — only manifest.json gets rewritten");
        }
        finally
        {
            rsa.Dispose();
        }
    }

    [Fact]
    public void SignArchive_writes_to_explicit_output_path_when_different_from_input()
    {
        var archivePath = StageFakeArchive(out _);
        var pemPath     = StagePrivateKeyPem(out var rsa);
        var outPath     = Path.Combine(_workspace, "signed-copy.kdeploy-step");
        try
        {
            PackCommands.SignArchive(archivePath, outPath, pemPath);

            File.Exists(outPath).Should().BeTrue();
            File.Exists(archivePath).Should().BeTrue(
                "the source archive must remain intact when --output is set");

            var sourceManifest = ReadManifestOnly(archivePath);
            var destManifest   = ReadManifestOnly(outPath);
            sourceManifest.Signature.Should().Be("unsigned-dev-build",
                "the input archive must NOT be re-written when --output is set");
            destManifest.Signature.Should().NotBe("unsigned-dev-build");
        }
        finally
        {
            rsa.Dispose();
        }
    }

    [Fact]
    public void SignArchive_throws_FileNotFound_for_missing_archive()
    {
        var pemPath = StagePrivateKeyPem(out var rsa);
        try
        {
            var act = () => PackCommands.SignArchive(
                Path.Combine(_workspace, "does-not-exist.kdeploy-step"),
                Path.Combine(_workspace, "ignored.kdeploy-step"),
                pemPath);

            act.Should().Throw<FileNotFoundException>();
        }
        finally
        {
            rsa.Dispose();
        }
    }

    [Fact]
    public void SignArchive_throws_FileNotFound_for_missing_pem()
    {
        var archivePath = StageFakeArchive(out _);
        var act = () => PackCommands.SignArchive(
            archivePath, archivePath,
            Path.Combine(_workspace, "no-such-key.pem"));

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void SignArchive_throws_InvalidData_when_manifest_is_missing()
    {
        var archivePath = Path.Combine(_workspace, "no-manifest.kdeploy-step");
        using (var fs = File.Create(archivePath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // Executor present but no manifest.json at the zip root.
            var entry = zip.CreateEntry("executor/Fake.dll");
            using var es = entry.Open();
            es.Write([0x4D, 0x5A, 0x00, 0x00]);
        }
        var pemPath = StagePrivateKeyPem(out var rsa);
        try
        {
            var act = () => PackCommands.SignArchive(archivePath, archivePath, pemPath);
            act.Should().Throw<InvalidDataException>()
                .WithMessage($"*{StepPackageFiles.ManifestFileName}*");
        }
        finally
        {
            rsa.Dispose();
        }
    }

    [Fact]
    public async Task RunAsync_returns_user_error_when_input_missing()
    {
        var fakeInput = new FileInfo(Path.Combine(_workspace, "ghost.kdeploy-step"));
        var exit = await PackCommands.RunAsync(fakeInput, keyFile: null, output: null, "Release");
        exit.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_returns_user_error_for_unknown_extension()
    {
        var weird = Path.Combine(_workspace, "thing.zip");
        File.WriteAllBytes(weird, [0x50, 0x4B]); // PK header but wrong extension
        var exit = await PackCommands.RunAsync(new FileInfo(weird), keyFile: null, output: null, "Release");
        exit.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_returns_zero_for_existing_archive_without_key()
    {
        var archivePath = StageFakeArchive(out _);
        var exit = await PackCommands.RunAsync(
            new FileInfo(archivePath), keyFile: null, output: null, "Release");
        exit.Should().Be(0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string StageFakeArchive(out byte[] executorBytes)
    {
        // Arbitrary "executor" payload — only its SHA-256 matters for signing.
        executorBytes = [.. Enumerable.Range(0, 1024).Select(i => (byte)(i % 251))];

        var manifest = new StepPackageManifest
        {
            Id               = "kraken.test.pack",
            Version          = "1.0.0",
            DisplayName      = "Pack-command test",
            TargetFramework  = "net10.0",
            StepTypes        = ["Kraken.Test.Pack"],
            ExecutorAssembly = "Test.Executor.dll",
            ExecutorTypeName = "Test.Executor.SomeHandler",
            Signature        = "unsigned-dev-build",
            SignedBy         = "kraken-project",
        };

        var archivePath = Path.Combine(_workspace, "kraken.test.pack-1.0.0.kdeploy-step");
        using var fs  = File.Create(archivePath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var manifestEntry = zip.CreateEntry(StepPackageFiles.ManifestFileName);
        using (var w = new StreamWriter(manifestEntry.Open()))
        {
            w.Write(StepPackageManifestJson.Serialize(manifest));
        }

        var executorEntry = zip.CreateEntry(
            $"{StepPackageFiles.ExecutorDirectory}/{manifest.ExecutorAssembly}");
        using (var es = executorEntry.Open())
        {
            es.Write(executorBytes);
        }
        return archivePath;
    }

    private string StagePrivateKeyPem(out RSA rsa)
    {
        rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var path = Path.Combine(_workspace, "signing.key");
        File.WriteAllText(path, pem);
        return path;
    }

    private (StepPackageManifest manifest, string executorDllOnDisk) ReadManifestAndStageExecutor(
        string archivePath)
    {
        using var read     = ZipFile.OpenRead(archivePath);
        var manifestEntry  = read.GetEntry(StepPackageFiles.ManifestFileName)!;
        using var ms       = new StreamReader(manifestEntry.Open());
        var manifest       = StepPackageManifestJson.Deserialize(ms.ReadToEnd());

        var dllOnDisk = Path.Combine(_workspace, "verify-staged-" + manifest.ExecutorAssembly);
        var executorEntry = read.GetEntry(
            $"{StepPackageFiles.ExecutorDirectory}/{manifest.ExecutorAssembly}")!;
        using var dst = File.Create(dllOnDisk);
        using var src = executorEntry.Open();
        src.CopyTo(dst);
        return (manifest, dllOnDisk);
    }

    private static StepPackageManifest ReadManifestOnly(string archivePath)
    {
        using var read = ZipFile.OpenRead(archivePath);
        var entry      = read.GetEntry(StepPackageFiles.ManifestFileName)!;
        using var sr   = new StreamReader(entry.Open());
        return StepPackageManifestJson.Deserialize(sr.ReadToEnd());
    }
}
