using System.Text.Json;
using System.Text.Json.Serialization;

namespace KrakenDeploy.Contracts.StepPackages;

/// <summary>
/// One step type a package claims, with optional per-type picker metadata
/// (SC1 / SD-4). In <c>manifest.json</c> a <c>stepTypes</c> entry is either a
/// plain string (id only — the pre-SC1 shape, still fully supported) or an
/// object: <c>{ "id": "...", "displayName": "...", "category": "...",
/// "description": "...", "featured": true }</c>.
/// <para>
/// Serialization is shape-preserving: an entry that carries no metadata
/// round-trips as a plain string. That keeps the canonical manifest bytes —
/// the signature input (<see cref="StepPackageManifestJson.CanonicalSignatureInput"/>)
/// — identical for pre-SC1 manifests, so re-verifying an old signed archive
/// with new code still succeeds.
/// </para>
/// </summary>
[JsonConverter(typeof(StepTypeDeclarationJsonConverter))]
public sealed record StepTypeDeclaration
{
    /// <summary>
    /// The step-type id the executor's <c>CanHandle</c> claims
    /// (e.g. <c>Octopus.HealthCheck</c>). Matching is case-insensitive
    /// downstream; stored lower-cased in the server's denormalised list.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Picker-card title (e.g. <c>Health Check</c>). Falls back to the package's DisplayName when absent.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Small taxonomy bucket (e.g. <c>kubernetes</c>, <c>script</c>) — feeds the picker's category filters.</summary>
    public string? Category { get; init; }

    /// <summary>One-liner shown on the picker card.</summary>
    public string? Description { get; init; }

    /// <summary>When true the picker surfaces this type in its Featured section.</summary>
    public bool Featured { get; init; }

    /// <summary>
    /// Where steps of this type execute: <c>null</c>/"agent" (default — the
    /// package's handler runs on the agent) or <c>"server"</c> (server-side
    /// orchestration, e.g. <c>Octopus.Manual</c>'s task-global gate). Feeds
    /// the registry's ExecutionLocus, which drives wave partitioning.
    /// </summary>
    public string? ExecutionLocus { get; init; }

    /// <summary>The <see cref="ExecutionLocus"/> value marking server-side execution.</summary>
    public const string ServerLocus = "server";

    /// <summary>True when the entry carries nothing beyond the id — serialised as a plain string.</summary>
    public bool IsIdOnly =>
        DisplayName is null && Category is null && Description is null
        && !Featured && ExecutionLocus is null;

    /// <summary>
    /// Lets pre-SC1 call sites keep writing <c>StepTypes = ["A", "B"]</c> —
    /// collection expressions convert each string element through this.
    /// </summary>
    public static implicit operator StepTypeDeclaration(string id) => new() { Id = id };
}

/// <summary>
/// Reads a <c>stepTypes</c> entry from either shape (string | object) and
/// writes the minimal shape back (string when id-only, object otherwise).
/// Manual property handling — an attribute-bound converter can't recurse into
/// the default object mapping for its own type.
/// </summary>
public sealed class StepTypeDeclarationJsonConverter : JsonConverter<StepTypeDeclaration>
{
    public override StepTypeDeclaration Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var bareId = reader.GetString();
            if (string.IsNullOrWhiteSpace(bareId))
            {
                throw new JsonException("stepTypes entry must not be an empty string.");
            }
            return new StepTypeDeclaration { Id = bareId };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"stepTypes entry must be a string or an object, got {reader.TokenType}.");
        }

        string? id          = null;
        string? displayName = null;
        string? category    = null;
        string? description = null;
        string? locus       = null;
        var     featured    = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Malformed stepTypes entry object.");
            }

            var prop = reader.GetString();
            reader.Read();
            switch (prop?.ToLowerInvariant())
            {
                case "id":             id          = reader.GetString(); break;
                case "displayname":    displayName = reader.GetString(); break;
                case "category":       category    = reader.GetString(); break;
                case "description":    description = reader.GetString(); break;
                case "featured":       featured    = reader.GetBoolean(); break;
                case "executionlocus": locus       = reader.GetString(); break;
                default:               reader.Skip(); break; // forward-compat
            }
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new JsonException("stepTypes entry object is missing a non-empty 'id'.");
        }

        // Empty/whitespace metadata normalises to null — the MSBuild pack
        // target always emits every field (item-metadata defaults), so ""
        // must mean "not set" or id-only entries would stop round-tripping
        // as plain strings.
        // "agent" is the default — normalise it (and empty) to null so such
        // entries keep round-tripping as plain strings.
        var normalizedLocus = NullIfWhiteSpace(locus);
        if (string.Equals(normalizedLocus, "agent", StringComparison.OrdinalIgnoreCase))
        {
            normalizedLocus = null;
        }

        return new StepTypeDeclaration
        {
            Id             = id,
            DisplayName    = NullIfWhiteSpace(displayName),
            Category       = NullIfWhiteSpace(category),
            Description    = NullIfWhiteSpace(description),
            Featured       = featured,
            ExecutionLocus = normalizedLocus,
        };
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    public override void Write(
        Utf8JsonWriter writer, StepTypeDeclaration value, JsonSerializerOptions options)
    {
        if (value.IsIdOnly)
        {
            writer.WriteStringValue(value.Id);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        if (value.DisplayName    is not null) { writer.WriteString("displayName",    value.DisplayName); }
        if (value.Category       is not null) { writer.WriteString("category",       value.Category); }
        if (value.Description    is not null) { writer.WriteString("description",    value.Description); }
        if (value.Featured)                   { writer.WriteBoolean("featured",      true); }
        if (value.ExecutionLocus is not null) { writer.WriteString("executionLocus", value.ExecutionLocus); }
        writer.WriteEndObject();
    }
}
