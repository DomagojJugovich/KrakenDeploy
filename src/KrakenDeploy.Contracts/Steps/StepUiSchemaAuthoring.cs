using System.Reflection;

namespace KrakenDeploy.Contracts.Steps;

// ── Attribute set used to declare a step's UI schema via a POCO ─────────────

/// <summary>
/// Declares a class as the root POCO for a step UI schema. Pair with
/// <see cref="StepUiGroupAttribute"/> (per group) and
/// <see cref="StepUiFieldAttribute"/> (per property) to describe the editor.
/// Read by <see cref="StepUiSchemaBuilder.FromType"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class StepUiSchemaRootAttribute : Attribute
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Declares a named UI group on a schema-root class. Multiple groups are
/// allowed; field-level <see cref="StepUiFieldAttribute.Group"/> references
/// the group <c>Id</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class StepUiGroupAttribute(string id, string title) : Attribute
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string? Description { get; init; }
    public bool Collapsed { get; init; }
}

/// <summary>
/// Marks a POCO property as a step-UI field. Drives widget choice, label,
/// validation, and group placement.
/// <para>
/// The config-bag key defaults to the property name. Properties that need a
/// dotted key (the dominant case — config keys like
/// <c>Octopus.Action.IISWebSite.WebSiteName</c> can't be C# property names)
/// must set <see cref="Key"/> explicitly.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class StepUiFieldAttribute : Attribute
{
    /// <summary>
    /// Config-bag key. Defaults to the property name; set explicitly when the
    /// key contains characters (such as <c>.</c>) that can't appear in a C#
    /// identifier.
    /// </summary>
    public string? Key { get; init; }

    public required string Widget { get; init; }

    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? Placeholder { get; init; }
    public string? Group { get; init; }

    /// <summary>
    /// Default value as a string (storage form). For non-string types the
    /// renderer parses this string back to the typed default
    /// (<c>"true"</c> for a checkbox, <c>"42"</c> for a number…).
    /// </summary>
    public string? Default { get; init; }

    // ── Validation ─────────────────────────────────────────────────────────
    public bool Required { get; init; }
    /// <summary>-1 = unset (no constraint).</summary>
    public int MinLength { get; init; } = -1;
    /// <summary>-1 = unset (no constraint).</summary>
    public int MaxLength { get; init; } = -1;
    public string? Pattern { get; init; }
    /// <summary><see cref="double.NaN"/> = unset.</summary>
    public double Min { get; init; } = double.NaN;
    /// <summary><see cref="double.NaN"/> = unset.</summary>
    public double Max { get; init; } = double.NaN;
}

/// <summary>
/// Repeating attribute that contributes one option to a <c>select</c>-widget
/// field. The decorated property's <see cref="StepUiFieldAttribute.Widget"/>
/// should be <c>"select"</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class StepUiEnumAttribute(string value, string label) : Attribute
{
    public string Value { get; } = value;
    public string Label { get; } = label;
}

/// <summary>
/// Declares a sibling-field predicate that controls whether the decorated
/// field is rendered. See <see cref="StepUiVisibleWhen"/> for the operator
/// vocabulary.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class StepUiVisibleWhenAttribute : Attribute
{
    public required string Field { get; init; }
    public required string Operator { get; init; }
    public string? Value { get; init; }
}

// ── Builder ────────────────────────────────────────────────────────────────

/// <summary>
/// Builds <see cref="StepUiSchema"/> instances. Two equivalent paths:
/// <list type="bullet">
///   <item><see cref="FromType"/> — reflection over a POCO decorated with
///         <see cref="StepUiSchemaRootAttribute"/>,
///         <see cref="StepUiGroupAttribute"/>,
///         <see cref="StepUiFieldAttribute"/>, etc.</item>
///   <item><see cref="FromJson"/> — parse an embedded <c>ui-schema.json</c>
///         resource via <see cref="StepUiSchemaJson.Deserialize"/>.</item>
/// </list>
/// Both paths produce identical <see cref="StepUiSchema"/> trees so the
/// renderer (Phase C-4) doesn't care which authoring style the step package
/// chose.
/// </summary>
public static class StepUiSchemaBuilder
{
    public static StepUiSchema FromJson(string json) => StepUiSchemaJson.Deserialize(json);

    public static StepUiSchema FromType(Type t)
    {
        ArgumentNullException.ThrowIfNull(t);

        var root = t.GetCustomAttribute<StepUiSchemaRootAttribute>()
            ?? throw new InvalidOperationException(
                $"Type '{t.FullName}' is missing [StepUiSchemaRoot] — it cannot be used as a schema root.");

        var groups = t.GetCustomAttributes<StepUiGroupAttribute>()
            .Select(g => new StepUiGroup
            {
                Id          = g.Id,
                Title       = g.Title,
                Description = g.Description,
                Collapsed   = g.Collapsed,
            })
            .ToList();

        var properties = new Dictionary<string, StepUiField>(StringComparer.Ordinal);
        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var fa = prop.GetCustomAttribute<StepUiFieldAttribute>();
            if (fa is null)
            {
                continue;
            }

            var key = string.IsNullOrWhiteSpace(fa.Key) ? prop.Name : fa.Key!;
            properties[key] = BuildField(prop, fa);
        }

        return new StepUiSchema
        {
            Id          = root.Id,
            Title       = root.Title,
            Description = root.Description,
            Version     = root.Version,
            Groups      = groups,
            Properties  = properties,
        };
    }

    /// <summary>
    /// Generic shorthand for <see cref="FromType(Type)"/>.
    /// </summary>
    public static StepUiSchema FromType<T>() => FromType(typeof(T));

    // ── Helpers ────────────────────────────────────────────────────────────

    private static StepUiField BuildField(PropertyInfo prop, StepUiFieldAttribute fa)
    {
        var type = InferFieldType(prop.PropertyType);

        var enums = prop.GetCustomAttributes<StepUiEnumAttribute>()
            .Select(e => new StepUiEnumValue { Value = e.Value, Label = e.Label })
            .ToList();

        var visibleWhenAttr = prop.GetCustomAttribute<StepUiVisibleWhenAttribute>();
        var visibleWhen = visibleWhenAttr is null
            ? null
            : new StepUiVisibleWhen
            {
                Field    = visibleWhenAttr.Field,
                Operator = visibleWhenAttr.Operator,
                Value    = visibleWhenAttr.Value,
            };

        var validation = BuildValidation(fa);

        return new StepUiField
        {
            Type        = type,
            Widget      = fa.Widget,
            Label       = fa.Label,
            HelpText    = fa.HelpText,
            Placeholder = fa.Placeholder,
            Group       = fa.Group,
            Default     = fa.Default,
            EnumValues  = enums,
            VisibleWhen = visibleWhen,
            Validation  = validation,
        };
    }

    private static StepUiValidation? BuildValidation(StepUiFieldAttribute fa)
    {
        var hasAny = fa.Required
                  || fa.MinLength >= 0
                  || fa.MaxLength >= 0
                  || !string.IsNullOrEmpty(fa.Pattern)
                  || !double.IsNaN(fa.Min)
                  || !double.IsNaN(fa.Max);
        if (!hasAny)
        {
            return null;
        }
        return new StepUiValidation
        {
            Required  = fa.Required,
            MinLength = fa.MinLength >= 0 ? fa.MinLength : null,
            MaxLength = fa.MaxLength >= 0 ? fa.MaxLength : null,
            Pattern   = string.IsNullOrEmpty(fa.Pattern) ? null : fa.Pattern,
            Min       = !double.IsNaN(fa.Min) ? fa.Min : null,
            Max       = !double.IsNaN(fa.Max) ? fa.Max : null,
        };
    }

    private static StepUiFieldType InferFieldType(Type propertyType)
    {
        var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (underlying == typeof(string))                                  { return StepUiFieldType.String; }
        if (underlying == typeof(bool))                                    { return StepUiFieldType.Boolean; }
        if (underlying == typeof(int) || underlying == typeof(long)
            || underlying == typeof(short) || underlying == typeof(byte)
            || underlying == typeof(uint) || underlying == typeof(ulong)
            || underlying == typeof(ushort) || underlying == typeof(sbyte)) { return StepUiFieldType.Integer; }
        if (underlying == typeof(float) || underlying == typeof(double)
            || underlying == typeof(decimal))                              { return StepUiFieldType.Number; }
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying)
            && underlying != typeof(string))                               { return StepUiFieldType.Array; }
        return StepUiFieldType.Object;
    }
}
