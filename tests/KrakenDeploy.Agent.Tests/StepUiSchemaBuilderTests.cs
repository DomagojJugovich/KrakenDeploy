using FluentAssertions;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="StepUiSchemaBuilder.FromType"/> (Phase C-2). Drives
/// the reflection-based authoring path with hand-crafted POCO classes and
/// asserts the produced <see cref="StepUiSchema"/> tree matches what the
/// renderer expects.
/// </summary>
public sealed class StepUiSchemaBuilderTests
{
    // ── Root attribute discovery ──────────────────────────────────────────

    [Fact]
    public void FromType_reads_required_root_attribute_fields()
    {
        var schema = StepUiSchemaBuilder.FromType<MinimalSchema>();

        schema.Id.Should().Be("kraken.minimal");
        schema.Title.Should().Be("Minimal step");
        schema.Version.Should().Be("1.0.0");
        schema.Description.Should().Be("An empty step for testing.");
    }

    [Fact]
    public void FromType_throws_when_root_attribute_missing()
    {
        var act = () => StepUiSchemaBuilder.FromType<UnrootedPoco>();
        act.Should().Throw<InvalidOperationException>().WithMessage("*StepUiSchemaRoot*");
    }

    // ── Groups ────────────────────────────────────────────────────────────

    [Fact]
    public void FromType_collects_groups_in_declaration_order()
    {
        var schema = StepUiSchemaBuilder.FromType<GroupedSchema>();

        schema.Groups.Should().HaveCount(3);
        schema.Groups[0].Id.Should().Be("a");
        schema.Groups[1].Id.Should().Be("b");
        schema.Groups[2].Id.Should().Be("c");
        schema.Groups[2].Collapsed.Should().BeTrue(
            "the 'c' group is declared collapsed");
    }

    // ── Field inference ──────────────────────────────────────────────────

    [Fact]
    public void FromType_emits_one_field_per_decorated_property_using_property_name_when_no_Key()
    {
        var schema = StepUiSchemaBuilder.FromType<PrimitiveTypesSchema>();

        schema.Properties.Should().ContainKey("Name");
        schema.Properties.Should().ContainKey("Enabled");
        schema.Properties.Should().ContainKey("Port");
        schema.Properties.Should().ContainKey("Timeout");
        // Properties without [StepUiField] are excluded.
        schema.Properties.Should().NotContainKey("Ignored");
    }

    [Fact]
    public void FromType_uses_Key_override_for_dotted_config_bag_keys()
    {
        var schema = StepUiSchemaBuilder.FromType<DottedKeySchema>();

        schema.Properties.Should().ContainKey("Kraken.IIS.SiteName");
        schema.Properties.Should().NotContainKey("SiteName",
            "the C# property name is overridden by the Key annotation");
    }

    [Fact]
    public void FromType_infers_StepUiFieldType_from_property_clr_type()
    {
        var schema = StepUiSchemaBuilder.FromType<PrimitiveTypesSchema>();

        schema.Properties["Name"].Type.Should().Be(StepUiFieldType.String);
        schema.Properties["Enabled"].Type.Should().Be(StepUiFieldType.Boolean);
        schema.Properties["Port"].Type.Should().Be(StepUiFieldType.Integer);
        schema.Properties["Timeout"].Type.Should().Be(StepUiFieldType.Number);
    }

    [Fact]
    public void FromType_treats_nullable_value_types_as_their_underlying_type()
    {
        var schema = StepUiSchemaBuilder.FromType<NullableTypesSchema>();
        schema.Properties["MaybeInt"].Type.Should().Be(StepUiFieldType.Integer);
        schema.Properties["MaybeBool"].Type.Should().Be(StepUiFieldType.Boolean);
    }

    [Fact]
    public void FromType_treats_IEnumerable_property_as_Array()
    {
        var schema = StepUiSchemaBuilder.FromType<EnumerableSchema>();
        schema.Properties["Tags"].Type.Should().Be(StepUiFieldType.Array);
    }

    // ── Enum options ──────────────────────────────────────────────────────

    [Fact]
    public void FromType_collects_repeating_StepUiEnum_attributes_into_EnumValues()
    {
        var schema = StepUiSchemaBuilder.FromType<EnumSchema>();
        var f = schema.Properties["IdentityType"];

        f.EnumValues.Should().HaveCount(3);
        f.EnumValues[0].Should().BeEquivalentTo(
            new StepUiEnumValue { Value = "ApplicationPoolIdentity", Label = "ApplicationPoolIdentity" });
        f.EnumValues[1].Should().BeEquivalentTo(
            new StepUiEnumValue { Value = "LocalSystem", Label = "LocalSystem" });
        f.EnumValues[2].Should().BeEquivalentTo(
            new StepUiEnumValue { Value = "SpecificUser", Label = "Specific user" });
    }

    // ── Visible-when ──────────────────────────────────────────────────────

    [Fact]
    public void FromType_emits_VisibleWhen_when_attribute_present()
    {
        var schema = StepUiSchemaBuilder.FromType<VisibleWhenSchema>();

        var pwd = schema.Properties["Password"];
        pwd.VisibleWhen.Should().NotBeNull();
        pwd.VisibleWhen!.Field.Should().Be("IdentityType");
        pwd.VisibleWhen.Operator.Should().Be("equals");
        pwd.VisibleWhen.Value.Should().Be("SpecificUser");

        // The 'IdentityType' field itself has no VisibleWhen.
        schema.Properties["IdentityType"].VisibleWhen.Should().BeNull();
    }

    // ── Validation ────────────────────────────────────────────────────────

    [Fact]
    public void FromType_no_validation_attributes_means_null_Validation_block()
    {
        var schema = StepUiSchemaBuilder.FromType<PrimitiveTypesSchema>();
        schema.Properties["Name"].Validation.Should().BeNull();
    }

    [Fact]
    public void FromType_collects_validation_constraints_from_attribute_args()
    {
        var schema = StepUiSchemaBuilder.FromType<ValidatedSchema>();

        var s = schema.Properties["Slug"];
        s.Validation.Should().NotBeNull();
        s.Validation!.Required.Should().BeTrue();
        s.Validation.MinLength.Should().Be(3);
        s.Validation.MaxLength.Should().Be(64);
        s.Validation.Pattern.Should().Be("^[a-z0-9-]+$");

        var p = schema.Properties["Port"];
        p.Validation!.Min.Should().Be(1);
        p.Validation.Max.Should().Be(65535);
    }

    // ── Round-trip via JSON ───────────────────────────────────────────────

    [Fact]
    public void FromType_then_serialize_and_deserialize_round_trips()
    {
        // C-2 promise: FromType produces a schema equivalent to one parsed from
        // an embedded ui-schema.json — the renderer should not be able to tell
        // the difference.
        var fromAttributes = StepUiSchemaBuilder.FromType<RoundTripSchema>();
        var json = StepUiSchemaJson.Serialize(fromAttributes);
        var fromJson = StepUiSchemaBuilder.FromJson(json);

        fromJson.Should().BeEquivalentTo(fromAttributes);
    }

    // ── Test fixtures ─────────────────────────────────────────────────────

    [StepUiSchemaRoot(Id = "kraken.minimal", Title = "Minimal step",
        Version = "1.0.0", Description = "An empty step for testing.")]
    private sealed class MinimalSchema { }

    private sealed class UnrootedPoco
    {
        // No [StepUiSchemaRoot] — builder must reject.
    }

    [StepUiSchemaRoot(Id = "g", Title = "Grouped", Version = "1.0.0")]
    [StepUiGroup("a", "Group A")]
    [StepUiGroup("b", "Group B", Description = "second group")]
    [StepUiGroup("c", "Group C", Collapsed = true)]
    private sealed class GroupedSchema { }

    [StepUiSchemaRoot(Id = "p", Title = "Primitive Types", Version = "1.0.0")]
    private sealed class PrimitiveTypesSchema
    {
        [StepUiField(Widget = "text")] public string Name { get; set; } = "";
        [StepUiField(Widget = "checkbox")] public bool Enabled { get; set; }
        [StepUiField(Widget = "number-input")] public int Port { get; set; }
        [StepUiField(Widget = "number-input")] public double Timeout { get; set; }
        // Not decorated — must not appear in the schema.
        public string Ignored { get; set; } = "";
    }

    [StepUiSchemaRoot(Id = "n", Title = "Nullable Types", Version = "1.0.0")]
    private sealed class NullableTypesSchema
    {
        [StepUiField(Widget = "number-input")] public int? MaybeInt { get; set; }
        [StepUiField(Widget = "checkbox")] public bool? MaybeBool { get; set; }
    }

    [StepUiSchemaRoot(Id = "e", Title = "Enumerable", Version = "1.0.0")]
    private sealed class EnumerableSchema
    {
        [StepUiField(Widget = "json-editor")] public List<string> Tags { get; set; } = [];
    }

    [StepUiSchemaRoot(Id = "iis", Title = "IIS", Version = "1.0.0")]
    private sealed class DottedKeySchema
    {
        [StepUiField(Key = "Kraken.IIS.SiteName", Widget = "text", Label = "Site name")]
        public string SiteName { get; set; } = "";
    }

    [StepUiSchemaRoot(Id = "i", Title = "Identity", Version = "1.0.0")]
    private sealed class EnumSchema
    {
        [StepUiField(Widget = "select", Default = "ApplicationPoolIdentity")]
        [StepUiEnum("ApplicationPoolIdentity", "ApplicationPoolIdentity")]
        [StepUiEnum("LocalSystem", "LocalSystem")]
        [StepUiEnum("SpecificUser", "Specific user")]
        public string IdentityType { get; set; } = "";
    }

    [StepUiSchemaRoot(Id = "v", Title = "Visible when", Version = "1.0.0")]
    private sealed class VisibleWhenSchema
    {
        [StepUiField(Widget = "select")]
        public string IdentityType { get; set; } = "";

        [StepUiField(Widget = "sensitive")]
        [StepUiVisibleWhen(Field = "IdentityType", Operator = "equals", Value = "SpecificUser")]
        public string Password { get; set; } = "";
    }

    [StepUiSchemaRoot(Id = "val", Title = "Validation", Version = "1.0.0")]
    private sealed class ValidatedSchema
    {
        [StepUiField(Widget = "text",
            Required = true, MinLength = 3, MaxLength = 64,
            Pattern = "^[a-z0-9-]+$")]
        public string Slug { get; set; } = "";

        [StepUiField(Widget = "number-input", Min = 1, Max = 65535)]
        public int Port { get; set; }
    }

    [StepUiSchemaRoot(Id = "rt", Title = "Round-trip", Version = "2.0.0", Description = "rt")]
    [StepUiGroup("g1", "G1")]
    [StepUiGroup("g2", "G2", Collapsed = true)]
    private sealed class RoundTripSchema
    {
        [StepUiField(Widget = "text", Label = "Name", Group = "g1", Required = true, MaxLength = 100)]
        public string Name { get; set; } = "";

        [StepUiField(Widget = "select", Label = "Mode", Group = "g1", Default = "auto")]
        [StepUiEnum("auto", "Automatic")]
        [StepUiEnum("manual", "Manual")]
        public string Mode { get; set; } = "";

        [StepUiField(Key = "kraken.dotted.path", Widget = "checkbox", Group = "g2")]
        [StepUiVisibleWhen(Field = "Mode", Operator = "equals", Value = "manual")]
        public bool Advanced { get; set; }

        [StepUiField(Widget = "number-input", Group = "g2", Min = 0, Max = 100)]
        public int Percent { get; set; }
    }
}
