using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// One validation error from <see cref="StepUiSchemaValidator.Validate"/>.
/// </summary>
/// <param name="FieldKey">The config-bag key the error is attached to.</param>
/// <param name="Message">Human-readable error message.</param>
public sealed record StepUiValidationError(string FieldKey, string Message);

/// <summary>
/// Validator + value coercion for the step-UI schema IR (Phase C-3).
/// <para>
/// Validation is applied at both edit-time (renderer feedback) and save-time
/// (server-side reject before persist). Coercion bridges the typed JSON the
/// renderer works with and the flat <c>Dictionary&lt;string,string&gt;</c>
/// that <see cref="StepUiSchema.Properties"/> backed configs store in.
/// </para>
/// </summary>
public static class StepUiSchemaValidator
{
    /// <summary>
    /// Validates a set of flat <c>string→string</c> config values against the
    /// schema. Returns an empty list when everything passes. Each constraint
    /// is independent; one field may have multiple errors.
    /// </summary>
    public static IReadOnlyList<StepUiValidationError> Validate(
        StepUiSchema schema,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        var errors = new List<StepUiValidationError>();

        foreach (var (key, field) in schema.Properties)
        {
            values.TryGetValue(key, out var raw);
            ValidateField(key, field, raw, errors);
        }

        return errors;
    }

    /// <summary>
    /// Translates a flat <c>string→string</c> config bag into the typed JSON
    /// form the renderer works with. Strings stay strings; booleans / numbers
    /// / integers parse; arrays + objects parse their stored JSON
    /// representation. Missing values fall back to the schema's
    /// <see cref="StepUiField.Default"/> when present, otherwise to the
    /// type's natural default (<c>""</c> / <c>false</c> / <c>0</c> / empty
    /// array / empty object).
    /// </summary>
    public static JsonObject CoerceFromConfig(
        StepUiSchema schema,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        var json = new JsonObject();
        foreach (var (key, field) in schema.Properties)
        {
            var raw = values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
                ? v
                : field.Default;
            json[key] = ToJsonNode(field.Type, raw);
        }
        return json;
    }

    /// <summary>
    /// Inverse of <see cref="CoerceFromConfig"/>. Walks a typed JSON object
    /// produced by the renderer and emits a flat <c>string→string</c> config
    /// bag suitable for persistence via <see cref="StepUiSchema.Properties"/>.
    /// </summary>
    public static Dictionary<string, string> CoerceToConfig(
        StepUiSchema schema,
        JsonObject typedValues)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(typedValues);

        var bag = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, field) in schema.Properties)
        {
            if (!typedValues.TryGetPropertyValue(key, out var node) || node is null)
            {
                continue;
            }
            bag[key] = NodeToString(field.Type, node);
        }
        return bag;
    }

    // ── Validation per field ──────────────────────────────────────────────

    private static void ValidateField(
        string key, StepUiField field, string? raw, List<StepUiValidationError> errors)
    {
        var empty = string.IsNullOrEmpty(raw);
        var v = field.Validation;

        if (v?.Required == true && empty)
        {
            errors.Add(new(key, $"'{field.Label ?? key}' is required."));
            return; // No point checking other constraints when value is absent.
        }

        if (empty)
        {
            return;
        }

        // Enum membership.
        if (field.EnumValues.Count > 0
            && !field.EnumValues.Any(e => e.Value.Equals(raw, StringComparison.Ordinal)))
        {
            errors.Add(new(key,
                $"'{field.Label ?? key}' must be one of: " +
                string.Join(", ", field.EnumValues.Select(e => $"'{e.Value}'")) + "."));
        }

        // Type coherence + numeric range. We parse defensively so a bad string
        // doesn't crash callers — every type rejects the bad value with a
        // single error.
        switch (field.Type)
        {
            case StepUiFieldType.Boolean:
                if (!bool.TryParse(raw, out _))
                {
                    errors.Add(new(key, $"'{field.Label ?? key}' must be true or false."));
                }
                break;
            case StepUiFieldType.Integer:
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ival))
                {
                    errors.Add(new(key, $"'{field.Label ?? key}' must be a whole number."));
                }
                else
                {
                    if (v?.Min is double iMin && ival < iMin)
                    {
                        errors.Add(new(key,
                            $"'{field.Label ?? key}' must be at least {iMin.ToString(CultureInfo.InvariantCulture)}."));
                    }
                    if (v?.Max is double iMax && ival > iMax)
                    {
                        errors.Add(new(key,
                            $"'{field.Label ?? key}' must be at most {iMax.ToString(CultureInfo.InvariantCulture)}."));
                    }
                }
                break;
            case StepUiFieldType.Number:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dval))
                {
                    errors.Add(new(key, $"'{field.Label ?? key}' must be a number."));
                }
                else
                {
                    if (v?.Min is double nMin && dval < nMin)
                    {
                        errors.Add(new(key,
                            $"'{field.Label ?? key}' must be at least {nMin.ToString(CultureInfo.InvariantCulture)}."));
                    }
                    if (v?.Max is double nMax && dval > nMax)
                    {
                        errors.Add(new(key,
                            $"'{field.Label ?? key}' must be at most {nMax.ToString(CultureInfo.InvariantCulture)}."));
                    }
                }
                break;
            case StepUiFieldType.String:
                if (v?.MinLength is int min && raw!.Length < min)
                {
                    errors.Add(new(key,
                        $"'{field.Label ?? key}' must be at least {min.ToString(CultureInfo.InvariantCulture)} characters."));
                }
                if (v?.MaxLength is int max && raw!.Length > max)
                {
                    errors.Add(new(key,
                        $"'{field.Label ?? key}' must be at most {max.ToString(CultureInfo.InvariantCulture)} characters."));
                }
                if (!string.IsNullOrEmpty(v?.Pattern))
                {
                    if (!RegexCache.Get(v.Pattern!).IsMatch(raw!))
                    {
                        errors.Add(new(key,
                            $"'{field.Label ?? key}' does not match the required pattern."));
                    }
                }
                break;
            case StepUiFieldType.Array:
            case StepUiFieldType.Object:
                // The storage form for arrays / objects is JSON text — parse to
                // confirm it's at least well-formed.
                try { JsonNode.Parse(raw!); }
                catch (JsonException ex)
                {
                    errors.Add(new(key,
                        $"'{field.Label ?? key}' is not valid JSON: {ex.Message}"));
                }
                break;
        }
    }

    // ── Coercion: string → typed JsonNode ─────────────────────────────────

    private static JsonNode? ToJsonNode(StepUiFieldType type, string? raw)
    {
        switch (type)
        {
            case StepUiFieldType.String:
                return JsonValue.Create(raw ?? string.Empty);
            case StepUiFieldType.Boolean:
                return JsonValue.Create(
                    !string.IsNullOrEmpty(raw)
                        && bool.TryParse(raw, out var b) && b);
            case StepUiFieldType.Integer:
                return JsonValue.Create(
                    !string.IsNullOrEmpty(raw)
                        && long.TryParse(raw, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var i)
                        ? i : 0L);
            case StepUiFieldType.Number:
                return JsonValue.Create(
                    !string.IsNullOrEmpty(raw)
                        && double.TryParse(raw, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var d)
                        ? d : 0.0);
            case StepUiFieldType.Array:
                if (string.IsNullOrWhiteSpace(raw)) { return new JsonArray(); }
                try { return JsonNode.Parse(raw); }
                catch (JsonException) { return new JsonArray(); }
            case StepUiFieldType.Object:
                if (string.IsNullOrWhiteSpace(raw)) { return new JsonObject(); }
                try { return JsonNode.Parse(raw); }
                catch (JsonException) { return new JsonObject(); }
            default:
                return JsonValue.Create(raw ?? string.Empty);
        }
    }

    // ── Coercion: typed JsonNode → string ─────────────────────────────────

    private static string NodeToString(StepUiFieldType type, JsonNode node)
    {
        return type switch
        {
            StepUiFieldType.String  => node.GetValue<string?>() ?? string.Empty,
            StepUiFieldType.Boolean => node.GetValue<bool>().ToString().ToLowerInvariant(),
            StepUiFieldType.Integer => node.GetValue<long>()
                .ToString(CultureInfo.InvariantCulture),
            StepUiFieldType.Number  => node.GetValue<double>()
                .ToString("G17", CultureInfo.InvariantCulture),
            StepUiFieldType.Array
                or StepUiFieldType.Object => node.ToJsonString(),
            _                       => node.ToString(),
        };
    }

    // ── Pattern cache ─────────────────────────────────────────────────────

    private static class RegexCache
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> _cache = new();

        public static Regex Get(string pattern) =>
            _cache.GetOrAdd(pattern, p =>
                new Regex(p, RegexOptions.CultureInvariant | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(250)));
    }
}
