using System.Text.Json;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Contracts.Offline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Offline;

/// <summary>
/// Executes an offline drop bundle locally — no server, no agent registration.
/// Decrypts <c>plan.enc</c> with the target's bundle key and runs the SAME
/// <see cref="DeploymentExecutor"/> the online agent uses, wired to bundle-backed
/// ports (<see cref="FileSystemServerLink"/>, <see cref="BundlePackageSource"/>,
/// <see cref="FileArtifactSink"/>, <see cref="BundleStepPackageSource"/>). This
/// is the unification: a process author gets identical execution semantics —
/// waves, output-variable feed-forward, step-package handlers — online and
/// offline, because it's literally the same executor code.
/// </summary>
public sealed class OfflineRunner(ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs the bundle in <paramref name="bundleDir"/> using
    /// <paramref name="bundleKey"/> (the decrypted 32-byte per-target key).
    /// Returns a process exit code: 0 on success, 1 on a failed/aborted run, 2
    /// on a setup error (bad key, missing/parse-failed plan).
    /// </summary>
    public async Task<int> RunAsync(string bundleDir, byte[] bundleKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDir);
        ArgumentNullException.ThrowIfNull(bundleKey);
        var log = loggerFactory.CreateLogger<OfflineRunner>();

        DeploymentPlan plan;
        try
        {
            var planEncPath = Path.Combine(bundleDir, OfflineBundleLayout.EncryptedPlanFile);
            var planEnc = await File.ReadAllTextAsync(planEncPath, ct).ConfigureAwait(false);
            var planJson = AesGcmCipher.Decrypt(bundleKey, planEnc);
            plan = JsonSerializer.Deserialize<DeploymentPlan>(planJson, Web)
                ?? throw new InvalidOperationException("plan.enc decrypted to null.");
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Failed to load the deployment plan from '{Bundle}'. Wrong bundle key or corrupt bundle?",
                bundleDir);
            return 2;
        }

        // Executor work dir + step-package cache live under the bundle so the
        // run is self-contained and leaves a clean trail next to its inputs.
        var workDir = Path.Combine(bundleDir, ".runner");
        Directory.CreateDirectory(workDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:DataPath"] = workDir,
                // Step-package archives are sourced from the server's verified
                // install store and embedded in a bundle that is itself
                // integrity-protected (manifest HMAC + AES-GCM plan), so the
                // per-package signature re-check on load is redundant here.
                ["StepPackages:AllowUnsignedLoads"] = "true",
            })
            .Build();
        var agentConfig = Options.Create(new AgentConfig { DataPath = workDir });

        var serverLink = new FileSystemServerLink(bundleDir, plan.Steps, bundleKey);
        var packageSource = new BundlePackageSource(bundleDir);
        var artifactSink = new FileArtifactSink(bundleDir, loggerFactory.CreateLogger<FileArtifactSink>());

        // loader ↔ source cycle broken by the lazy closure (extract is only
        // invoked during ExecuteAsync, by which point loader is assigned).
        StepPackageLoader loader = null!;
        var stepSource = new BundleStepPackageSource(
            bundleDir,
            (name, version, archivePath) =>
            {
                loader.ExtractToCache(name, version, archivePath);
                return Task.CompletedTask;
            },
            loggerFactory.CreateLogger<BundleStepPackageSource>());
        loader = new StepPackageLoader(
            config, loggerFactory.CreateLogger<StepPackageLoader>(), stepSource);

        // A fresh gate per offline invocation: this process runs exactly one plan
        // and exits, so there is nothing to serialize against. A live agent on the
        // same box has its OWN gate in its own process — an offline run is
        // deliberately not coordinated with it (pre-existing behaviour).
        using var executionGate = new MachineExecutionGate();
        var executor = new DeploymentExecutor(
            serverLink, packageSource, artifactSink, loader, executionGate, agentConfig,
            loggerFactory.CreateLogger<DeploymentExecutor>());

        log.LogInformation(
            "Running offline drop {DeploymentId} ({Steps} step(s)) from '{Bundle}'.",
            plan.DeploymentId, plan.Steps.Length, bundleDir);

        try
        {
            // The executor drives the run in orchestrate mode (no server to drive
            // conditions/timeouts/retries/Required gating offline), accumulating
            // output variables across waves and reporting per-step + completion
            // through serverLink, which writes deployment-log.txt + deployment-result.json
            // (+ result-signature.bin) into the bundle root.
            // ct doubles as the "host stopping" token for the gate wait — offline
            // runs one plan against their own gate, so it never actually contends.
            await executor.ExecuteAsync(plan, orchestrateSteps: true, hostStopping: ct)
                .ConfigureAwait(false);
            return await ReadExitCodeAsync(bundleDir, ct).ConfigureAwait(false);
        }
        finally
        {
            // The runner's working dir (step-package cache + per-step staging)
            // lives under the bundle; remove it so it isn't shipped back inside
            // the re-zipped result bundle. Outputs (result/log/artifacts) are in
            // the bundle root, not workDir.
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private async Task<int> ReadExitCodeAsync(string bundleDir, CancellationToken ct)
    {
        try
        {
            var resultPath = Path.Combine(bundleDir, OfflineBundleLayout.ResultFile);
            var json = await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<OfflineDropResult>(json, Web);
            return result is { Success: true } ? 0 : 1;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger<OfflineRunner>()
                .LogError(ex, "Run finished but the result file could not be read.");
            return 1;
        }
    }
}
