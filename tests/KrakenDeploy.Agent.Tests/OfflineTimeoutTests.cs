using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Agent.Offline;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Contracts.Offline;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Regression guard for the per-step <c>TimeoutSeconds</c> reporting bug: a step
/// that honours its cancellation token and exceeds its timeout must surface as a
/// TIMEOUT — the agent's offline orchestrate path emits
/// <c>--- Step 'X' timed out after Ns ---</c> — not as a generic handler failure.
/// <para>
/// Before the fix, <c>DeploymentExecutor.ExecuteStepAsync</c>'s handler-body
/// <c>catch (Exception)</c> swallowed the <see cref="OperationCanceledException"/>
/// raised by the per-attempt linked-CTS timeout and returned <c>false</c>, so
/// <c>StepRetryRunner</c> never saw the timeout and the offline log showed only
/// "Step handler threw an unhandled exception" / "Step 'X' failed." This runs the
/// REAL <see cref="DeploymentExecutor"/> through <see cref="OfflineRunner"/>
/// (orchestrate mode) against a handler that blocks on its token until cancelled.
/// </para>
/// </summary>
public sealed class OfflineTimeoutTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _bundle =
        Path.Combine(Path.GetTempPath(), $"kraken-timeout-{Guid.NewGuid():N}");

    public OfflineTimeoutTests() => Directory.CreateDirectory(_bundle);

    [Fact]
    public async Task Step_exceeding_TimeoutSeconds_is_reported_as_timed_out_offline()
    {
        var key = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var deploymentId = Guid.NewGuid();

        BuildHandlerArchive(Path.Combine(
            _bundle, "step-packages", "kraken.blocking", "1.0.0", "package.kdeploy-step"));

        // A single Required step whose handler blocks on its token. With
        // TimeoutSeconds=1 and no retries, the per-attempt linked CTS cancels the
        // token after 1s; the handler propagates the OCE, which StepRetryRunner must
        // classify as a timeout.
        var slow = new DeploymentStepPlan(
            0, "SlowStep", "Kraken.BlockingTest", "", "",
            new Dictionary<string, string>(),
            StepPackageName: "kraken.blocking", StepPackageVersion: "1.0.0",
            TimeoutSeconds: 1);

        var plan = new DeploymentPlan(
            deploymentId, "Production", [slow],
            new Dictionary<string, string>(), new Dictionary<string, string[]>());

        File.WriteAllText(
            Path.Combine(_bundle, OfflineBundleLayout.EncryptedPlanFile),
            AesGcmCipher.Encrypt(key, JsonSerializer.Serialize(plan, Web)));

        var exit = await new OfflineRunner(NullLoggerFactory.Instance).RunAsync(_bundle, key);

        // A Required step that timed out fails the deployment.
        exit.Should().NotBe(0);

        var result = JsonSerializer.Deserialize<OfflineDropResult>(
            await File.ReadAllTextAsync(Path.Combine(_bundle, OfflineBundleLayout.ResultFile)), Web)!;
        result.Success.Should().BeFalse();

        var log = await File.ReadAllTextAsync(Path.Combine(_bundle, OfflineBundleLayout.LogFile));
        log.Should().Contain("timed out after 1s",
            "the per-step TimeoutSeconds must surface as a timeout via " +
            "StepRetryRunner's onAttemptTimedOut callback");
        log.Should().NotContain("Step handler threw an unhandled exception",
            "the timeout's OperationCanceledException must NOT be mislabelled as a " +
            "generic handler failure (the bug this guards against)");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static void BuildHandlerArchive(string destPath)
    {
        var manifest = new StepPackageManifest
        {
            Id = "kraken.blocking",
            Version = "1.0.0",
            DisplayName = "Blocking test",
            TargetFramework = "net10.0",
            StepTypes = ["Kraken.BlockingTest"],
            ExecutorAssembly = typeof(BlockingTestHandler).Assembly.GetName().Name + ".dll",
            ExecutorTypeName = typeof(BlockingTestHandler).FullName!,
            Signature = "unsigned-dev-build",
            SignedBy = "kraken-project",
        };

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var fs = File.Create(destPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        var manifestEntry = zip.CreateEntry(StepPackageFiles.ManifestFileName);
        using (var w = new StreamWriter(manifestEntry.Open()))
        {
            w.Write(StepPackageManifestJson.Serialize(manifest));
        }

        var asm = typeof(BlockingTestHandler).Assembly.Location;
        var exEntry = zip.CreateEntry($"{StepPackageFiles.ExecutorDirectory}/{Path.GetFileName(asm)}");
        using var es = exEntry.Open();
        using var src = File.OpenRead(asm);
        src.CopyTo(es);
    }

    public void Dispose()
    {
        try { Directory.Delete(_bundle, recursive: true); }
        catch { /* best effort */ }
    }
}

/// <summary>
/// Shell-free test step handler that blocks on its cancellation token forever —
/// the minimal faithful model of "a step that honours cancellation but outlives
/// its TimeoutSeconds." <see cref="Task.Delay(int, CancellationToken)"/> throws
/// <see cref="OperationCanceledException"/> when the per-attempt token cancels.
/// Lives in the test assembly so the archive builder can pack it as the
/// step-package executor.
/// </summary>
public sealed class BlockingTestHandler : IStepHandler
{
    public bool CanHandle(string stepType) => stepType == "Kraken.BlockingTest";

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return true; // unreachable — the token always cancels first
    }
}
