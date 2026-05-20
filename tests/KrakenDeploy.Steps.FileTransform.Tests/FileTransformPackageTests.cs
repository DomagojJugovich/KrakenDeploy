using System.IO.Compression;
using System.Text.Json.Nodes;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.FileTransform;

namespace KrakenDeploy.Steps.FileTransform.Tests;

/// <summary>
/// Unit tests for the Phase D-8 step-package port of <c>Octopus.FileTransform</c>.
/// Behavioural parity with the legacy in-DI handler — the same JSON path
/// substitution semantics, case-insensitive key matching, and silent skip
/// when no path matches.
/// </summary>
public sealed class FileTransformPackageTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"kraken-filetransform-test-{Guid.NewGuid():N}");

    public FileTransformPackageTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("Octopus.FileTransform", true)]
    [InlineData("octopus.filetransform", true)]
    [InlineData("Kraken.Script",         false)]
    public void CanHandle_only_octopus_filetransform(string stepType, bool expected)
        => new FileTransformStepHandler().CanHandle(stepType).Should().Be(expected);

    [Fact]
    public async Task Applies_dotted_variable_names_to_nested_json_paths()
    {
        var path = Path.Combine(_workspace, "appsettings.json");
        await File.WriteAllTextAsync(path, """
            {
              "ConnectionStrings": { "Default": "old" },
              "Logging": { "Level": "Info" }
            }
            """);

        var ctx = NewContext(
            extractDir: _workspace,
            config: new() { ["Octopus.Action.Package.JsonConfigurationVariablesTargets"] = "appsettings.json" },
            variables: new()
            {
                ["ConnectionStrings.Default"] = "Server=db;Database=app;",
                ["Logging.Level"]             = "Debug",
                ["NoMatch.Key.Here"]          = "ignored",
            });

        var handler = new FileTransformStepHandler();
        var ok      = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        node["ConnectionStrings"]!["Default"]!.GetValue<string>()
            .Should().Be("Server=db;Database=app;");
        node["Logging"]!["Level"]!.GetValue<string>().Should().Be("Debug");
    }

    [Fact]
    public async Task Matches_keys_case_insensitively_but_preserves_original_casing()
    {
        var path = Path.Combine(_workspace, "appsettings.json");
        await File.WriteAllTextAsync(path, """{ "ConnectionStrings": { "Default": "old" } }""");

        var ctx = NewContext(
            extractDir: _workspace,
            config: new() { ["Octopus.Action.Package.JsonConfigurationVariablesTargets"] = "appsettings.json" },
            variables: new() { ["connectionstrings.DEFAULT"] = "new" });

        await new FileTransformStepHandler().HandleAsync(ctx, CancellationToken.None);

        var raw = await File.ReadAllTextAsync(path);
        raw.Should().Contain("\"ConnectionStrings\"",
            "the rewritten file keeps the original casing on the property name");
        raw.Should().Contain("\"Default\": \"new\"");
    }

    [Fact]
    public async Task Warns_and_returns_true_when_no_targets_pattern_configured()
    {
        var logs = new List<(string, string)>();
        var ctx  = NewContext(_workspace, new(), new(), logs);

        (await new FileTransformStepHandler().HandleAsync(ctx, CancellationToken.None))
            .Should().BeTrue();
        logs.Should().Contain(l => l.Item1 == "warning" && l.Item2.Contains("No JSON config targets"));
    }

    [Fact]
    public void Built_manifest_has_correct_id_and_executor_type()
    {
        var path = FindBuiltArchive();
        path.Should().NotBeNull(
            "the pack target must produce octopus.filetransform-1.0.0.kdeploy-step");

        using var fs  = File.OpenRead(path!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        using var r   = new StreamReader(
            zip.GetEntry(StepPackageFiles.ManifestFileName)!.Open());
        var manifest = StepPackageManifestJson.Deserialize(r.ReadToEnd());

        manifest.Id.Should().Be("octopus.filetransform");
        manifest.Version.Should().Be("1.0.0");
        manifest.StepTypes.Should().ContainSingle().Which.Should().Be("Octopus.FileTransform");
        manifest.ExecutorTypeName.Should().Be(typeof(FileTransformStepHandler).FullName!);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static StepHandlerContext NewContext(
        string extractDir,
        Dictionary<string, string> config,
        Dictionary<string, string> variables,
        List<(string, string)>? logs = null)
    {
        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Production",
            Steps: [],
            Variables: variables,
            ArrayVariables: new Dictionary<string, string[]>());

        var step = new DeploymentStepPlan(
            Index: 0, Name: "Tx", StepType: "Octopus.FileTransform",
            PackageId: "p", PackageVersion: "1.0.0", Config: config);

        return new StepHandlerContext
        {
            Plan         = plan,
            Step         = step,
            ExtractDir   = extractDir,
            ArtifactsDir = extractDir,
            LogAsync     = (level, msg) =>
            {
                logs?.Add((level, msg));
                return Task.CompletedTask;
            },
        };
    }

    private static string? FindBuiltArchive()
    {
        var here      = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "..",
            "steps", "KrakenDeploy.Steps.FileTransform",
            "bin", "Debug", "net10.0",
            "octopus.filetransform-1.0.0.kdeploy-step"));
        return File.Exists(candidate) ? candidate : null;
    }
}
