using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for the <c>StepUiSchema</c> IR (Phase C-1). Covers:
/// (a) record construction with sensible defaults, (b) JSON round-trip
/// through <see cref="StepUiSchemaJson"/>, (c) edge cases the renderer
/// (Phase C-4) and validator (Phase C-3) will rely on.
/// </summary>
public sealed class StepUiSchemaTests
{
    // ── Construction defaults ──────────────────────────────────────────────

    [Fact]
    public void StepUiSchema_required_fields_only_yields_sensible_defaults()
    {
        var schema = new StepUiSchema
        {
            Id      = "test.step",
            Title   = "Test step",
            Version = "1.0.0",
        };

        schema.Description.Should().BeNull();
        schema.Groups.Should().BeEmpty();
        schema.Properties.Should().BeEmpty();
    }

    [Fact]
    public void StepUiField_required_fields_only_yields_sensible_defaults()
    {
        var f = new StepUiField
        {
            Type   = StepUiFieldType.String,
            Widget = StepUiWidgets.Text,
        };

        f.Label.Should().BeNull();
        f.HelpText.Should().BeNull();
        f.Placeholder.Should().BeNull();
        f.Group.Should().BeNull();
        f.Default.Should().BeNull();
        f.EnumValues.Should().BeEmpty();
        f.VisibleWhen.Should().BeNull();
        f.Validation.Should().BeNull();
        f.ItemSchema.Should().BeNull();
        f.Properties.Should().BeNull();
    }

    // ── JSON round-trip ────────────────────────────────────────────────────

    [Fact]
    public void Round_trip_a_minimal_schema_preserves_required_fields()
    {
        var original = new StepUiSchema
        {
            Id      = "kraken.minimal",
            Title   = "Minimal step",
            Version = "1.0.0",
        };

        var json = StepUiSchemaJson.Serialize(original);
        var roundtripped = StepUiSchemaJson.Deserialize(json);

        roundtripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Round_trip_a_realistic_schema_preserves_every_property()
    {
        var original = new StepUiSchema
        {
            Id          = "kraken.iis",
            Title       = "Kraken IIS",
            Description = "Configure an IIS site, app pool, and bindings.",
            Version     = "2.0.0",
            Groups = new[]
            {
                new StepUiGroup { Id = "site",     Title = "Web site" },
                new StepUiGroup { Id = "app-pool", Title = "App pool", Description = "Process model and identity." },
                new StepUiGroup { Id = "recycle",  Title = "Recycling", Collapsed = true },
            },
            Properties = new Dictionary<string, StepUiField>
            {
                ["Kraken.IIS.SiteName"] = new()
                {
                    Type     = StepUiFieldType.String,
                    Widget   = StepUiWidgets.Text,
                    Label    = "Site name",
                    HelpText = "The IIS site to create or update.",
                    Group    = "site",
                    Validation = new StepUiValidation { Required = true, MaxLength = 255 },
                },
                ["Kraken.IIS.AppPool.IdentityType"] = new()
                {
                    Type    = StepUiFieldType.String,
                    Widget  = StepUiWidgets.Select,
                    Label   = "App pool identity",
                    Group   = "app-pool",
                    Default = "ApplicationPoolIdentity",
                    EnumValues = new[]
                    {
                        new StepUiEnumValue { Value = "ApplicationPoolIdentity", Label = "ApplicationPoolIdentity" },
                        new StepUiEnumValue { Value = "LocalSystem",             Label = "LocalSystem" },
                        new StepUiEnumValue { Value = "SpecificUser",            Label = "Specific user" },
                    },
                },
                ["Kraken.IIS.AppPool.Username"] = new()
                {
                    Type        = StepUiFieldType.String,
                    Widget      = StepUiWidgets.Text,
                    Label       = "Username",
                    Group       = "app-pool",
                    VisibleWhen = new StepUiVisibleWhen
                    {
                        Field    = "Kraken.IIS.AppPool.IdentityType",
                        Operator = "equals",
                        Value    = "SpecificUser",
                    },
                },
                ["Kraken.IIS.AppPool.Password"] = new()
                {
                    Type        = StepUiFieldType.String,
                    Widget      = StepUiWidgets.Sensitive,
                    Label       = "Password",
                    Group       = "app-pool",
                    VisibleWhen = new StepUiVisibleWhen
                    {
                        Field    = "Kraken.IIS.AppPool.IdentityType",
                        Operator = "equals",
                        Value    = "SpecificUser",
                    },
                },
                ["Kraken.IIS.Bindings"] = new()
                {
                    Type   = StepUiFieldType.Array,
                    Widget = StepUiWidgets.JsonEditor,
                    Label  = "Bindings",
                    ItemSchema = new StepUiField
                    {
                        Type   = StepUiFieldType.Object,
                        Widget = StepUiWidgets.JsonEditor,
                        Properties = new Dictionary<string, StepUiField>
                        {
                            ["protocol"] = new()
                            {
                                Type   = StepUiFieldType.String,
                                Widget = StepUiWidgets.Select,
                                EnumValues = new[]
                                {
                                    new StepUiEnumValue { Value = "http",  Label = "HTTP"  },
                                    new StepUiEnumValue { Value = "https", Label = "HTTPS" },
                                },
                            },
                            ["port"] = new()
                            {
                                Type       = StepUiFieldType.Integer,
                                Widget     = StepUiWidgets.NumberInput,
                                Validation = new StepUiValidation { Min = 1, Max = 65535 },
                            },
                        },
                    },
                },
                ["Kraken.IIS.Recycle.RegularTimeIntervalMinutes"] = new()
                {
                    Type       = StepUiFieldType.Integer,
                    Widget     = StepUiWidgets.NumberInput,
                    Label      = "Regular recycle interval (minutes)",
                    Group      = "recycle",
                    Default    = "1740",
                    Validation = new StepUiValidation { Min = 0 },
                },
                ["Kraken.IIS.AlwaysRunning"] = new()
                {
                    Type    = StepUiFieldType.Boolean,
                    Widget  = StepUiWidgets.Checkbox,
                    Label   = "Always-running app pool",
                    Group   = "app-pool",
                    Default = "false",
                },
            },
        };

        var json = StepUiSchemaJson.Serialize(original);
        var roundtripped = StepUiSchemaJson.Deserialize(json);

        roundtripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Serialization_uses_camelCase_property_names()
    {
        // The JSON convention is camelCase to match JSON Schema / TypeScript
        // tooling — important for the future authoring story where step
        // packages may include a hand-edited ui-schema.json.
        var schema = new StepUiSchema
        {
            Id          = "test",
            Title       = "T",
            Description = "Desc",
            Version     = "1.0.0",
        };

        var json = StepUiSchemaJson.Serialize(schema);

        json.Should().Contain("\"id\":");
        json.Should().Contain("\"title\":");
        json.Should().Contain("\"description\":");
        json.Should().Contain("\"version\":");
        json.Should().NotContain("\"Id\":");
    }

    [Fact]
    public void Field_type_enum_serializes_as_camelCase_string()
    {
        var schema = new StepUiSchema
        {
            Id      = "t",
            Title   = "T",
            Version = "1.0.0",
            Properties = new Dictionary<string, StepUiField>
            {
                ["x"] = new() { Type = StepUiFieldType.Integer, Widget = StepUiWidgets.NumberInput },
            },
        };

        var json = StepUiSchemaJson.Serialize(schema);

        json.Should().Contain("\"type\": \"integer\"",
            "the JsonStringEnumConverter<StepUiFieldType> emits the enum as a string in camelCase");
    }

    [Fact]
    public void Null_optional_fields_are_omitted_from_serialised_output()
    {
        // DefaultIgnoreCondition = WhenWritingNull keeps the JSON terse.
        var schema = new StepUiSchema
        {
            Id      = "t",
            Title   = "T",
            Version = "1.0.0",
        };

        var json = StepUiSchemaJson.Serialize(schema);

        json.Should().NotContain("\"description\"",
            "Description is null and should be omitted");
        json.Should().NotContain("\"placeholder\"");
    }

    [Fact]
    public void Deserialize_throws_InvalidOperation_on_null_json_body()
    {
        var act = () => StepUiSchemaJson.Deserialize("null");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*deserialised to null*");
    }

    [Fact]
    public void Deserialize_tolerates_comments_and_trailing_commas()
    {
        // Both Skip comments and AllowTrailingCommas are configured on the
        // canonical JsonSerializerOptions — useful for hand-authored
        // ui-schema.json files inside step packages.
        var json = """
            // step-package ui-schema for kraken.iis 2.0.0
            {
                "id": "kraken.iis",
                "title": "Kraken IIS",
                "version": "2.0.0",
                "properties": {
                    "Kraken.IIS.SiteName": {
                        "type": "string",
                        "widget": "text",
                        "label": "Site name",
                    },
                },
            }
            """;

        var schema = StepUiSchemaJson.Deserialize(json);

        schema.Id.Should().Be("kraken.iis");
        schema.Properties.Should().ContainKey("Kraken.IIS.SiteName");
        schema.Properties["Kraken.IIS.SiteName"].Label.Should().Be("Site name");
    }

    [Fact]
    public void VisibleWhen_truthy_and_falsy_operators_can_omit_Value()
    {
        // truthy / falsy don't need a comparison value; the renderer just
        // checks whether the referenced field is set / unset.
        var v = new StepUiVisibleWhen { Field = "Enabled", Operator = "truthy" };
        var json = JsonSerializer.Serialize(v, StepUiSchemaJson.Options);
        json.Should().Contain("\"operator\": \"truthy\"");
        json.Should().NotContain("\"value\":");
    }

    [Fact]
    public void StepUiGroup_collapsed_defaults_to_false()
    {
        var g = new StepUiGroup { Id = "g", Title = "G" };
        g.Collapsed.Should().BeFalse();
    }
}
