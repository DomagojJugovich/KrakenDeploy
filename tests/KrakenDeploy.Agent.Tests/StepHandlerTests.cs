using System.Collections.ObjectModel;
using FluentAssertions;
using KrakenDeploy.Agent.Deployment.StepHandlers;
using KrakenDeploy.Contracts;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="IStepHandler"/> implementations.
/// Tests that don't require external process execution (ScriptRunner) or file I/O
/// use in-memory contexts; file-I/O handlers are tested with temp directories.
/// </summary>
public sealed class StepHandlerTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"kraken-handler-test-{Guid.NewGuid():N}");

    public StepHandlerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ── CanHandle / RequiresPackage ───────────────────────────────────────────

    [Theory]
    [InlineData("KrakenDeploy.Script")]
    [InlineData("krakendeploy.script")]
    [InlineData("Octopus.Script")]
    [InlineData("OCTOPUS.SCRIPT")]
    public void ScriptStepHandler_CanHandle_is_case_insensitive(string stepType)
    {
        var handler = new ScriptStepHandler(null!);
        handler.CanHandle(stepType).Should().BeTrue();
    }

    [Fact]
    public void ScriptStepHandler_CanHandle_returns_false_for_unknown_type()
    {
        var handler = new ScriptStepHandler(null!);
        handler.CanHandle("Octopus.IIS").Should().BeFalse();
    }

    [Fact]
    public void ScriptStepHandler_RequiresPackage_is_true()
    {
        new ScriptStepHandler(null!).RequiresPackage.Should().BeTrue();
    }

    [Fact]
    public void SubstituteVariablesStepHandler_CanHandle_recognises_step_type()
    {
        var handler = new SubstituteVariablesStepHandler();
        handler.CanHandle("Octopus.SubstituteVariables").Should().BeTrue();
        handler.CanHandle("OCTOPUS.SUBSTITUTEVARIABLES").Should().BeTrue();
        handler.CanHandle("Octopus.Script").Should().BeFalse();
    }

    [Fact]
    public void FileTransformStepHandler_CanHandle_recognises_step_type()
    {
        var handler = new FileTransformStepHandler();
        handler.CanHandle("Octopus.FileTransform").Should().BeTrue();
        handler.CanHandle("OCTOPUS.FILETRANSFORM").Should().BeTrue();
        handler.CanHandle("Octopus.Script").Should().BeFalse();
    }

    [Fact]
    public void ManualInterventionStepHandler_CanHandle_recognises_step_type()
    {
        var handler = new ManualInterventionStepHandler();
        handler.CanHandle("Octopus.Manual").Should().BeTrue();
        handler.CanHandle("OCTOPUS.MANUAL").Should().BeTrue();
        handler.CanHandle("Octopus.Script").Should().BeFalse();
    }

    [Fact]
    public void ManualInterventionStepHandler_RequiresPackage_is_false()
    {
        new ManualInterventionStepHandler().RequiresPackage.Should().BeFalse();
    }

    // ── ManualInterventionStepHandler.HandleAsync ──────────────────────────────

    [Fact]
    public async Task ManualIntervention_auto_approves_and_returns_true()
    {
        var logs = new List<(string Level, string Message)>();
        var context = MakeContext("Octopus.Manual",
            new Dictionary<string, string> { ["Instructions"] = "Please review the build." },
            logs);

        var handler = new ManualInterventionStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue("manual steps auto-approve in unattended mode");
        logs.Should().Contain(l => l.Message.Contains("Please review the build."));
        logs.Should().Contain(l => l.Message.Contains("auto-approved"));
    }

    [Fact]
    public async Task ManualIntervention_handles_missing_instructions_gracefully()
    {
        var logs = new List<(string Level, string Message)>();
        var context = MakeContext("Octopus.Manual", [], logs);

        var handler = new ManualInterventionStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        logs.Should().Contain(l => l.Level == "info");
    }

    // ── SubstituteVariablesStepHandler.HandleAsync ────────────────────────────

    [Fact]
    public async Task SubstituteVariables_replaces_octostache_tokens_in_file()
    {
        var filePath = Path.Combine(_tempDir, "app.config");
        await File.WriteAllTextAsync(filePath, "Server: #{ServerName}, Port: #{Port}");

        var variables = new Dictionary<string, string>
        {
            ["ServerName"] = "prod-db01",
            ["Port"]       = "5432",
        };

        var logs = new List<(string Level, string Message)>();
        var context = MakeContext(
            "Octopus.SubstituteVariables",
            new Dictionary<string, string>
            {
                ["Octopus.Action.SubstituteInFiles.TargetFiles"] = "app.config",
            },
            logs,
            extractDir: _tempDir,
            variables: variables);

        var handler = new SubstituteVariablesStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Be("Server: prod-db01, Port: 5432");
    }

    [Fact]
    public async Task SubstituteVariables_returns_true_when_no_target_files_specified()
    {
        var logs = new List<(string Level, string Message)>();
        var context = MakeContext("Octopus.SubstituteVariables", [], logs,
            extractDir: _tempDir);

        var handler = new SubstituteVariablesStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue("no target files is a warning, not a failure");
        logs.Should().Contain(l => l.Level == "warning");
    }

    // ── FileTransformStepHandler.HandleAsync ──────────────────────────────────

    [Fact]
    public async Task FileTransform_applies_variable_to_json_property()
    {
        var filePath = Path.Combine(_tempDir, "appsettings.json");
        await File.WriteAllTextAsync(filePath, """
            {
              "ConnectionStrings": {
                "Default": "Server=old;Database=db"
              }
            }
            """);

        var variables = new Dictionary<string, string>
        {
            ["ConnectionStrings.Default"] = "Server=prod-db01;Database=proddb",
        };

        var logs = new List<(string Level, string Message)>();
        var context = MakeContext(
            "Octopus.FileTransform",
            new Dictionary<string, string>
            {
                ["Octopus.Action.Package.JsonConfigurationVariablesTargets"] = "appsettings.json",
            },
            logs,
            extractDir: _tempDir,
            variables: variables);

        var handler = new FileTransformStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("Server=prod-db01;Database=proddb");
        content.Should().NotContain("Server=old");
    }

    [Fact]
    public async Task FileTransform_returns_true_when_no_targets_specified()
    {
        var logs = new List<(string Level, string Message)>();
        var context = MakeContext("Octopus.FileTransform", [], logs,
            extractDir: _tempDir);

        var handler = new FileTransformStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        logs.Should().Contain(l => l.Level == "warning");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StepHandlerContext MakeContext(
        string stepType,
        Dictionary<string, string> config,
        List<(string Level, string Message)> logSink,
        string? extractDir = null,
        Dictionary<string, string>? variables = null)
    {
        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Test",
            Steps: [],
            Variables: new ReadOnlyDictionary<string, string>(
                variables ?? new Dictionary<string, string>()),
            ArrayVariables: new ReadOnlyDictionary<string, string[]>(
                new Dictionary<string, string[]>()));

        var step = new DeploymentStepPlan(
            Index: 0,
            Name: "Test Step",
            StepType: stepType,
            PackageId: "TestPkg",
            PackageVersion: "1.0.0",
            Config: new ReadOnlyDictionary<string, string>(config));

        return new StepHandlerContext
        {
            Plan         = plan,
            Step         = step,
            ExtractDir   = extractDir ?? string.Empty,
            ArtifactsDir = extractDir is not null
                ? Path.Combine(extractDir, "artifacts")
                : string.Empty,
            LogAsync     = (level, msg) =>
            {
                logSink.Add((level, msg));
                return Task.CompletedTask;
            },
        };
    }
}
