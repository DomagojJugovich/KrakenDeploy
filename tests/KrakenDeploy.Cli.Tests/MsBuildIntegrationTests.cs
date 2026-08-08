using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;

namespace KrakenDeploy.Cli.Tests;

/// <summary>
/// End-to-end test for the Phase D-12.5 MSBuild signing integration.
/// Spawns a real <c>dotnet build</c> on the in-repo Manual step package
/// with <c>-p:KrakenSigningKey=&lt;pem&gt;</c>, then verifies the produced
/// archive's manifest carries a real RSA-SHA256 signature that the
/// matching public key validates.
/// <para>
/// Touches the in-tree <c>steps/KrakenDeploy.Steps.Manual/bin/</c> output,
/// which is fine: every subsequent plain <c>dotnet build</c> of that
/// project regenerates manifest.json from scratch and emits the dev
/// sentinel, so no state leaks across test runs.
/// </para>
/// </summary>
public sealed class MsBuildIntegrationTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"kraken-msbuild-test-{Guid.NewGuid():N}");

    public MsBuildIntegrationTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    [Fact(Timeout = 180_000)] // generous: cold dotnet build of a step package + cli invocation
    public async Task Build_with_KrakenSigningKey_signs_the_archive_in_place()
    {
        // The in-repo Manual project — smallest step package, builds in ~1s.
        var manualProject = FindRepoFile(
            Path.Combine("steps", "KrakenDeploy.Steps.Manual",
                         "KrakenDeploy.Steps.Manual.csproj"));
        var manualArchive = ExpectedArchivePath(manualProject);

        // Generate a fresh key + write the PEM the targets file will hand
        // to `kraken pack --key`.
        using var rsa = RSA.Create(2048);
        var pemPath = Path.Combine(_workspace, "signing.key");
        await File.WriteAllTextAsync(pemPath, rsa.ExportRSAPrivateKeyPem());

        // Drive `dotnet build -p:KrakenSigningKey=...`. The CLI is already
        // built (this test project's project-reference chain ensures
        // src/KrakenDeploy.Cli/bin/Debug/net10.0/kraken.dll exists before
        // this test runs), so the targets file's _KrakenCliInRepoDebug
        // path resolves and the Exec invokes the local CLI build.
        var (exitCode, output) = await RunDotnetBuildAsync(manualProject, pemPath);

        exitCode.Should().Be(0,
            "the MSBuild signing target must complete cleanly — output:\n{0}", output);

        File.Exists(manualArchive).Should().BeTrue(
            "the pack target should have produced the archive before the sign target ran");

        // Open the archive + check the signature. The dev sentinel
        // ("unsigned-dev-build") would mean the sign target was skipped.
        var (manifest, executorDll) = ReadManifestAndStageExecutor(manualArchive);
        manifest.Signature.Should().NotBe("unsigned-dev-build",
            "the sign target must replace the dev sentinel with a real RSA-SHA256 signature");
        manifest.Signature.Should().NotBeNullOrEmpty();

        var verify = StepPackageSigner.Verify(manifest, executorDll, rsa);
        verify.IsValid.Should().BeTrue(
            "the signature must validate against the same key we passed via " +
            "KrakenSigningKey; reason: {0}", verify.Reason);
    }

    [Fact(Timeout = 60_000)]
    public async Task Build_without_KrakenSigningKey_leaves_the_dev_sentinel_intact()
    {
        // The sign target's Condition gates on KrakenSigningKey being set;
        // without it the dev sentinel stays. This pins that the targets file
        // is a true no-op when the property is empty — critical for local
        // iteration where authors don't want CI to wire signing artifacts.
        var manualProject = FindRepoFile(
            Path.Combine("steps", "KrakenDeploy.Steps.Manual",
                         "KrakenDeploy.Steps.Manual.csproj"));
        var manualArchive = ExpectedArchivePath(manualProject);

        var (exitCode, output) = await RunDotnetBuildAsync(manualProject, pemPath: null);
        exitCode.Should().Be(0, output);

        var (manifest, _) = ReadManifestAndStageExecutor(manualArchive);
        manifest.Signature.Should().Be("unsigned-dev-build",
            "with KrakenSigningKey unset, the sign target must not touch manifest.json");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunDotnetBuildAsync(
        string project, string? pemPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "dotnet",
            WorkingDirectory       = Path.GetDirectoryName(project),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--nologo");
        if (pemPath is not null)
        {
            psi.ArgumentList.Add($"-p:KrakenSigningKey={pemPath}");
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start `dotnet build`.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var output = (await stdoutTask) + "\n" + (await stderrTask);
        return (proc.ExitCode, output);
    }

    /// <summary>
    /// Derives the archive path the pack target will produce from the
    /// project's own <c>KrakenStepPackageId</c>/<c>Version</c> properties —
    /// hardcoding a version here broke on every bump.
    /// </summary>
    private static string ExpectedArchivePath(string project)
    {
        var doc = System.Xml.Linq.XDocument.Load(project);
        var id      = doc.Descendants("KrakenStepPackageId").Single().Value.Trim();
        var version = doc.Descendants("KrakenStepPackageVersion").Single().Value.Trim();
        return Path.Combine(
            Path.GetDirectoryName(project)!,
            "bin", "Debug", "net10.0",
            $"{id}-{version}.kdeploy-step");
    }

    /// <summary>
    /// Locates a file relative to the repo root. Walks up from the test
    /// assembly's directory until <c>KrakenDeploy.sln</c> appears, then
    /// resolves <paramref name="relativePath"/> from there.
    /// </summary>
    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KrakenDeploy.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new FileNotFoundException(
                $"Could not locate KrakenDeploy.sln above {AppContext.BaseDirectory}");
        }
        return Path.Combine(dir.FullName, relativePath);
    }

    private (StepPackageManifest manifest, string executorDllOnDisk)
        ReadManifestAndStageExecutor(string archivePath)
    {
        using var read  = ZipFile.OpenRead(archivePath);
        var manifestEnt = read.GetEntry(StepPackageFiles.ManifestFileName)!;
        using var ms    = new StreamReader(manifestEnt.Open());
        var manifest    = StepPackageManifestJson.Deserialize(ms.ReadToEnd());

        var dllOnDisk   = Path.Combine(_workspace, "stage-" + manifest.ExecutorAssembly);
        var execEnt     = read.GetEntry(
            $"{StepPackageFiles.ExecutorDirectory}/{manifest.ExecutorAssembly}")!;
        using var dst   = File.Create(dllOnDisk);
        using var src   = execEnt.Open();
        src.CopyTo(dst);
        return (manifest, dllOnDisk);
    }
}
