using FluentAssertions;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for <see cref="OctopusLibraryImporter"/>.
/// These tests run without a database — <see cref="OctopusLibraryImporter.Parse"/>
/// is a pure static JSON-to-domain transformation.
/// </summary>
public sealed class OctopusLibraryImporterTests
{
    // ── Minimal valid JSON ────────────────────────────────────────────────────

    [Fact]
    public void Parse_minimal_json_creates_correct_template()
    {
        const string json = """
            {
              "Name": "Run a Script",
              "ActionType": "Octopus.Script",
              "Description": "Runs an inline PowerShell script.",
              "CommunityActionTemplateId": "abc-123",
              "Properties": {
                "Octopus.Action.Script.ScriptBody": "Write-Host 'Hello, KrakenDeploy!'",
                "Octopus.Action.Script.Syntax": "PowerShell"
              },
              "Parameters": []
            }
            """;

        var template = OctopusLibraryImporter.Parse(json, "test");

        template.Name.Should().Be("Run a Script");
        template.ActionType.Should().Be("Octopus.Script");
        template.Description.Should().Be("Runs an inline PowerShell script.");
        template.CommunityTemplateId.Should().Be("abc-123");
        template.ImportedFrom.Should().Be("test");
        template.Parameters.Should().BeEmpty();

        template.Properties.Should().ContainKey("Octopus.Action.Script.ScriptBody");
        template.Properties["Octopus.Action.Script.ScriptBody"]
            .Should().Contain("Hello, KrakenDeploy!");
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_maps_single_line_text_parameter()
    {
        const string json = """
            {
              "Name": "T",
              "ActionType": "Octopus.Script",
              "Parameters": [
                {
                  "Name": "ServerName",
                  "Label": "Server Name",
                  "HelpText": "The target server.",
                  "DefaultValue": "localhost",
                  "DisplaySettings": {
                    "Octopus.ControlType": "SingleLineText"
                  }
                }
              ]
            }
            """;

        var template = OctopusLibraryImporter.Parse(json);

        template.Parameters.Should().ContainSingle();
        var p = template.Parameters[0];
        p.Name.Should().Be("ServerName");
        p.Label.Should().Be("Server Name");
        p.HelpText.Should().Be("The target server.");
        p.DefaultValue.Should().Be("localhost");
        p.ControlType.Should().Be("SingleLineText");
        p.SelectOptions.Should().BeEmpty();
    }

    [Fact]
    public void Parse_maps_sensitive_parameter()
    {
        const string json = """
            {
              "Name": "T",
              "ActionType": "Octopus.Script",
              "Parameters": [
                {
                  "Name": "ApiKey",
                  "Label": "API Key",
                  "DisplaySettings": { "Octopus.ControlType": "Sensitive" }
                }
              ]
            }
            """;

        var template = OctopusLibraryImporter.Parse(json);

        template.Parameters.Should().ContainSingle();
        template.Parameters[0].ControlType.Should().Be("Sensitive");
    }

    [Fact]
    public void Parse_maps_select_parameter_with_options()
    {
        const string json = """
            {
              "Name": "T",
              "ActionType": "Octopus.Script",
              "Parameters": [
                {
                  "Name": "Color",
                  "Label": "Color",
                  "DisplaySettings": {
                    "Octopus.ControlType": "Select",
                    "Octopus.SelectOptions": "red|Red\ngreen|Green\nblue|Blue"
                  }
                }
              ]
            }
            """;

        var template = OctopusLibraryImporter.Parse(json);

        template.Parameters.Should().ContainSingle();
        var p = template.Parameters[0];
        p.ControlType.Should().Be("Select");
        p.SelectOptions.Should().HaveCount(3);
        // Full "value|Label" pairs are stored so the UI can show human-readable labels.
        p.SelectOptions.Should().ContainInOrder("red|Red", "green|Green", "blue|Blue");
    }

    [Fact]
    public void Parse_maps_checkbox_parameter()
    {
        const string json = """
            {
              "Name": "T",
              "ActionType": "Octopus.Script",
              "Parameters": [
                {
                  "Name": "Verbose",
                  "Label": "Enable verbose logging",
                  "DefaultValue": "False",
                  "DisplaySettings": { "Octopus.ControlType": "Checkbox" }
                }
              ]
            }
            """;

        var template = OctopusLibraryImporter.Parse(json);

        template.Parameters.Should().ContainSingle();
        template.Parameters[0].ControlType.Should().Be("Checkbox");
        template.Parameters[0].DefaultValue.Should().Be("False");
    }

    [Fact]
    public void Parse_unknown_control_type_falls_back_to_single_line_text()
    {
        const string json = """
            {
              "Name": "T",
              "ActionType": "Octopus.Script",
              "Parameters": [
                {
                  "Name": "X",
                  "Label": "X",
                  "DisplaySettings": { "Octopus.ControlType": "FutureThing" }
                }
              ]
            }
            """;

        var template = OctopusLibraryImporter.Parse(json);

        template.Parameters[0].ControlType.Should().Be("SingleLineText");
    }

    [Fact]
    public void Parse_skips_parameters_missing_name_or_label()
    {
        const string json = """
            {
              "Name": "T",
              "ActionType": "Octopus.Script",
              "Parameters": [
                { "Name": "GoodParam", "Label": "Good" },
                { "Label": "No Name Here" },
                { "Name": "NoLabel" }
              ]
            }
            """;

        var template = OctopusLibraryImporter.Parse(json);

        // Only the first parameter has both Name and Label.
        template.Parameters.Should().ContainSingle();
        template.Parameters[0].Name.Should().Be("GoodParam");
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_throws_when_Name_is_missing()
    {
        const string json = """{ "ActionType": "Octopus.Script" }""";

        var act = () => OctopusLibraryImporter.Parse(json);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Name*");
    }

    [Fact]
    public void Parse_throws_when_ActionType_is_missing()
    {
        const string json = """{ "Name": "My Template" }""";

        var act = () => OctopusLibraryImporter.Parse(json);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ActionType*");
    }

    [Fact]
    public void Parse_allows_trailing_commas_and_comments()
    {
        // Some editors / copy-paste sources include trailing commas.
        const string json = """
            {
              "Name": "T",
              "ActionType": "Octopus.Script",
              "Parameters": [],
            }
            """;

        var act = () => OctopusLibraryImporter.Parse(json);

        act.Should().NotThrow("trailing commas should be accepted by the parser");
    }

    // ── Optional fields ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_accepts_missing_optional_fields()
    {
        const string json = """{ "Name": "T", "ActionType": "Octopus.Script" }""";

        var template = OctopusLibraryImporter.Parse(json);

        template.Description.Should().BeNull();
        template.CommunityTemplateId.Should().BeNull();
        template.ImportedFrom.Should().BeNull();
        template.Properties.Should().BeEmpty();
        template.Parameters.Should().BeEmpty();
    }
}
