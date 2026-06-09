using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
/// P6 — full offline end-to-end through the real <c>DeploymentExecutor</c>: a
/// bundled <c>.kdeploy-step</c> handler is loaded via ALC, two sequential steps
/// run, and the second step reads the first step's output variable
/// (<c>Octopus.Action[Producer].Output.Foo</c>). Proves cross-step output
/// feed-forward — the gap that motivated the unification — works offline, with
/// no shell dependency (the handler emits the <c>##octopus[setVariable]</c>
/// marker directly).
/// </summary>
public sealed class OfflineE2ETests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _bundle =
        Path.Combine(Path.GetTempPath(), $"kraken-e2e-{Guid.NewGuid():N}");

    public OfflineE2ETests() => Directory.CreateDirectory(_bundle);

    [Fact]
    public async Task Cross_step_output_feed_forward_runs_offline()
    {
        var key = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var deploymentId = Guid.NewGuid();

        // Bundle the test handler as a real step-package archive.
        BuildHandlerArchive(Path.Combine(
            _bundle, "step-packages", "kraken.feedforward", "1.0.0", "package.kdeploy-step"));

        // Producer (wave 1) emits Foo=bar; Consumer (wave 2) reads the producer's
        // output and re-emits it as Echoed. Default StartTrigger=0 → each step is
        // its own wave (sequential), so the producer's output is accumulated into
        // the consumer's plan before it runs.
        var producer = new DeploymentStepPlan(
            0, "Producer", "Kraken.FeedForwardTest", "", "",
            new Dictionary<string, string> { ["Emit.Name"] = "Foo", ["Emit.Value"] = "bar" },
            StepPackageName: "kraken.feedforward", StepPackageVersion: "1.0.0");
        var consumer = new DeploymentStepPlan(
            1, "Consumer", "Kraken.FeedForwardTest", "", "",
            new Dictionary<string, string>
            {
                ["Echo.From"] = "Octopus.Action[Producer].Output.Foo",
                ["Echo.As"] = "Echoed",
            },
            StepPackageName: "kraken.feedforward", StepPackageVersion: "1.0.0");

        var plan = new DeploymentPlan(
            deploymentId, "Production", [producer, consumer],
            new Dictionary<string, string>(), new Dictionary<string, string[]>());

        File.WriteAllText(
            Path.Combine(_bundle, OfflineBundleLayout.EncryptedPlanFile),
            AesGcmCipher.Encrypt(key, JsonSerializer.Serialize(plan, Web)));

        var exit = await new OfflineRunner(NullLoggerFactory.Instance).RunAsync(_bundle, key);

        exit.Should().Be(0);

        var result = JsonSerializer.Deserialize<OfflineDropResult>(
            await File.ReadAllTextAsync(Path.Combine(_bundle, OfflineBundleLayout.ResultFile)), Web)!;
        result.Success.Should().BeTrue();

        var producerOut = result.Steps.Single(s => s.StepName == "Producer");
        producerOut.OutputVariables.Should().ContainKey("Foo").WhoseValue.Should().Be("bar");

        var consumerOut = result.Steps.Single(s => s.StepName == "Consumer");
        // The headline: the consumer saw the producer's output offline.
        consumerOut.OutputVariables.Should().ContainKey("Echoed").WhoseValue.Should().Be("bar");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static void BuildHandlerArchive(string destPath)
    {
        var manifest = new StepPackageManifest
        {
            Id = "kraken.feedforward",
            Version = "1.0.0",
            DisplayName = "Feed-forward test",
            TargetFramework = "net10.0",
            StepTypes = ["Kraken.FeedForwardTest"],
            ExecutorAssembly = typeof(FeedForwardTestHandler).Assembly.GetName().Name + ".dll",
            ExecutorTypeName = typeof(FeedForwardTestHandler).FullName!,
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

        var asm = typeof(FeedForwardTestHandler).Assembly.Location;
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
/// Shell-free test step handler: emits an output variable (Emit.*) and/or reads
/// a plan variable and re-emits it (Echo.*), via the
/// <c>##octopus[setVariable]</c> marker the executor intercepts. Lives in the
/// test assembly so the archive builder can pack it as the step-package executor.
/// </summary>
public sealed class FeedForwardTestHandler : IStepHandler
{
    public bool CanHandle(string stepType) => stepType == "Kraken.FeedForwardTest";

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var cfg = context.Step.Config;

        if (cfg.TryGetValue("Emit.Name", out var emitName) &&
            cfg.TryGetValue("Emit.Value", out var emitValue))
        {
            await EmitAsync(context, emitName, emitValue).ConfigureAwait(false);
        }

        if (cfg.TryGetValue("Echo.From", out var from) &&
            cfg.TryGetValue("Echo.As", out var asName))
        {
            var value = context.Plan.Variables.TryGetValue(from, out var v) ? v : "<missing>";
            await EmitAsync(context, asName, value).ConfigureAwait(false);
        }

        return true;
    }

    private static Task EmitAsync(StepHandlerContext context, string name, string value)
    {
        var b64Name = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
        var b64Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return context.LogAsync("info", $"##octopus[setVariable name='{b64Name}' value='{b64Value}']");
    }
}
