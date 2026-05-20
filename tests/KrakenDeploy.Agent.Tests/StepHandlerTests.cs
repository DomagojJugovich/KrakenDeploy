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

    // Kraken.Script + Octopus.Script behavioural tests moved to
    // tests/KrakenDeploy.Steps.Script.Tests/ (D-8.4 — the in-DI Agent handler
    // + the agent's ScriptRunner singleton were retired).

    [Fact]
    public void SubstituteVariablesStepHandler_CanHandle_recognises_step_type()
    {
        var handler = new SubstituteVariablesStepHandler();
        handler.CanHandle("Octopus.SubstituteVariables").Should().BeTrue();
        handler.CanHandle("OCTOPUS.SUBSTITUTEVARIABLES").Should().BeTrue();
        handler.CanHandle("Octopus.Script").Should().BeFalse();
    }

    // Octopus.JsonConfigurationVariables (formerly Octopus.FileTransform) now
    // ships as a step package — see KrakenDeploy.Steps.JsonConfigurationVariables.Tests
    // for its behavioural tests. The in-DI handler was retired in D-8.3.

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
    public async Task ManualIntervention_auto_approves_and_logs_instructions_from_legacy_key()
    {
        // Back-compat: the legacy un-prefixed "Instructions" key continues to
        // work for any process authored before the alignment with the Octopus
        // contract.
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
    public async Task ManualIntervention_reads_Octopus_Action_Manual_Instructions_first()
    {
        // The Octopus contract key takes priority over the legacy key.
        var logs = new List<(string Level, string Message)>();
        var context = MakeContext("Octopus.Manual",
            new Dictionary<string, string>
            {
                [OctopusManualConfigKeys.Instructions] = "Octopus instructions take priority.",
                [OctopusManualConfigKeys.LegacyInstructionsKey] = "Should be ignored.",
            },
            logs);

        var handler = new ManualInterventionStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        logs.Should().Contain(l => l.Message.Contains("Octopus instructions take priority."));
        logs.Should().NotContain(l => l.Message.Contains("Should be ignored."));
    }

    [Fact]
    public async Task ManualIntervention_octostache_evaluates_instructions()
    {
        var logs = new List<(string Level, string Message)>();
        var context = MakeContext("Octopus.Manual",
            new Dictionary<string, string>
            {
                [OctopusManualConfigKeys.Instructions] =
                    "Please approve deploy of #{Octopus.Project.Name} to #{Octopus.Environment.Name}.",
            },
            logs,
            variables: new Dictionary<string, string>
            {
                ["Octopus.Project.Name"]     = "Argosy",
                ["Octopus.Environment.Name"] = "Production",
            });

        var handler = new ManualInterventionStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        logs.Should().Contain(l =>
            l.Message.Contains("Please approve deploy of Argosy to Production."));
    }

    [Fact]
    public async Task ManualIntervention_logs_responsible_teams_when_present()
    {
        var logs = new List<(string Level, string Message)>();
        var context = MakeContext("Octopus.Manual",
            new Dictionary<string, string>
            {
                [OctopusManualConfigKeys.Instructions]       = "Approve please.",
                [OctopusManualConfigKeys.ResponsibleTeamIds] = "teams-administrators,teams-ops",
            },
            logs);

        var handler = new ManualInterventionStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        logs.Should().Contain(l =>
            l.Message.Contains("teams-administrators") &&
            l.Message.Contains("teams-ops"));
    }

    [Fact]
    public async Task ManualIntervention_logs_BlockConcurrentDeployments_when_true()
    {
        var logs = new List<(string Level, string Message)>();
        var context = MakeContext("Octopus.Manual",
            new Dictionary<string, string>
            {
                [OctopusManualConfigKeys.Instructions]                = "Approve please.",
                [OctopusManualConfigKeys.BlockConcurrentDeployments] = "True",
            },
            logs);

        var handler = new ManualInterventionStepHandler();
        var result  = await handler.HandleAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        logs.Should().Contain(l =>
            l.Message.Contains("BlockConcurrentDeployments") &&
            l.Message.Contains("Kraken runs unattended"));
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

    // Octopus.JsonConfigurationVariables behavioural tests have moved to
    // tests/KrakenDeploy.Steps.JsonConfigurationVariables.Tests/ along with
    // the handler itself (D-8.3 — the in-DI Agent handler was retired).

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
