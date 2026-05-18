using System.Collections.ObjectModel;
using FluentAssertions;
using KrakenDeploy.Agent.Deployment.Package;
using KrakenDeploy.Agent.Deployment.StepHandlers;
using KrakenDeploy.Contracts;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="OctopusTentaclePackageStepHandler"/>.
/// Each test stages a temp directory representing the extracted package, drives
/// the handler with a hand-crafted <c>Octopus.Action.*</c> property bag, and
/// asserts the on-disk outcome.
/// </summary>
public sealed class OctopusTentaclePackageStepHandlerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"kraken-tentaclepkg-test-{Guid.NewGuid():N}");

    public OctopusTentaclePackageStepHandlerTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Discoverability ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Octopus.TentaclePackage")]
    [InlineData("OCTOPUS.TENTACLEPACKAGE")]
    [InlineData("octopus.tentaclepackage")]
    public void CanHandle_is_case_insensitive(string stepType)
        => new OctopusTentaclePackageStepHandler().CanHandle(stepType).Should().BeTrue();

    [Fact]
    public void CanHandle_rejects_other_step_types()
    {
        var handler = new OctopusTentaclePackageStepHandler();
        handler.CanHandle("Octopus.Script").Should().BeFalse();
        handler.CanHandle("Octopus.IIS").Should().BeFalse();
        handler.CanHandle("Kraken.IIS").Should().BeFalse();
    }

    [Fact]
    public void RequiresPackage_is_true() =>
        new OctopusTentaclePackageStepHandler().RequiresPackage.Should().BeTrue();

    // ── No features enabled ────────────────────────────────────────────────

    [Fact]
    public async Task No_features_warns_about_missing_destination_but_still_succeeds()
    {
        var (extractDir, _) = StageExtractedPackage();
        var logs = new List<(string Level, string Message)>();

        var result = await new OctopusTentaclePackageStepHandler()
            .HandleAsync(Context(config: [], extractDir: extractDir, logs: logs), CancellationToken.None);

        result.Should().BeTrue();
        logs.Should().Contain(l => l.Level == "warning" && l.Message.Contains("CustomDirectory"));
    }

    [Fact]
    public async Task Empty_extract_dir_fails()
    {
        var logs = new List<(string Level, string Message)>();
        var result = await new OctopusTentaclePackageStepHandler()
            .HandleAsync(Context(config: [], extractDir: string.Empty, logs: logs), CancellationToken.None);

        result.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error");
    }

    // ── CustomDirectory ────────────────────────────────────────────────────

    [Fact]
    public async Task CustomDirectory_copies_package_contents_to_destination()
    {
        var (extractDir, _) = StageExtractedPackage();
        var customDir = Path.Combine(_root, "install");

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.CustomDirectory",
            ["Octopus.Action.Package.CustomInstallationDirectory"] = customDir,
        };

        var result = await Run(config, extractDir);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(customDir, "Web.config")).Should().BeTrue();
        File.Exists(Path.Combine(customDir, "bin", "App.dll")).Should().BeTrue();
    }

    [Fact]
    public async Task CustomDirectory_resolves_octostache_in_path()
    {
        var (extractDir, _) = StageExtractedPackage();
        var customDir = Path.Combine(_root, "install-Production");

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.CustomDirectory",
            ["Octopus.Action.Package.CustomInstallationDirectory"] =
                Path.Combine(_root, "install-#{Octopus.Environment.Name}"),
        };
        var variables = new Dictionary<string, string>
        {
            ["Octopus.Environment.Name"] = "Production",
        };

        var result = await Run(config, extractDir, variables: variables);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(customDir, "Web.config")).Should().BeTrue();
    }

    [Fact]
    public async Task CustomDirectory_purge_deletes_old_contents_before_copy()
    {
        var (extractDir, _) = StageExtractedPackage();
        var customDir = Path.Combine(_root, "install");
        Directory.CreateDirectory(customDir);
        await File.WriteAllTextAsync(Path.Combine(customDir, "OldFile.txt"), "obsolete");

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.CustomDirectory",
            ["Octopus.Action.Package.CustomInstallationDirectory"] = customDir,
            ["Octopus.Action.Package.CustomInstallationDirectoryShouldBePurgedBeforeDeployment"] = "True",
        };

        var result = await Run(config, extractDir);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(customDir, "OldFile.txt")).Should().BeFalse(
            "purge should have deleted pre-existing files");
        File.Exists(Path.Combine(customDir, "Web.config")).Should().BeTrue();
    }

    [Fact]
    public async Task CustomDirectory_purge_respects_exclusions_top_level_name()
    {
        var (extractDir, _) = StageExtractedPackage();
        var customDir = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(customDir, "App_Data"));
        await File.WriteAllTextAsync(Path.Combine(customDir, "App_Data", "uploads.bin"), "keep me");
        await File.WriteAllTextAsync(Path.Combine(customDir, "OldFile.txt"), "delete me");

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.CustomDirectory",
            ["Octopus.Action.Package.CustomInstallationDirectory"] = customDir,
            ["Octopus.Action.Package.CustomInstallationDirectoryShouldBePurgedBeforeDeployment"] = "True",
            ["Octopus.Action.Package.CustomInstallationDirectoryPurgeExclusions"] = "App_Data",
        };

        var result = await Run(config, extractDir);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(customDir, "OldFile.txt")).Should().BeFalse();
        File.Exists(Path.Combine(customDir, "App_Data", "uploads.bin")).Should().BeTrue(
            "App_Data was excluded from purge");
    }

    // ── ConfigurationVariables ────────────────────────────────────────────

    [Fact]
    public async Task ConfigurationVariables_substitutes_appSettings_value_by_matching_key()
    {
        var (extractDir, configPath) = StageExtractedPackageWithConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="ServerName" value="localhost" />
                <add key="Unrelated" value="leave me" />
              </appSettings>
            </configuration>
            """);

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.ConfigurationVariables",
            ["Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings"] = "True",
        };
        var variables = new Dictionary<string, string>
        {
            ["ServerName"] = "prod-db01",
        };

        var result = await Run(config, extractDir, variables: variables);

        result.Success.Should().BeTrue();
        var content = await File.ReadAllTextAsync(configPath);
        content.Should().Contain(@"key=""ServerName"" value=""prod-db01""");
        content.Should().Contain(@"value=""leave me""");
    }

    [Fact]
    public async Task ConfigurationVariables_substitutes_connectionStrings_by_matching_name()
    {
        var (extractDir, configPath) = StageExtractedPackageWithConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <connectionStrings>
                <add name="Default" connectionString="Server=old;Database=db" providerName="System.Data.SqlClient" />
              </connectionStrings>
            </configuration>
            """);

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.ConfigurationVariables",
            ["Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings"] = "True",
        };
        var variables = new Dictionary<string, string>
        {
            ["Default"] = "Server=prod;Database=proddb",
        };

        var result = await Run(config, extractDir, variables: variables);

        result.Success.Should().BeTrue();
        var content = await File.ReadAllTextAsync(configPath);
        content.Should().Contain(@"connectionString=""Server=prod;Database=proddb""");
        content.Should().NotContain("Server=old");
    }

    [Fact]
    public async Task ConfigurationVariables_skips_xdt_transform_files_when_base_exists()
    {
        // Web.config (base) + Web.Production.config (transform) — base should be
        // substituted, transform left alone.
        var (extractDir, basePath) = StageExtractedPackageWithConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="ServerName" value="localhost" />
              </appSettings>
            </configuration>
            """, fileName: "Web.config");

        var transformPath = Path.Combine(extractDir, "Web.Production.config");
        await File.WriteAllTextAsync(transformPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="ServerName" value="prod-from-transform" />
              </appSettings>
            </configuration>
            """);

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.ConfigurationVariables",
            ["Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings"] = "True",
        };
        var variables = new Dictionary<string, string>
        {
            ["ServerName"] = "from-variable",
        };

        var result = await Run(config, extractDir, variables: variables);

        result.Success.Should().BeTrue();
        (await File.ReadAllTextAsync(basePath)).Should().Contain(@"value=""from-variable""");
        (await File.ReadAllTextAsync(transformPath))
            .Should().Contain(@"value=""prod-from-transform""",
                "XDT transform file must not be touched by ConfigurationVariables");
    }

    [Fact]
    public async Task ConfigurationVariables_handles_invalid_xml_with_warning()
    {
        var (extractDir, configPath) = StageExtractedPackageWithConfig("not <valid> xml at all <<<");

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.ConfigurationVariables",
            ["Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings"] = "True",
        };

        var result = await Run(config, extractDir);

        result.Success.Should().BeTrue("invalid XML in one file should not fail the whole step");
        result.Logs.Should().Contain(l => l.Level == "warning" && l.Message.Contains("invalid XML"));
        // Original content untouched.
        (await File.ReadAllTextAsync(configPath)).Should().Be("not <valid> xml at all <<<");
    }

    // ── ConfigurationTransforms (deferred) ────────────────────────────────

    [Fact]
    public async Task ConfigurationTransforms_warns_about_unimplemented_xdt_support()
    {
        var (extractDir, _) = StageExtractedPackage();
        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.ConfigurationTransforms",
            ["Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles"] = "True",
        };

        var result = await Run(config, extractDir);

        result.Success.Should().BeTrue();
        result.Logs.Should().Contain(l => l.Level == "warning" && l.Message.Contains("XDT"));
    }

    // ── Combined feature exercise (mirrors a real Argosy step) ────────────

    [Fact]
    public async Task All_three_features_run_in_order()
    {
        var (extractDir, _) = StageExtractedPackageWithConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="ServerName" value="localhost" />
              </appSettings>
            </configuration>
            """);
        var customDir = Path.Combine(_root, "deploy");

        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] =
                "Octopus.Features.CustomDirectory,Octopus.Features.ConfigurationVariables,Octopus.Features.ConfigurationTransforms",
            ["Octopus.Action.Package.CustomInstallationDirectory"] = customDir,
            ["Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings"] = "True",
            ["Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles"] = "True",
        };
        var variables = new Dictionary<string, string>
        {
            ["ServerName"] = "prod-srv",
        };

        var result = await Run(config, extractDir, variables: variables);

        result.Success.Should().BeTrue();
        var deployedConfig = await File.ReadAllTextAsync(Path.Combine(customDir, "Web.config"));
        deployedConfig.Should().Contain(@"value=""prod-srv""",
            "ConfigurationVariables should have substituted in the copied destination");
        result.Logs.Should().Contain(l => l.Message.Contains("XDT") && l.Level == "warning",
            "transforms are flagged as not-yet-implemented");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private (string ExtractDir, string ConfigPath) StageExtractedPackage()
    {
        var dir = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        var configPath = Path.Combine(dir, "Web.config");
        File.WriteAllText(configPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings />
            </configuration>
            """);
        File.WriteAllBytes(Path.Combine(dir, "bin", "App.dll"), [0, 1, 2, 3]);
        return (dir, configPath);
    }

    private (string ExtractDir, string ConfigPath) StageExtractedPackageWithConfig(
        string xml, string fileName = "Web.config")
    {
        var dir = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, fileName);
        File.WriteAllText(configPath, xml);
        return (dir, configPath);
    }

    private static async Task<(bool Success, List<(string Level, string Message)> Logs)> Run(
        Dictionary<string, string> config,
        string extractDir,
        Dictionary<string, string>? variables = null)
    {
        var logs = new List<(string Level, string Message)>();
        var context = Context(config, extractDir, logs, variables);
        var result = await new OctopusTentaclePackageStepHandler()
            .HandleAsync(context, CancellationToken.None);
        return (result, logs);
    }

    private static StepHandlerContext Context(
        Dictionary<string, string> config,
        string extractDir,
        List<(string Level, string Message)> logs,
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
            Name: "Test TentaclePackage",
            StepType: "Octopus.TentaclePackage",
            PackageId: "TestPkg",
            PackageVersion: "1.0.0",
            Config: new ReadOnlyDictionary<string, string>(config));

        return new StepHandlerContext
        {
            Plan         = plan,
            Step         = step,
            ExtractDir   = extractDir,
            ArtifactsDir = string.IsNullOrEmpty(extractDir)
                ? string.Empty
                : Path.Combine(extractDir, "artifacts"),
            LogAsync     = (level, msg) =>
            {
                logs.Add((level, msg));
                return Task.CompletedTask;
            },
        };
    }
}
