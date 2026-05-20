using FluentAssertions;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for <see cref="StepTemplateSchemaAdapter"/> (Phase C-6). Pure
/// in-process — no DB required. The bridge is a static mapper so we exercise
/// it directly with hand-crafted <see cref="StepTemplate"/> and
/// <see cref="StepTemplateParameter"/> instances.
/// </summary>
public sealed class StepTemplateSchemaAdapterTests
{
    // ── ControlType -> widget mapping (one test per branch) ───────────────

    [Theory]
    [InlineData("SingleLineText", StepUiWidgets.Text)]
    [InlineData("singlelinetext", StepUiWidgets.Text)]   // case insensitive
    [InlineData("MultiLineText",  StepUiWidgets.Textarea)]
    [InlineData("Sensitive",      StepUiWidgets.Sensitive)]
    [InlineData("Checkbox",       StepUiWidgets.Checkbox)]
    [InlineData("Select",         StepUiWidgets.Select)]
    [InlineData("Package",        StepUiWidgets.PackageRef)]
    [InlineData("unknown",        StepUiWidgets.Text)]   // defensive fallback
    [InlineData("",               StepUiWidgets.Text)]
    [InlineData(null,             StepUiWidgets.Text)]
    public void Maps_every_ControlType_to_the_expected_widget(string? controlType, string expectedWidget)
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(new StepTemplate
        {
            Name = "T",
            ActionType = "Octopus.Script",
            Parameters =
            [
                new() { Name = "p", Label = "P", ControlType = controlType ?? "" },
            ],
        });

        schema.Properties["p"].Widget.Should().Be(expectedWidget);
    }

    [Fact]
    public void Checkbox_widget_yields_Boolean_field_type()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(NewTemplate(
            new StepTemplateParameter { Name = "flag", Label = "Flag", ControlType = "Checkbox" }));
        schema.Properties["flag"].Type.Should().Be(StepUiFieldType.Boolean);
    }

    [Fact]
    public void Every_other_widget_yields_String_field_type()
    {
        // Legacy templates store every non-checkbox value as a string in the
        // config bag, so the schema models them as String regardless of
        // numeric / sensitive flavour.
        var schema = StepTemplateSchemaAdapter.BuildSchema(new StepTemplate
        {
            Name = "T", ActionType = "x",
            Parameters =
            [
                new() { Name = "t", Label = "T", ControlType = "SingleLineText" },
                new() { Name = "m", Label = "M", ControlType = "MultiLineText" },
                new() { Name = "s", Label = "S", ControlType = "Sensitive" },
                new() { Name = "p", Label = "P", ControlType = "Package" },
            ],
        });

        schema.Properties["t"].Type.Should().Be(StepUiFieldType.String);
        schema.Properties["m"].Type.Should().Be(StepUiFieldType.String);
        schema.Properties["s"].Type.Should().Be(StepUiFieldType.String);
        schema.Properties["p"].Type.Should().Be(StepUiFieldType.String);
    }

    // ── Field-level metadata propagation ──────────────────────────────────

    [Fact]
    public void Label_HelpText_and_DefaultValue_propagate_into_the_schema_field()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(NewTemplate(
            new StepTemplateParameter
            {
                Name         = "ServerName",
                Label        = "Database server",
                HelpText     = "Hostname or IP of the DB server.",
                DefaultValue = "localhost",
                ControlType  = "SingleLineText",
            }));

        var f = schema.Properties["ServerName"];
        f.Label.Should().Be("Database server");
        f.HelpText.Should().Be("Hostname or IP of the DB server.");
        f.Default.Should().Be("localhost");
    }

    [Fact]
    public void Legacy_parameters_never_carry_Validation_VisibleWhen_or_Group_metadata()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(NewTemplate(
            new StepTemplateParameter
            {
                Name = "n", Label = "L", ControlType = "SingleLineText",
            }));

        var f = schema.Properties["n"];
        f.Validation.Should().BeNull(
            "the legacy StepTemplateParameter shape doesn't model validation constraints");
        f.VisibleWhen.Should().BeNull(
            "conditional visibility wasn't part of the legacy shape");
        f.Group.Should().BeNull(
            "groups weren't part of the legacy shape");
    }

    // ── SelectOptions → EnumValues ────────────────────────────────────────

    [Fact]
    public void SelectOptions_with_pipe_split_into_value_and_label()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(NewTemplate(
            new StepTemplateParameter
            {
                Name = "mode", Label = "Mode", ControlType = "Select",
                SelectOptions = ["auto|Automatic", "manual|Manual"],
            }));

        var f = schema.Properties["mode"];
        f.Widget.Should().Be(StepUiWidgets.Select);
        f.EnumValues.Should().HaveCount(2);
        f.EnumValues[0].Should().BeEquivalentTo(
            new StepUiEnumValue { Value = "auto",   Label = "Automatic" });
        f.EnumValues[1].Should().BeEquivalentTo(
            new StepUiEnumValue { Value = "manual", Label = "Manual" });
    }

    [Fact]
    public void SelectOptions_without_pipe_use_the_same_string_for_value_and_label()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(NewTemplate(
            new StepTemplateParameter
            {
                Name = "mode", Label = "Mode", ControlType = "Select",
                SelectOptions = ["auto", "manual"],
            }));

        var f = schema.Properties["mode"];
        f.EnumValues[0].Value.Should().Be("auto");
        f.EnumValues[0].Label.Should().Be("auto");
        f.EnumValues[1].Value.Should().Be("manual");
        f.EnumValues[1].Label.Should().Be("manual");
    }

    [Fact]
    public void SelectOptions_with_whitespace_are_trimmed()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(NewTemplate(
            new StepTemplateParameter
            {
                Name = "mode", Label = "Mode", ControlType = "Select",
                SelectOptions = ["  auto  |  Automatic  "],
            }));

        var f = schema.Properties["mode"];
        f.EnumValues.Should().ContainSingle();
        f.EnumValues[0].Value.Should().Be("auto");
        f.EnumValues[0].Label.Should().Be("Automatic");
    }

    [Fact]
    public void Non_select_widgets_ignore_SelectOptions()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(NewTemplate(
            new StepTemplateParameter
            {
                Name = "n", Label = "N", ControlType = "SingleLineText",
                SelectOptions = ["should|be ignored"],
            }));

        schema.Properties["n"].EnumValues.Should().BeEmpty();
    }

    // ── Schema-root metadata ──────────────────────────────────────────────

    [Fact]
    public void Schema_root_uses_template_ActionType_as_id_lowercased()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(new StepTemplate
        {
            Name = "Run a Script",
            ActionType = "Octopus.Script",
        });

        schema.Id.Should().Be("octopus.script");
        schema.Title.Should().Be("Run a Script");
    }

    [Fact]
    public void Schema_root_description_falls_back_to_null_when_template_has_none()
    {
        var schema = StepTemplateSchemaAdapter.BuildSchema(new StepTemplate
        {
            Name = "T", ActionType = "x",
        });
        schema.Description.Should().BeNull();
    }

    [Fact]
    public void Schema_root_uses_empty_id_fallback_when_ActionType_is_blank()
    {
        // Defensive: the static mapper shouldn't throw for a malformed
        // template — return a placeholder id so the renderer can still
        // load the schema, and a follow-up audit can correct the template.
        var schema = StepTemplateSchemaAdapter.BuildSchema(new StepTemplate
        {
            Name = "Bad Template", ActionType = "",
        });
        schema.Id.Should().Be("kraken.legacy-template");
    }

    // ── End-to-end: full StepTemplate → schema → JSON round-trip ──────────

    [Fact]
    public void BuildSchema_then_serialize_and_deserialize_round_trips()
    {
        var template = new StepTemplate
        {
            Name        = "File System - Create Folders",
            Description = "Ensure/Create multiple folders separated by ;",
            ActionType  = "Octopus.Script",
            Parameters  =
            [
                new()
                {
                    Name         = "FolderPaths",
                    Label        = "Folders",
                    HelpText     = "Semicolon-separated.",
                    DefaultValue = "",
                    ControlType  = "MultiLineText",
                },
                new()
                {
                    Name         = "OverwriteExisting",
                    Label        = "Overwrite existing",
                    ControlType  = "Checkbox",
                    DefaultValue = "false",
                },
                new()
                {
                    Name         = "Mode",
                    Label        = "Mode",
                    ControlType  = "Select",
                    SelectOptions = ["strict|Strict", "lenient|Lenient"],
                    DefaultValue = "lenient",
                },
            ],
        };

        var original = StepTemplateSchemaAdapter.BuildSchema(template);
        var json     = StepUiSchemaJson.Serialize(original);
        var parsed   = StepUiSchemaJson.Deserialize(json);

        parsed.Should().BeEquivalentTo(original);
        parsed.Properties.Should().HaveCount(3);
        parsed.Properties["FolderPaths"].Widget.Should().Be(StepUiWidgets.Textarea);
        parsed.Properties["OverwriteExisting"].Widget.Should().Be(StepUiWidgets.Checkbox);
        parsed.Properties["Mode"].Widget.Should().Be(StepUiWidgets.Select);
        parsed.Properties["Mode"].EnumValues.Should().HaveCount(2);
    }

    // ── BuildPropertyMap lower-level entry point ──────────────────────────

    [Fact]
    public void BuildPropertyMap_works_directly_on_a_parameter_list()
    {
        var map = StepTemplateSchemaAdapter.BuildPropertyMap(
        [
            new StepTemplateParameter { Name = "a", Label = "A", ControlType = "SingleLineText" },
            new StepTemplateParameter { Name = "b", Label = "B", ControlType = "Checkbox" },
        ]);

        map.Should().HaveCount(2);
        map["a"].Widget.Should().Be(StepUiWidgets.Text);
        map["b"].Widget.Should().Be(StepUiWidgets.Checkbox);
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private static StepTemplate NewTemplate(params StepTemplateParameter[] parameters) => new()
    {
        Name        = "T",
        ActionType  = "Octopus.Script",
        Parameters  = [.. parameters],
    };
}
