using FluentAssertions;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for the pure-static <see cref="OctopusDeploymentProcessImporter.Parse"/>.
/// No database is required. Two of the tests load real Octopus deploymentprocess
/// exports from <c>TestData/</c> to exercise the parser against production-shape
/// JSON.
/// </summary>
public sealed class OctopusDeploymentProcessImporterTests
{
    // ── Error cases ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_throws_on_malformed_json()
    {
        var act = () => OctopusDeploymentProcessImporter.Parse("not { valid } json {");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*could not be parsed*");
    }

    [Fact]
    public void Parse_throws_when_Steps_array_is_missing()
    {
        var act = () => OctopusDeploymentProcessImporter.Parse("""{ "Id": "x", "Version": 1 }""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Steps*");
    }

    // ── Single-step happy path ────────────────────────────────────────────

    [Fact]
    public void Parse_extracts_action_type_and_target_roles()
    {
        const string json = """
        {
          "Steps": [
            {
              "Name": "Deploy ArgosyBase",
              "Properties": { "Octopus.Action.TargetRoles": "SERVER, WORKSTATION" },
              "Actions": [
                {
                  "Name": "ArgosyBase",
                  "ActionType": "Octopus.TentaclePackage",
                  "Properties": { "Octopus.Action.Package.PackageId": "ArgosyBase" },
                  "Packages": [
                    { "PackageId": "ArgosyBase", "FeedId": "feeds-builtin" }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().HaveCount(1);
        var step = result.Steps[0];
        step.Name.Should().Be("Deploy ArgosyBase");
        step.StepType.Should().Be("Octopus.TentaclePackage");
        step.PackageId.Should().Be("ArgosyBase");
        step.TargetRoles.Should().Equal("SERVER", "WORKSTATION");
        step.Config.Should().ContainKey("Octopus.Action.Package.PackageId");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_preserves_properties_verbatim_including_json_in_string_bindings()
    {
        const string bindingsJson = """[{"protocol":"http","ipAddress":"*","port":"80","host":"#{VirtualHost}","thumbprint":null,"certificateVariable":null,"requireSni":false,"enabled":true}]""";

        var json = $$"""
        {
          "Steps": [
            {
              "Name": "Configure IIS",
              "Actions": [
                {
                  "Name": "Configure IIS",
                  "ActionType": "Octopus.IIS",
                  "Properties": {
                    "Octopus.Action.IISWebSite.WebSiteName": "MySite",
                    "Octopus.Action.IISWebSite.Bindings": {{System.Text.Json.JsonSerializer.Serialize(bindingsJson)}}
                  }
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().HaveCount(1);
        result.Steps[0].Config["Octopus.Action.IISWebSite.Bindings"]
            .Should().Be(bindingsJson, "the bindings JSON-in-string must round-trip byte-for-byte");
    }

    [Fact]
    public void Parse_strips_dummy_package_sentinel()
    {
        const string json = """
        {
          "Steps": [
            {
              "Name": "IIS only",
              "Actions": [
                {
                  "Name": "IIS only",
                  "ActionType": "Octopus.IIS",
                  "Properties": { "Octopus.Action.Package.PackageId": "dummy" },
                  "Packages": [
                    { "PackageId": "dummy", "FeedId": "feeds-builtin" }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps[0].PackageId.Should().BeEmpty(
            "the 'dummy' sentinel is stripped from DeploymentStep.PackageId");
        result.Steps[0].Config["Octopus.Action.Package.PackageId"]
            .Should().Be("dummy",
                "the verbatim 'dummy' is still preserved in Config for round-trip");
    }

    // ── Skip + warning paths ──────────────────────────────────────────────

    [Fact]
    public void Parse_skips_step_with_no_actions_and_warns()
    {
        const string json = """
        {
          "Steps": [
            { "Name": "Empty step", "Actions": [] }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps.Should().BeEmpty();
        result.Warnings.Should().ContainSingle(w => w.StepName == "Empty step");
    }

    [Fact]
    public void Parse_skips_disabled_action_and_warns()
    {
        const string json = """
        {
          "Steps": [
            {
              "Name": "Disabled step",
              "Actions": [
                { "Name": "x", "ActionType": "Octopus.Script", "IsDisabled": true }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps.Should().BeEmpty();
        result.Warnings.Should().ContainSingle(w => w.Message.Contains("disabled"));
    }

    [Fact]
    public void Parse_skips_step_with_parallel_actions_and_warns()
    {
        const string json = """
        {
          "Steps": [
            {
              "Name": "Parallel",
              "Actions": [
                { "Name": "a", "ActionType": "Octopus.Script" },
                { "Name": "b", "ActionType": "Octopus.Script" }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps.Should().BeEmpty();
        result.Warnings.Should().ContainSingle(w => w.Message.Contains("parallel"));
    }

    [Fact]
    public void Parse_warns_about_tenant_tags_but_still_imports()
    {
        const string json = """
        {
          "Steps": [
            {
              "Name": "TenantScoped",
              "Actions": [
                {
                  "Name": "x",
                  "ActionType": "Octopus.Script",
                  "TenantTags": ["PackagesForTenants/ArgosyBASE"]
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().HaveCount(1);
        result.Warnings.Should().ContainSingle(w => w.Message.Contains("tenant tags"));
    }

    [Fact]
    public void Parse_warns_about_worker_pool_and_container_but_still_imports()
    {
        const string json = """
        {
          "Steps": [
            {
              "Name": "Containerised",
              "Actions": [
                {
                  "Name": "x",
                  "ActionType": "Octopus.Script",
                  "WorkerPoolId": "WorkerPools-1",
                  "Container": { "Image": "mcr.microsoft.com/dotnet/sdk:9.0" }
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().HaveCount(1);
        result.Warnings.Should().Contain(w => w.Message.Contains("worker pool"));
        result.Warnings.Should().Contain(w => w.Message.Contains("container"));
    }

    // ── Real exports ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_real_argosy_process_export_imports_all_tentaclepackage_steps()
    {
        var json = LoadTestData("argosy-process.json");

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().NotBeEmpty();

        var byType = result.Steps.GroupBy(s => s.StepType).ToDictionary(g => g.Key, g => g.Count());
        byType.Should().ContainKey("Octopus.TentaclePackage");
        byType["Octopus.TentaclePackage"].Should().BeGreaterThan(20,
            "the Argosy process contains many TentaclePackage steps");

        var tentacleStep = result.Steps.First(s => s.StepType == "Octopus.TentaclePackage");
        tentacleStep.Config.Should().ContainKey("Octopus.Action.Package.PackageId");
        tentacleStep.Config.Should().ContainKey("Octopus.Action.EnabledFeatures");
    }

    [Fact]
    public void Parse_real_webargosy_export_preserves_iis_keys_and_bindings()
    {
        var json = LoadTestData("webargosy-virtual-app.json");

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().NotBeEmpty();

        var iisStep = result.Steps.FirstOrDefault(s => s.StepType == "Octopus.IIS");
        iisStep.Should().NotBeNull("the webargosy process is expected to contain at least one Octopus.IIS step");

        iisStep!.Config.Should().ContainKey("Octopus.Action.EnabledFeatures");
        iisStep.Config["Octopus.Action.EnabledFeatures"]
            .Should().Contain("Octopus.Features.IISWebSite");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string LoadTestData(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Test data file not found: {path}. " +
                "Ensure TestData/*.json files are configured as CopyToOutputDirectory.", path);
        }
        return File.ReadAllText(path);
    }
}
