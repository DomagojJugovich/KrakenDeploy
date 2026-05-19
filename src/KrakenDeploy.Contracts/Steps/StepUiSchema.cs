using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Declarative schema describing one step type's editor — a JSON Schema subset
/// (draft 2020-12) extended with Kraken widget annotations. Authored either as
/// C# attributes on a POCO (see Phase C-2) or as an embedded <c>ui-schema.json</c>
/// resource inside a step package (Phase D-1). Rendered by a single Razor
/// component (Phase C-4) that handles every step type, so per-type editor
/// pages can be retired.
/// <para>
/// The IR is renderer-agnostic — nothing in this file references Razor,
/// React, MAUI, or any framework. A future MAUI-based desktop editor would
/// consume the same schema.
/// </para>
/// <para>
/// JSON round-trip: <see cref="StepUiSchemaJson.Serialize"/> and
/// <see cref="StepUiSchemaJson.Deserialize"/> are the canonical conversion
/// path. Field-name casing in JSON is <c>camelCase</c> for compatibility with
/// JavaScript / JSON Schema convention; enums emit as their lowercase name.
/// </para>
/// </summary>
public sealed record StepUiSchema
{
    /// <summary>Stable identifier — matches the step package <c>id</c> (e.g. <c>kraken.iis</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Display title shown above the editor.</summary>
    public required string Title { get; init; }

    /// <summary>Optional one-paragraph description rendered as helper text.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Schema version — semver matching the step package version. Renderers may
    /// compare against an existing step's pinned version to detect schema drift
    /// when the version is bumped (added / removed / renamed fields).
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Named groups for collapsible sections in the editor (e.g. "Web Site" /
    /// "App Pool" / "Bindings" / "Health Probe" on a Kraken.IIS form). Fields
    /// reference a group via <see cref="StepUiField.Group"/>; fields without a
    /// group render in a default "General" section.
    /// </summary>
    public IReadOnlyList<StepUiGroup> Groups { get; init; } = [];

    /// <summary>
    /// Top-level field definitions, keyed by the config-bag key (i.e. the same
    /// key that lands in <c>DeploymentStep.Config</c>). Order is insertion
    /// order — the renderer walks the dictionary directly.
    /// </summary>
    public IReadOnlyDictionary<string, StepUiField> Properties { get; init; } =
        new Dictionary<string, StepUiField>();
}

/// <summary>
/// Named group of related fields rendered together. Optional — fields without
/// a <see cref="StepUiField.Group"/> reference land in a default "General"
/// section.
/// </summary>
public sealed record StepUiGroup
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// When <c>true</c>, the group renders collapsed by default. Useful for
    /// rarely-touched sections (Recycling / Rapid-Fail on IIS).
    /// </summary>
    public bool Collapsed { get; init; }
}

/// <summary>
/// A single field in the schema. Recursive: <see cref="ItemSchema"/> describes
/// the per-row schema for an <c>array</c> field; <see cref="Properties"/>
/// describes the nested fields of an <c>object</c> field.
/// </summary>
public sealed record StepUiField
{
    /// <summary>
    /// JSON-Schema-style data type. Storage in <c>DeploymentStep.Config</c> is
    /// always <c>string→string</c> — the schema's type drives the widget choice
    /// and coercion at the renderer boundary.
    /// </summary>
    public required StepUiFieldType Type { get; init; }

    /// <summary>
    /// Widget hint. One of the constants on <see cref="StepUiWidgets"/>
    /// (<c>text</c>, <c>textarea</c>, <c>sensitive</c>, <c>select</c>,
    /// <c>number-input</c>, <c>checkbox</c>, <c>variable-ref</c>,
    /// <c>certificate-ref</c>, <c>package-ref</c>, <c>target-roles</c>,
    /// <c>json-editor</c>, <c>file-picker</c>). Renderers fall back to a
    /// type-appropriate default when the widget is empty or unknown.
    /// </summary>
    public required string Widget { get; init; }

    /// <summary>Field label shown in the UI. Falls back to the property key.</summary>
    public string? Label { get; init; }

    /// <summary>Optional help text rendered below the input.</summary>
    public string? HelpText { get; init; }

    /// <summary>Optional placeholder for text-style widgets.</summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// The group this field belongs to — references a <see cref="StepUiGroup.Id"/>.
    /// Fields without a group render in a default "General" section.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Default value as a string. Storage is <c>string→string</c> so all
    /// defaults are expressed as their string form (<c>"true"</c> for a
    /// checkbox, <c>"42"</c> for a number, etc.). Coercion to the typed value
    /// for the renderer is the renderer's job (Phase C-3).
    /// </summary>
    public string? Default { get; init; }

    /// <summary>
    /// Options for <c>select</c> widgets. Empty for non-select fields. Order
    /// is preserved.
    /// </summary>
    public IReadOnlyList<StepUiEnumValue> EnumValues { get; init; } = [];

    /// <summary>
    /// Optional sibling-field predicate that controls whether this field is
    /// rendered. <c>null</c> means "always visible".
    /// </summary>
    public StepUiVisibleWhen? VisibleWhen { get; init; }

    /// <summary>
    /// Validation rules. <c>null</c> means no constraints beyond the field's
    /// type. Renderers may use this both for inline editor validation and for
    /// server-side validation at save time (Phase C-3).
    /// </summary>
    public StepUiValidation? Validation { get; init; }

    /// <summary>
    /// Required when <see cref="Type"/> is <see cref="StepUiFieldType.Array"/>.
    /// Describes the schema for one row. The renderer renders an editable grid
    /// or repeated form.
    /// </summary>
    public StepUiField? ItemSchema { get; init; }

    /// <summary>
    /// Required when <see cref="Type"/> is <see cref="StepUiFieldType.Object"/>.
    /// Per-key schemas for the nested fields. Renderer walks this dictionary
    /// the same way as the schema root's <see cref="StepUiSchema.Properties"/>.
    /// </summary>
    public IReadOnlyDictionary<string, StepUiField>? Properties { get; init; }
}

/// <summary>
/// JSON-Schema-style data type. Storage in the step config bag is always
/// <c>string→string</c> — the type drives widget selection and the renderer's
/// coercion at the edit boundary.
/// </summary>
[JsonConverter(typeof(StepUiFieldTypeJsonConverter))]
[SuppressMessage("Naming", "CA1720:Identifiers should not contain type names",
    Justification = "Enum members deliberately mirror JSON Schema's type names "
                  + "(string / number / integer / boolean / object / array).")]
public enum StepUiFieldType
{
    String,
    Number,
    Integer,
    Boolean,
    Object,
    Array,
}

/// <summary>
/// Lower-cased enum-as-string converter for <see cref="StepUiFieldType"/>, so
/// the serialised JSON matches JSON Schema's lower-cased type names
/// (<c>"string"</c>, <c>"integer"</c>, ...).
/// </summary>
public sealed class StepUiFieldTypeJsonConverter : JsonStringEnumConverter<StepUiFieldType>
{
    public StepUiFieldTypeJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>One option in a <c>select</c> widget.</summary>
public sealed record StepUiEnumValue
{
    /// <summary>The persisted value (what lands in the config bag).</summary>
    public required string Value { get; init; }

    /// <summary>The label shown in the dropdown.</summary>
    public required string Label { get; init; }
}

/// <summary>
/// Sibling-field predicate controlling field visibility. The predicate is
/// evaluated against the current form values; when it returns false, the
/// field is hidden and (renderer's choice) either preserved or cleared at
/// save time.
/// <para>
/// Example: hide <c>AppPoolUsername</c> when <c>AppPoolIdentityType</c> is
/// not <c>SpecificUser</c>.
/// </para>
/// </summary>
public sealed record StepUiVisibleWhen
{
    /// <summary>
    /// Sibling field key (relative to the same nesting level) whose value is
    /// inspected.
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Predicate operator. One of: <c>equals</c>, <c>not-equals</c>,
    /// <c>in</c>, <c>not-in</c>, <c>truthy</c>, <c>falsy</c>.
    /// </summary>
    public required string Operator { get; init; }

    /// <summary>
    /// Comparison value as a string. For <c>in</c> / <c>not-in</c>, semicolon-
    /// separated. Ignored for <c>truthy</c> / <c>falsy</c>.
    /// </summary>
    public string? Value { get; init; }
}

/// <summary>
/// Per-field validation constraints. All fields are optional; when omitted
/// the constraint is not applied.
/// </summary>
public sealed record StepUiValidation
{
    public bool Required { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }

    /// <summary>Regex pattern (anchored at both ends by the renderer if not already).</summary>
    public string? Pattern { get; init; }

    public double? Min { get; init; }
    public double? Max { get; init; }
}

/// <summary>
/// Canonical widget identifiers. Renderers may add their own widget catalogue
/// on top — these are the standard set every renderer is expected to handle.
/// </summary>
public static class StepUiWidgets
{
    public const string Text           = "text";
    public const string Textarea       = "textarea";
    public const string Sensitive      = "sensitive";
    public const string Select         = "select";
    public const string NumberInput    = "number-input";
    public const string Checkbox       = "checkbox";

    /// <summary>Picker for a deployment-variable name (e.g. <c>#{X}</c>).</summary>
    public const string VariableRef    = "variable-ref";

    /// <summary>Picker for an X.509 certificate stored in the Kraken cert store.</summary>
    public const string CertificateRef = "certificate-ref";

    /// <summary>Picker for a package identifier (and optional version).</summary>
    public const string PackageRef     = "package-ref";

    /// <summary>Multi-select chip widget over the project's defined target roles.</summary>
    public const string TargetRoles    = "target-roles";

    /// <summary>Raw JSON text area with syntax highlighting / validation.</summary>
    public const string JsonEditor     = "json-editor";

    /// <summary>File path within the package payload (artifact-relative).</summary>
    public const string FilePicker     = "file-picker";
}

/// <summary>
/// Canonical JSON serialiser for <see cref="StepUiSchema"/>. Centralises the
/// serialization options so authoring + parsing + tests all agree on casing,
/// whitespace, and enum format.
/// </summary>
public static class StepUiSchemaJson
{
    /// <summary>Shared JSON options used for both serialise and deserialise.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy         = null,           // property keys are user-defined config keys
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(StepUiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return JsonSerializer.Serialize(schema, Options);
    }

    public static StepUiSchema Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<StepUiSchema>(json, Options)
            ?? throw new InvalidOperationException("Step UI schema JSON deserialised to null.");
    }
}
