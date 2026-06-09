using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.SubstituteVariables;

namespace KrakenDeploy.Steps.SubstituteVariables.Tests;

/// <summary>
/// Unit tests for the Phase D-8 step-package port of <c>Octopus.SubstituteVariables</c>.
/// Behaviour is mirrored from the legacy in-DI handler — these tests drive
/// the same scenarios (target patterns, octostache substitution, missing
/// pattern warning) to prove the port is a true drop-in.
/// </summary>
public sealed class SubstituteVariablesPackageTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"kraken-subvars-test-{Guid.NewGuid():N}");

    public SubstituteVariablesPackageTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("Octopus.SubstituteVariables", true)]
    [InlineData("octopus.substitutevariables", true)] // case-insensitive
    [InlineData("Kraken.Script",                false)]
    public void CanHandle_only_octopus_substitutevariables(string stepType, bool expected)
        => new SubstituteVariablesStepHandler().CanHandle(stepType).Should().Be(expected);

    [Fact]
    public void Handler_requires_a_package()
        => new SubstituteVariablesStepHandler().RequiresPackage.Should().BeTrue(
            "the handler operates on files extracted from the step's primary package");

    [Fact]
    public async Task Substitutes_variables_in_a_single_file()
    {
        var file = Path.Combine(_workspace, "appsettings.json");
        await File.WriteAllTextAsync(file, """{ "ConnectionString": "#{Db.ConnectionString}" }""");

        var handler = new SubstituteVariablesStepHandler();
        var logs    = new List<(string, string)>();
        var ctx     = NewContext(
            extractDir: _workspace,
            config: new() { ["Octopus.Action.SubstituteInFiles.TargetFiles"] = "appsettings.json" },
            variables: new() { ["Db.ConnectionString"] = "Server=db;Database=app;" },
            logs: logs);

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue();
        var rewritten = await File.ReadAllTextAsync(file);
        rewritten.Should().Contain("Server=db;Database=app;");
        rewritten.Should().NotContain("#{Db.ConnectionString}");
    }

    [Fact]
    public async Task Warns_when_no_target_files_pattern_is_configured()
    {
        var handler = new SubstituteVariablesStepHandler();
        var logs    = new List<(string, string)>();
        var ctx     = NewContext(
            extractDir: _workspace,
            config: new(),  // no TargetFiles key
            variables: new(),
            logs: logs);

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue("the handler is a no-op when no patterns are configured, not a failure");
        logs.Should().Contain(l => l.Item1 == "warning" && l.Item2.Contains("No target files"));
    }

    [Fact]
    public async Task Resolves_a_dir_relative_glob_pattern()
    {
        // The legacy handler uses Directory.GetFiles with a hand-written
        // split on the last slash — patterns like "config/*.txt" land in
        // the right directory + filename glob. Full ** semantics are NOT
        // implemented (matches the in-DI handler's behaviour exactly).
        var subDir = Path.Combine(_workspace, "config");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(
            Path.Combine(subDir, "a.txt"), "Hello #{Name}");
        await File.WriteAllTextAsync(
            Path.Combine(subDir, "b.txt"), "Bye #{Name}");

        var handler = new SubstituteVariablesStepHandler();
        var logs    = new List<(string, string)>();
        var ctx     = NewContext(
            extractDir: _workspace,
            config: new() { ["Octopus.Action.SubstituteInFiles.TargetFiles"] = "config/*.txt" },
            variables: new() { ["Name"] = "world" },
            logs: logs);

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(subDir, "a.txt"))).Should().Be("Hello world");
        (await File.ReadAllTextAsync(Path.Combine(subDir, "b.txt"))).Should().Be("Bye world");
    }

    [Fact]
    public void Built_archive_lands_at_expected_path()
        => FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.substitutevariables-1.0.0.kdeploy-step");

    [Fact]
    public void Built_manifest_has_correct_id_and_executor_type()
    {
        var path = FindBuiltArchive();
        path.Should().NotBeNull();

        using var fs  = File.OpenRead(path!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        using var r = new StreamReader(
            zip.GetEntry(StepPackageFiles.ManifestFileName)!.Open());
        var manifest = StepPackageManifestJson.Deserialize(r.ReadToEnd());

        manifest.Id.Should().Be("octopus.substitutevariables");
        manifest.Version.Should().Be("1.0.0");
        manifest.StepTypes.Should().ContainSingle().Which.Should().Be("Octopus.SubstituteVariables");
        manifest.ExecutorTypeName.Should().Be(typeof(SubstituteVariablesStepHandler).FullName!);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static StepHandlerContext NewContext(
        string extractDir,
        Dictionary<string, string> config,
        Dictionary<string, string> variables,
        List<(string, string)> logs)
    {
        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Production",
            Steps: [],
            Variables: variables,
            ArrayVariables: new Dictionary<string, string[]>());

        var step = new DeploymentStepPlan(
            Index: 0, Name: "Sub", StepType: "Octopus.SubstituteVariables",
            PackageId: "p", PackageVersion: "1.0.0", Config: config);

        return new StepHandlerContext
        {
            Plan         = plan,
            Step         = step,
            ExtractDir   = extractDir,
            ArtifactsDir = extractDir,
            LogAsync     = (level, msg) =>
            {
                logs.Add((level, msg));
                return Task.CompletedTask;
            },
        };
    }

    private static string? FindBuiltArchive()
    {
        var here      = AppContext.BaseDirectory;
        var binRoot = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "..",
            "steps", "KrakenDeploy.Steps.SubstituteVariables", "bin"));
        // Configuration-agnostic: CI builds Release, local builds Debug — locate
        // the packed archive under bin/<Config>/<tfm>/ wherever it landed.
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.substitutevariables-1.0.0.kdeploy-step",
                SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }
}
