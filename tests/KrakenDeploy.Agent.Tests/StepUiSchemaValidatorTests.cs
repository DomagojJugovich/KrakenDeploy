using System.Text.Json.Nodes;
using FluentAssertions;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="StepUiSchemaValidator"/> (Phase C-3). Covers
/// Validate (per-constraint behaviour, empty-field handling, type coherence,
/// pattern matching, numeric range, enum membership, JSON well-formedness)
/// and the two coercion helpers (CoerceFromConfig + CoerceToConfig — round-
/// tripping the storage <-> renderer boundary).
/// </summary>
public sealed class StepUiSchemaValidatorTests
{
    // ── Validate: required ────────────────────────────────────────────────

    [Fact]
    public void Validate_required_field_with_empty_value_reports_error()
    {
        var schema = SchemaWith("name",
            type: StepUiFieldType.String,
            widget: "text",
            validation: new StepUiValidation { Required = true });

        var errors = StepUiSchemaValidator.Validate(schema, new Dictionary<string, string>());

        errors.Should().ContainSingle().Which.FieldKey.Should().Be("name");
    }

    [Fact]
    public void Validate_required_field_with_non_empty_value_passes()
    {
        var schema = SchemaWith("name",
            type: StepUiFieldType.String,
            widget: "text",
            validation: new StepUiValidation { Required = true });

        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["name"] = "Alice" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_non_required_empty_field_is_silently_accepted()
    {
        var schema = SchemaWith("note",
            type: StepUiFieldType.String,
            widget: "text",
            validation: new StepUiValidation { MinLength = 3 });
        // empty value would normally fail MinLength=3, but Required is false
        // so empties pass through without further checks.

        var errors = StepUiSchemaValidator.Validate(schema, new Dictionary<string, string>());

        errors.Should().BeEmpty();
    }

    // ── Validate: enum membership ─────────────────────────────────────────

    [Fact]
    public void Validate_value_outside_enum_options_reports_error()
    {
        var schema = SchemaWith("mode",
            type: StepUiFieldType.String,
            widget: "select",
            enumValues: [
                new() { Value = "auto", Label = "A" },
                new() { Value = "manual", Label = "M" }
            ]);

        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["mode"] = "rocket" });

        errors.Should().ContainSingle()
            .Which.Message.Should().Contain("'auto'").And.Contain("'manual'");
    }

    [Fact]
    public void Validate_value_inside_enum_options_passes()
    {
        var schema = SchemaWith("mode",
            type: StepUiFieldType.String,
            widget: "select",
            enumValues: [new() { Value = "auto", Label = "A" }]);

        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["mode"] = "auto" });

        errors.Should().BeEmpty();
    }

    // ── Validate: string length + pattern ─────────────────────────────────

    [Fact]
    public void Validate_MinLength_violation_reports_error()
    {
        var schema = SchemaWith("slug",
            StepUiFieldType.String, "text",
            validation: new StepUiValidation { MinLength = 3 });
        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["slug"] = "ab" });
        errors.Should().ContainSingle().Which.Message.Should().Contain("at least 3");
    }

    [Fact]
    public void Validate_MaxLength_violation_reports_error()
    {
        var schema = SchemaWith("slug",
            StepUiFieldType.String, "text",
            validation: new StepUiValidation { MaxLength = 5 });
        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["slug"] = "abcdef" });
        errors.Should().ContainSingle().Which.Message.Should().Contain("at most 5");
    }

    [Fact]
    public void Validate_pattern_mismatch_reports_error()
    {
        var schema = SchemaWith("slug",
            StepUiFieldType.String, "text",
            validation: new StepUiValidation { Pattern = "^[a-z]+$" });
        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["slug"] = "AB12" });
        errors.Should().ContainSingle().Which.Message.Should().Contain("pattern");
    }

    [Fact]
    public void Validate_pattern_match_passes()
    {
        var schema = SchemaWith("slug",
            StepUiFieldType.String, "text",
            validation: new StepUiValidation { Pattern = "^[a-z]+$" });
        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["slug"] = "hello" });
        errors.Should().BeEmpty();
    }

    // ── Validate: numeric range + type coherence ──────────────────────────

    [Fact]
    public void Validate_integer_min_max_violations_report_errors()
    {
        var schema = SchemaWith("port",
            StepUiFieldType.Integer, "number-input",
            validation: new StepUiValidation { Min = 1, Max = 65535 });

        StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["port"] = "0" }).Should().ContainSingle()
            .Which.Message.Should().Contain("at least 1");
        StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["port"] = "70000" }).Should().ContainSingle()
            .Which.Message.Should().Contain("at most 65535");
        StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["port"] = "443" }).Should().BeEmpty();
    }

    [Fact]
    public void Validate_non_integer_string_for_integer_field_reports_error()
    {
        var schema = SchemaWith("port", StepUiFieldType.Integer, "number-input");
        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["port"] = "not-a-number" });
        errors.Should().ContainSingle().Which.Message.Should().Contain("whole number");
    }

    [Fact]
    public void Validate_non_boolean_string_for_boolean_field_reports_error()
    {
        var schema = SchemaWith("on", StepUiFieldType.Boolean, "checkbox");
        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["on"] = "kinda" });
        errors.Should().ContainSingle().Which.Message.Should().Contain("true or false");
    }

    [Fact]
    public void Validate_malformed_json_for_array_field_reports_error()
    {
        var schema = SchemaWith("tags", StepUiFieldType.Array, "json-editor");
        var errors = StepUiSchemaValidator.Validate(schema,
            new Dictionary<string, string> { ["tags"] = "not valid json [" });
        errors.Should().ContainSingle().Which.Message.Should().Contain("not valid JSON");
    }

    // ── CoerceFromConfig ──────────────────────────────────────────────────

    [Fact]
    public void CoerceFromConfig_converts_strings_to_typed_JsonNodes()
    {
        var schema = new StepUiSchema
        {
            Id = "t", Title = "t", Version = "1.0.0",
            Properties = new Dictionary<string, StepUiField>
            {
                ["name"]   = new() { Type = StepUiFieldType.String,  Widget = "text" },
                ["enabled"] = new() { Type = StepUiFieldType.Boolean, Widget = "checkbox" },
                ["port"]   = new() { Type = StepUiFieldType.Integer, Widget = "number-input" },
                ["ratio"]  = new() { Type = StepUiFieldType.Number,  Widget = "number-input" },
                ["tags"]   = new() { Type = StepUiFieldType.Array,   Widget = "json-editor" },
            },
        };
        var config = new Dictionary<string, string>
        {
            ["name"]    = "Alice",
            ["enabled"] = "true",
            ["port"]    = "443",
            ["ratio"]   = "1.5",
            ["tags"]    = """["a","b"]""",
        };

        var typed = StepUiSchemaValidator.CoerceFromConfig(schema, config);

        typed["name"]!.GetValue<string>().Should().Be("Alice");
        typed["enabled"]!.GetValue<bool>().Should().BeTrue();
        typed["port"]!.GetValue<long>().Should().Be(443);
        typed["ratio"]!.GetValue<double>().Should().Be(1.5);
        typed["tags"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public void CoerceFromConfig_uses_default_when_value_missing()
    {
        var schema = new StepUiSchema
        {
            Id = "t", Title = "t", Version = "1.0.0",
            Properties = new Dictionary<string, StepUiField>
            {
                ["port"] = new() { Type = StepUiFieldType.Integer, Widget = "number-input", Default = "80" },
            },
        };

        var typed = StepUiSchemaValidator.CoerceFromConfig(schema, new Dictionary<string, string>());

        typed["port"]!.GetValue<long>().Should().Be(80,
            "missing values fall back to the schema's Default");
    }

    [Fact]
    public void CoerceFromConfig_falls_back_to_zero_default_for_unparsable_integer()
    {
        var schema = SchemaWith("port", StepUiFieldType.Integer, "number-input");
        var typed = StepUiSchemaValidator.CoerceFromConfig(schema,
            new Dictionary<string, string> { ["port"] = "garbage" });
        typed["port"]!.GetValue<long>().Should().Be(0L);
    }

    // ── CoerceToConfig ────────────────────────────────────────────────────

    [Fact]
    public void CoerceToConfig_emits_string_form_for_each_field_type()
    {
        var schema = new StepUiSchema
        {
            Id = "t", Title = "t", Version = "1.0.0",
            Properties = new Dictionary<string, StepUiField>
            {
                ["name"]   = new() { Type = StepUiFieldType.String,  Widget = "text" },
                ["on"]     = new() { Type = StepUiFieldType.Boolean, Widget = "checkbox" },
                ["count"]  = new() { Type = StepUiFieldType.Integer, Widget = "number-input" },
                ["tags"]   = new() { Type = StepUiFieldType.Array,   Widget = "json-editor" },
            },
        };

        var typed = new JsonObject
        {
            ["name"]  = "hello",
            ["on"]    = true,
            ["count"] = 42L,
            ["tags"]  = new JsonArray("a", "b"),
        };

        var config = StepUiSchemaValidator.CoerceToConfig(schema, typed);

        config["name"].Should().Be("hello");
        config["on"].Should().Be("true");
        config["count"].Should().Be("42");
        config["tags"].Should().Be("""["a","b"]""");
    }

    [Fact]
    public void CoerceToConfig_skips_missing_keys_in_the_typed_form()
    {
        var schema = new StepUiSchema
        {
            Id = "t", Title = "t", Version = "1.0.0",
            Properties = new Dictionary<string, StepUiField>
            {
                ["a"] = new() { Type = StepUiFieldType.String, Widget = "text" },
                ["b"] = new() { Type = StepUiFieldType.String, Widget = "text" },
            },
        };

        var typed = new JsonObject { ["a"] = "set" };
        var config = StepUiSchemaValidator.CoerceToConfig(schema, typed);

        config.Should().ContainKey("a").WhoseValue.Should().Be("set");
        config.Should().NotContainKey("b");
    }

    // ── Round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void CoerceFromConfig_then_CoerceToConfig_round_trips_typed_values()
    {
        var schema = new StepUiSchema
        {
            Id = "t", Title = "t", Version = "1.0.0",
            Properties = new Dictionary<string, StepUiField>
            {
                ["name"]    = new() { Type = StepUiFieldType.String,  Widget = "text" },
                ["enabled"] = new() { Type = StepUiFieldType.Boolean, Widget = "checkbox" },
                ["port"]    = new() { Type = StepUiFieldType.Integer, Widget = "number-input" },
            },
        };
        var input = new Dictionary<string, string>
        {
            ["name"]    = "hello",
            ["enabled"] = "true",
            ["port"]    = "443",
        };

        var roundTrip = StepUiSchemaValidator.CoerceToConfig(
            schema, StepUiSchemaValidator.CoerceFromConfig(schema, input));

        roundTrip.Should().BeEquivalentTo(input);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static StepUiSchema SchemaWith(
        string key,
        StepUiFieldType type,
        string widget,
        IReadOnlyList<StepUiEnumValue>? enumValues = null,
        StepUiValidation? validation = null) =>
        new()
        {
            Id      = "t",
            Title   = "t",
            Version = "1.0.0",
            Properties = new Dictionary<string, StepUiField>
            {
                [key] = new()
                {
                    Type        = type,
                    Widget      = widget,
                    EnumValues  = enumValues ?? [],
                    Validation  = validation,
                },
            },
        };
}
