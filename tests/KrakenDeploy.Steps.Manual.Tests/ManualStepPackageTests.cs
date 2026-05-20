using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Manual;

namespace KrakenDeploy.Steps.Manual.Tests;

/// <summary>
/// Two surfaces under test (Phase D-8):
/// <list type="bullet">
///   <item>The handler class itself — <c>CanHandle</c>, <c>RequiresPackage</c>,
///         the auto-approve path with various property bags.</item>
///   <item>The produced <c>octopus.manual-1.0.0.kdeploy-step</c> archive —
///         layout matches what the loader + server-side validator expect.</item>
/// </list>
/// The archive test reads <c>bin/Debug/net10.0/octopus.manual-1.0.0.kdeploy-step</c>
/// directly so any broken pack target shows up here, not at runtime on a
/// real agent.
/// </summary>
public sealed class ManualStepPackageTests
{
    // ── Handler behaviour ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Octopus.Manual",  true)]
    [InlineData("octopus.manual",  true)]   // case-insensitive
    [InlineData("Kraken.Script",   false)]
    [InlineData("",                false)]
    public void CanHandle_returns_true_only_for_Octopus_Manual(string stepType, bool expected)
    {
        var handler = new ManualInterventionStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new ManualInterventionStepHandler().RequiresPackage.Should().BeFalse(
            "manual intervention is purely informational — no package download.");

    [Fact]
    public async Task HandleAsync_auto_approves_with_a_log_line()
    {
        var handler = new ManualInterventionStepHandler();
        var logs    = new List<(string Level, string Message)>();

        var context = NewContext(
            new Dictionary<string, string>
            {
                [OctopusManualConfigKeys.Instructions] = "Please verify backups completed.",
            },
            logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeTrue();
        logs.Should().Contain(l => l.Message.Contains("Please verify backups completed."));
        logs.Should().Contain(l => l.Message.Contains("auto-approved"));
    }

    [Fact]
    public async Task HandleAsync_resolves_octostache_placeholders_in_instructions()
    {
        var handler = new ManualInterventionStepHandler();
        var logs    = new List<(string Level, string Message)>();

        var context = NewContext(
            new Dictionary<string, string>
            {
                [OctopusManualConfigKeys.Instructions] = "Approve release #{Octopus.Release.Number}",
            },
            logs,
            variables: new Dictionary<string, string>
            {
                ["Octopus.Release.Number"] = "2.3.0",
            });

        await handler.HandleAsync(context, CancellationToken.None);

        logs.Should().Contain(l => l.Message.Contains("Approve release 2.3.0"));
    }

    [Fact]
    public async Task HandleAsync_logs_responsible_team_ids_for_audit()
    {
        var handler = new ManualInterventionStepHandler();
        var logs    = new List<(string Level, string Message)>();

        var context = NewContext(
            new Dictionary<string, string>
            {
                [OctopusManualConfigKeys.ResponsibleTeamIds] = "team-ops, team-sec",
            },
            logs);

        await handler.HandleAsync(context, CancellationToken.None);

        logs.Should().Contain(l => l.Message.Contains("team-ops") && l.Message.Contains("team-sec"));
    }

    // ── Built archive ──────────────────────────────────────────────────────

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the project's pack target must produce octopus.manual-1.0.0.kdeploy-step " +
            "next to KrakenDeploy.Steps.Manual.dll in the output directory");
    }

    [Fact]
    public void Built_archive_contains_a_well_formed_manifest_and_executor_DLL()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs  = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var manifestEntry = zip.GetEntry(StepPackageFiles.ManifestFileName);
        manifestEntry.Should().NotBeNull("manifest.json is the package's only required file");

        using var reader = new StreamReader(manifestEntry!.Open());
        var manifest     = StepPackageManifestJson.Deserialize(reader.ReadToEnd());

        manifest.Id.Should().Be("octopus.manual");
        manifest.Version.Should().Be("1.0.0");
        manifest.StepTypes.Should().ContainSingle().Which.Should().Be("Octopus.Manual");
        manifest.ExecutorTypeName.Should().Be(typeof(ManualInterventionStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Manual.dll");

        var dllEntry = zip.GetEntry($"executor/{manifest.ExecutorAssembly}");
        dllEntry.Should().NotBeNull(
            "the manifest's executorAssembly must exist under executor/ inside the archive");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static StepHandlerContext NewContext(
        Dictionary<string, string> config,
        List<(string Level, string Message)> logs,
        Dictionary<string, string>? variables = null)
    {
        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Production",
            Steps: [],
            Variables: variables ?? new Dictionary<string, string>(),
            ArrayVariables: new Dictionary<string, string[]>());

        var step = new DeploymentStepPlan(
            Index: 0,
            Name: "Approve",
            StepType: "Octopus.Manual",
            PackageId: "",
            PackageVersion: "",
            Config: config);

        return new StepHandlerContext
        {
            Plan         = plan,
            Step         = step,
            ExtractDir   = "",
            ArtifactsDir = "",
            LogAsync     = (level, message) =>
            {
                logs.Add((level, message));
                return Task.CompletedTask;
            },
        };
    }

    private static string? FindBuiltArchive()
    {
        // The Steps.Manual project's pack target writes to its OWN bin/, not
        // ours — chase the project-reference's output location.
        var here = AppContext.BaseDirectory;
        // tests/KrakenDeploy.Steps.Manual.Tests/bin/Debug/net10.0/ → up four
        // → solution root → steps/.../bin/.../*.kdeploy-step
        var candidate = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "..",
            "steps", "KrakenDeploy.Steps.Manual",
            "bin", "Debug", "net10.0",
            "octopus.manual-1.0.0.kdeploy-step"));
        return File.Exists(candidate) ? candidate : null;
    }
}
