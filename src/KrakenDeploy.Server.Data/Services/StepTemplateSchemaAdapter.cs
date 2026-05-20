using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.StepTemplates;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Bridge between the legacy community-library shape
/// (<see cref="StepTemplate"/> + <see cref="StepTemplateParameter"/>) and the
/// new Phase C declarative schema IR (<see cref="StepUiSchema"/>). Lets the
/// existing 600+ community-library step templates flow through the new
/// schema-driven renderer (Phase C-4) without rewriting their parameter
/// definitions.
/// <para>
/// Maps each <see cref="StepTemplateParameter.ControlType"/> to a
/// <see cref="StepUiWidgets"/> identifier:
/// </para>
/// <list type="bullet">
///   <item><c>SingleLineText</c> → <see cref="StepUiWidgets.Text"/></item>
///   <item><c>MultiLineText</c> → <see cref="StepUiWidgets.Textarea"/></item>
///   <item><c>Sensitive</c> → <see cref="StepUiWidgets.Sensitive"/></item>
///   <item><c>Checkbox</c> → <see cref="StepUiWidgets.Checkbox"/></item>
///   <item><c>Select</c> → <see cref="StepUiWidgets.Select"/>, with
///         <see cref="StepTemplateParameter.SelectOptions"/> entries split on
///         <c>|</c> into value/label pairs (matching the legacy dialog's
///         convention).</item>
///   <item><c>Package</c> → <see cref="StepUiWidgets.PackageRef"/></item>
///   <item>anything else → <see cref="StepUiWidgets.Text"/> (defensive default)</item>
/// </list>
/// </summary>
public static class StepTemplateSchemaAdapter
{
    /// <summary>
    /// Builds a <see cref="StepUiSchema"/> from a <see cref="StepTemplate"/>.
    /// Schema root uses the template's <see cref="StepTemplate.ActionType"/>
    /// as the id and <see cref="StepTemplate.Name"/> as the title; the
    /// description and version follow the template's own fields where
    /// available, with sensible fallbacks.
    /// </summary>
    public static StepUiSchema BuildSchema(StepTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new StepUiSchema
        {
            Id          = !string.IsNullOrWhiteSpace(template.ActionType)
                            ? template.ActionType.ToLowerInvariant()
                            : "kraken.legacy-template",
            Title       = template.Name,
            Description = template.Description,
            Version     = "1.0.0",
            Properties  = BuildPropertyMap(template.Parameters),
        };
    }

    /// <summary>
    /// Builds the per-key field map directly from a parameter list. Lower-level
    /// entry point — useful when the caller doesn't have a <see cref="StepTemplate"/>
    /// wrapper in hand. Kept on a dedicated method instead of folded into
    /// <see cref="StepUiSchemaBuilder"/> because the bridge crosses the
    /// Server.Data → Server.Core domain boundary that Contracts can't see.
    /// </summary>
    public static IReadOnlyDictionary<string, StepUiField> BuildPropertyMap(
        IReadOnlyList<StepTemplateParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var map = new Dictionary<string, StepUiField>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            map[p.Name] = BuildField(p);
        }
        return map;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static StepUiField BuildField(StepTemplateParameter p)
    {
        var widget = MapControlType(p.ControlType);
        var enumValues = widget == StepUiWidgets.Select
            ? ParseSelectOptions(p.SelectOptions)
            : (IReadOnlyList<StepUiEnumValue>)[];

        return new StepUiField
        {
            Type        = InferFieldType(widget),
            Widget      = widget,
            Label       = p.Label,
            HelpText    = p.HelpText,
            Default     = p.DefaultValue,
            EnumValues  = enumValues,
            // Legacy StepTemplateParameter doesn't carry per-parameter
            // validation, conditional visibility, or group placement — those
            // were authored ad-hoc in script bodies before Phase C. Schemas
            // built from the legacy shape therefore have no Validation,
            // VisibleWhen, or Group on any field.
        };
    }

    /// <summary>
    /// Maps a <see cref="StepTemplateParameter.ControlType"/> string to a
    /// <see cref="StepUiWidgets"/> identifier. Case-insensitive. Unknown
    /// control types defensively fall back to <see cref="StepUiWidgets.Text"/>
    /// so a malformed import doesn't blow up the editor.
    /// </summary>
    private static string MapControlType(string? controlType)
    {
        if (string.IsNullOrWhiteSpace(controlType)) { return StepUiWidgets.Text; }
        return controlType.Trim().ToLowerInvariant() switch
        {
            "singlelinetext" => StepUiWidgets.Text,
            "multilinetext"  => StepUiWidgets.Textarea,
            "sensitive"      => StepUiWidgets.Sensitive,
            "checkbox"       => StepUiWidgets.Checkbox,
            "select"         => StepUiWidgets.Select,
            "package"        => StepUiWidgets.PackageRef,
            _                => StepUiWidgets.Text,
        };
    }

    /// <summary>
    /// Picks the JSON-Schema-style field type that matches the widget.
    /// Checkbox is Boolean, everything else is String — legacy templates store
    /// every value as a string in the config bag, so even numeric inputs are
    /// modelled as String (the renderer / handler parses on read).
    /// </summary>
    private static StepUiFieldType InferFieldType(string widget) => widget switch
    {
        StepUiWidgets.Checkbox => StepUiFieldType.Boolean,
        _                      => StepUiFieldType.String,
    };

    /// <summary>
    /// Parses the legacy <c>value|label</c> format used by community-library
    /// templates' <see cref="StepTemplateParameter.SelectOptions"/>. Entries
    /// without a pipe use the same string for both fields.
    /// </summary>
    private static List<StepUiEnumValue> ParseSelectOptions(
        IEnumerable<string> rawOptions)
    {
        var list = new List<StepUiEnumValue>();
        foreach (var line in rawOptions)
        {
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            var idx = line.IndexOf('|', StringComparison.Ordinal);
            if (idx >= 0)
            {
                list.Add(new StepUiEnumValue
                {
                    Value = line[..idx].Trim(),
                    Label = line[(idx + 1)..].Trim(),
                });
            }
            else
            {
                var v = line.Trim();
                list.Add(new StepUiEnumValue { Value = v, Label = v });
            }
        }
        return list;
    }
}
