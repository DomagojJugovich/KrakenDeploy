using System.Text.Json;
using System.Text.Json.Nodes;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD and Octopus Library import for <see cref="StepTemplate"/> entities.
/// </summary>
public class StepTemplateService(KrakenDbContext db)
{
    // ── Queries ────────────────────────────────────────────────────────────────

    public Task<List<StepTemplate>> GetAllAsync(CancellationToken ct = default)
        => db.StepTemplates.OrderBy(t => t.Name).ToListAsync(ct);

    public Task<StepTemplate?> GetAsync(Guid id, CancellationToken ct = default)
        => db.StepTemplates.FindAsync([id], ct).AsTask();

    // ── Create ─────────────────────────────────────────────────────────────────

    public async Task<StepTemplate> CreateAsync(
        string name,
        string actionType,
        string? description,
        Dictionary<string, string>? properties,
        List<StepTemplateParameter>? parameters,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);

        var template = new StepTemplate
        {
            Name       = name.Trim(),
            ActionType = actionType.Trim(),
            Description = description?.Trim(),
            Properties = properties ?? [],
            Parameters  = parameters ?? [],
        };

        db.StepTemplates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return template;
    }

    // ── Update ─────────────────────────────────────────────────────────────────

    public async Task<StepTemplate?> UpdateAsync(
        Guid id,
        string name,
        string? description,
        Dictionary<string, string>? properties,
        List<StepTemplateParameter>? parameters,
        CancellationToken ct = default)
    {
        var template = await db.StepTemplates.FindAsync([id], ct).ConfigureAwait(false);
        if (template is null)
        {
            return null;
        }

        template.Name        = name.Trim();
        template.Description = description?.Trim();
        template.Properties  = properties ?? [];
        template.Parameters  = parameters ?? [];
        template.Version++;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return template;
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var template = await db.StepTemplates.FindAsync([id], ct).ConfigureAwait(false);
        if (template is null)
        {
            return false;
        }

        db.StepTemplates.Remove(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Octopus Library import ─────────────────────────────────────────────────

    /// <summary>
    /// Parses an Octopus Community Library step-template JSON and upserts a
    /// <see cref="StepTemplate"/> in the database.
    /// If a template with the same <c>CommunityActionTemplateId</c> already exists it
    /// is updated; otherwise a new record is created.
    /// </summary>
    /// <param name="json">Raw JSON from the Library repo (single template object).</param>
    /// <param name="importSource">Human-readable source description (URL, filename, etc.).</param>
    public async Task<StepTemplate> ImportFromJsonAsync(
        string json,
        string? importSource = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var imported = OctopusLibraryImporter.Parse(json, importSource);

        // Upsert by CommunityTemplateId when present.
        StepTemplate? existing = null;
        if (!string.IsNullOrWhiteSpace(imported.CommunityTemplateId))
        {
            existing = await db.StepTemplates
                .FirstOrDefaultAsync(
                    t => t.CommunityTemplateId == imported.CommunityTemplateId, ct)
                .ConfigureAwait(false);
        }

        if (existing is null)
        {
            db.StepTemplates.Add(imported);
        }
        else
        {
            existing.Name               = imported.Name;
            existing.Description        = imported.Description;
            existing.ActionType         = imported.ActionType;
            existing.Properties         = imported.Properties;
            existing.Parameters         = imported.Parameters;
            existing.ImportedFrom       = imported.ImportedFrom;
            existing.Version++;
            imported = existing;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return imported;
    }
}

// ── DTOs ───────────────────────────────────────────────────────────────────────

/// <summary>Summary row returned by the list endpoint.</summary>
public sealed record StepTemplateSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    string ActionType,
    int ParameterCount,
    int Version,
    DateTimeOffset CreatedUtc);

// ── Octopus Library importer ───────────────────────────────────────────────────

/// <summary>
/// Parses the JSON format used by the <c>OctopusDeploy/Library</c> step-template repository
/// into a <see cref="StepTemplate"/> domain object.
/// </summary>
public static class OctopusLibraryImporter
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// Parses a single step-template JSON document.
    /// </summary>
    /// <param name="json">Raw Library JSON.</param>
    /// <param name="importSource">Optional human-readable source label.</param>
    public static StepTemplate Parse(string json, string? importSource = null)
    {
        var root = JsonNode.Parse(json, nodeOptions: null,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling  = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })?.AsObject()
            ?? throw new InvalidOperationException("JSON root is not an object.");

        var name             = root["Name"]?.GetValue<string>()?.Trim()
                               ?? throw new InvalidOperationException("'Name' is required.");
        var actionType       = root["ActionType"]?.GetValue<string>()?.Trim()
                               ?? throw new InvalidOperationException("'ActionType' is required.");
        var description      = root["Description"]?.GetValue<string>()?.Trim();
        var communityId      = root["CommunityActionTemplateId"]?.GetValue<string>()?.Trim();

        // ── Properties ────────────────────────────────────────────────────────
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root["Properties"] is JsonObject propertiesNode)
        {
            foreach (var (key, value) in propertiesNode)
            {
                if (value is not null)
                {
                    properties[key] = value.GetValue<string>();
                }
            }
        }

        // ── Parameters ────────────────────────────────────────────────────────
        var parameters = new List<StepTemplateParameter>();
        if (root["Parameters"] is JsonArray paramsArray)
        {
            foreach (var paramNode in paramsArray.OfType<JsonObject>())
            {
                var pName         = paramNode["Name"]?.GetValue<string>()?.Trim();
                var pLabel        = paramNode["Label"]?.GetValue<string>()?.Trim();
                var pHelp         = paramNode["HelpText"]?.GetValue<string>()?.Trim();
                var pDefault      = paramNode["DefaultValue"]?.GetValue<string>();
                var controlType   = "SingleLineText";
                var selectOptions = new List<string>();

                if (paramNode["DisplaySettings"] is JsonObject displaySettings)
                {
                    controlType = MapControlType(
                        displaySettings["Octopus.ControlType"]?.GetValue<string>(),
                        displaySettings,
                        out selectOptions);
                }

                if (string.IsNullOrWhiteSpace(pName) || string.IsNullOrWhiteSpace(pLabel))
                {
                    continue; // skip malformed parameter entries
                }

                parameters.Add(new StepTemplateParameter
                {
                    Name          = pName,
                    Label         = pLabel,
                    HelpText      = pHelp,
                    DefaultValue  = pDefault,
                    ControlType   = controlType,
                    SelectOptions = selectOptions,
                });
            }
        }

        return new StepTemplate
        {
            Name               = name,
            Description        = description,
            ActionType         = actionType,
            Properties         = properties,
            Parameters         = parameters,
            CommunityTemplateId = communityId,
            ImportedFrom       = importSource,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps Octopus <c>Octopus.ControlType</c> values to KrakenDeploy control types
    /// and extracts <c>SelectOptions</c> for Select controls.
    /// </summary>
    private static string MapControlType(
        string? octopusType,
        JsonObject displaySettings,
        out List<string> selectOptions)
    {
        selectOptions = [];

        return octopusType switch
        {
            "SingleLineText"     => "SingleLineText",
            "MultiLineText"      => "MultiLineText",
            "Sensitive"          => "Sensitive",
            "Checkbox"           => "Checkbox",
            "Package"            => "Package",
            "Select" or "DropDownList" => ParseSelect(displaySettings, out selectOptions),
            _                    => "SingleLineText",
        };
    }

    private static string ParseSelect(JsonObject displaySettings, out List<string> options)
    {
        options = [];

        // Octopus stores select options as a JSON-encoded string value in the key
        // "Octopus.SelectOptions".  Format: "value|Label\nvalue2|Label2"
        var raw = displaySettings["Octopus.SelectOptions"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var pipe = line.IndexOf('|');
                var val  = pipe >= 0 ? line[..pipe].Trim() : line.Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    options.Add(val);
                }
            }
        }

        return "Select";
    }
}
