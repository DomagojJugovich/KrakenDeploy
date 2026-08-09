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
///         the no-server fallback path with various property bags (WP3: the real
///         gate is server-side; this handler only warns).</item>
///   <item>The produced <c>octopus.manual-*.kdeploy-step</c> archive —
///         layout matches what the loader + server-side validator expect.</item>
/// </list>
/// The archive test reads <c>bin/Debug/net10.0/octopus.manual-*.kdeploy-step</c>
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
    public async Task HandleAsync_proceeds_but_warns_that_approval_was_not_enforced()
    {
        // WP3 changed what this handler MEANS. It is no longer the approval flow —
        // online tasks pause server-side (Octopus.Manual is in
        // WavePartitioner.ServerOnlyStepTypes, so it never reaches an agent) and
        // offline drop bundles containing one are refused at generation time. This
        // handler is only reachable by a runner executing a hand-built plan that
        // bypassed both gates, where there is no approver to reach.
        //
        // So it still proceeds — it cannot block — but the log line must be a WARNING
        // that the change-control gate did not run, not a reassuring "auto-approved".
        // That line is the only signal in a deployment log that an approval step was
        // passed without an approval.
        var handler = new ManualInterventionStepHandler();
        var logs    = new List<(string Level, string Message)>();

        var context = NewContext(
            new Dictionary<string, string>
            {
                [OctopusManualConfigKeys.Instructions] = "Please verify backups completed.",
            },
            logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeTrue("the handler cannot block — there is nobody to ask");
        logs.Should().Contain(l => l.Message.Contains("Please verify backups completed."));

        var warning = logs.Should().ContainSingle(l => l.Level == "warning").Subject;
        warning.Message.Should().Contain("APPROVAL NOT ENFORCED");
        logs.Should().NotContain(l => l.Message.Contains("auto-approved"),
            "'auto-approved' reads as a legitimate approval in an audit log");
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
            "the project's pack target must produce octopus.manual-<version>.kdeploy-step " +
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
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().ContainSingle().Which.Id.Should().Be("Octopus.Manual");
        manifest.ExecutorTypeName.Should().Be(typeof(ManualInterventionStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Manual.dll");

        var dllEntry = zip.GetEntry($"executor/{manifest.ExecutorAssembly}");
        dllEntry.Should().NotBeNull(
            "the manifest's executorAssembly must exist under executor/ inside the archive");
    }

    [Fact]
    public void Built_archive_bundles_Octostache_runtime_DLL()
    {
        // Pins the CopyLocalLockFileAssemblies fix in
        // steps/KrakenStepPackage.targets. Octostache is the handler's
        // only third-party runtime dep — if it stops shipping in executor/
        // the agent's AssemblyDependencyResolver will silently fall back
        // to the default ALC (if the agent host happens to reference it)
        // OR fail at HandleAsync time (if the agent host doesn't). Either
        // way the package isn't actually self-contained anymore. The
        // archive carries it so step packages with NuGet deps the agent
        // does NOT host (AWSSDK.S3, AWSSDK.CloudFront, …) work the same
        // way as this one.
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs  = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        zip.GetEntry("executor/Octostache.dll").Should().NotBeNull(
            "Octostache is referenced by the handler — its runtime DLL must " +
            "ship inside the .kdeploy-step archive's executor/ directory");
    }

    [Fact]
    public void Built_archive_excludes_agent_hosted_runtime_DLLs()
    {
        // The agent host process references Contracts (which pulls in
        // Google.Protobuf + the Grpc.Net stack) plus Microsoft.Extensions
        // .Logging.Abstractions through Serilog. Those are explicitly
        // excluded from executor/ in KrakenStepPackage.targets so we don't
        // ship ~1 MB of dead weight that the D-4 ALC delegation would
        // resolve from the default ALC anyway.
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs  = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var entries = zip.Entries.Select(e => e.FullName).ToArray();

        entries.Should().NotContain(e => e.EndsWith(
            "/KrakenDeploy.Contracts.dll", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(e => e.EndsWith(
            "/Google.Protobuf.dll", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(e => e.Contains(
            "/Grpc.", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(e => e.EndsWith(
            "/Microsoft.Extensions.Logging.Abstractions.dll", StringComparison.OrdinalIgnoreCase));
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
        var binRoot = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "..",
            "steps", "KrakenDeploy.Steps.Manual", "bin"));
        // Configuration-agnostic: CI builds Release, local builds Debug — locate
        // the packed archive under bin/<Config>/<tfm>/ wherever it landed.
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.manual-*.kdeploy-step",
                SearchOption.AllDirectories)
                .OrderByDescending(p => Version.Parse(ArchiveVersion(p)))
                .FirstOrDefault()
            : null;
    }

    // "<id>-<version>.kdeploy-step" -> "<version>" (the id itself may contain dashes).
    private static string ArchiveVersion(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem[(stem.LastIndexOf('-') + 1)..];
    }
}
