using System.Text.Json;
using System.Text.Json.Nodes;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD and Octopus Library import for <see cref="StepTemplate"/> entities.
/// </summary>
public class StepTemplateService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPermissionEvaluator permissions)
{
    private async Task EnsureStepTemplateScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, Guid templateId,
        Permission permission, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var spaceId = await db.StepTemplates.IgnoreQueryFilters()
            .Where(t => t.Id == templateId)
            .Select(t => (Guid?)t.SpaceId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, permission,
            new PermissionScope(SpaceId: spaceId), ct).ConfigureAwait(false);
    }

    // ── Queries ────────────────────────────────────────────────────────────────

    public async Task<List<StepTemplate>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.StepTemplates.OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<StepTemplate?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.StepTemplates.FindAsync([id], ct).AsTask();
    }

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

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureStepTemplateScopeAsync(db, caller, id, Permission.StepTemplateEdit, ct).ConfigureAwait(false);

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
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
    /// <param name="source">
    /// Provenance of the import. Defaults to <see cref="StepTemplateSource.LocalImport"/>;
    /// the community-catalog browser passes <see cref="StepTemplateSource.CommunityLibrary"/>.
    /// </param>
    public async Task<StepTemplate> ImportFromJsonAsync(
        string json,
        string? importSource = null,
        StepTemplateSource source = StepTemplateSource.LocalImport,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var imported = OctopusLibraryImporter.Parse(json, importSource);
        imported.Source = source;

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
            existing.Category           = imported.Category;
            existing.Author             = imported.Author;
            existing.Website            = imported.Website;
            existing.LogoUrl            = imported.LogoUrl;
            existing.Source             = source;
            existing.Version++;
            imported = existing;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return imported;
    }

    /// <summary>
    /// Imports every template in an Octopus <c>/api/actiontemplates</c>
    /// paginated response (the JSON dump produced by hitting that endpoint or
    /// exported via the Octopus admin UI). The wrapper has shape
    /// <c>{ "ItemType": "ActionTemplate", "Items": [...] }</c>; each item is
    /// fed through <see cref="ImportFromJsonAsync"/> with
    /// <see cref="StepTemplateSource.LocalImport"/>. Returns the same
    /// added/updated/skipped/errored summary as
    /// <see cref="ImportFromDirectoryAsync"/>.
    /// </summary>
    public async Task<ImportFromDirectoryResult> ImportFromOctopusApiResponseAsync(
        string json, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling     = JsonCommentHandling.Skip,
                });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON: {ex.Message}", ex);
        }

        if (root?.AsObject() is not JsonObject obj
            || obj["Items"] is not JsonArray items)
        {
            throw new InvalidOperationException(
                "Expected an Octopus /api/actiontemplates response — a JSON object with an 'Items' array. " +
                "For a single step template, use 'Import JSON' instead.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existingCommunityIds = await db.StepTemplates
            .Where(t => t.CommunityTemplateId != null)
            .Select(t => t.CommunityTemplateId!)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        var added   = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var errors  = new List<ImportFromDirectoryError>();

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            if (item is null)
            {
                continue;
            }

            var name = item["Name"]?.GetValue<string>() ?? $"#{i}";
            var itemJson = item.ToJsonString();

            try
            {
                var parsed = OctopusLibraryImporter.Parse(itemJson, importSource: name);
                var isUpdate = !string.IsNullOrWhiteSpace(parsed.CommunityTemplateId)
                    && existingCommunityIds.Contains(parsed.CommunityTemplateId!);

                await ImportFromJsonAsync(
                    itemJson,
                    importSource: $"octopus-api: {name}",
                    source: StepTemplateSource.LocalImport,
                    ct: ct).ConfigureAwait(false);

                (isUpdate ? updated : added).Add(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                skipped.Add(name);
                _ = ex;
            }
            catch (Exception ex)
            {
                errors.Add(new ImportFromDirectoryError(name, ex.Message));
            }
        }

        return new ImportFromDirectoryResult(
            ScannedFiles: items.Count,
            Added:        added.Count,
            Updated:      updated.Count,
            Skipped:      skipped.Count,
            Errored:      errors.Count,
            Errors:       errors);
    }

    // ── Bulk import ────────────────────────────────────────────────────────────

    /// <summary>
    /// Recursively scans <paramref name="folderPath"/> for <c>*.json</c> files
    /// and calls <see cref="ImportFromJsonAsync"/> on each. Tracks added /
    /// updated / skipped (not-a-template) / errored counts and returns a
    /// summary. Intended for bulk-loading a clone of the
    /// <c>OctopusDeploy/Library</c> repo's <c>step-templates/</c> directory.
    /// Sets <see cref="StepTemplateSource.LocalImport"/> on every row.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// The folder does not exist.
    /// </exception>
    public async Task<ImportFromDirectoryResult> ImportFromDirectoryAsync(
        string folderPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException(
                $"Folder '{folderPath}' not found on the server.");
        }

        var files = Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories);
        var added   = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var errors  = new List<ImportFromDirectoryError>();

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existingCommunityIds = await db.StepTemplates
            .Where(t => t.CommunityTemplateId != null)
            .Select(t => t.CommunityTemplateId!)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            string json;
            try
            {
                json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(new ImportFromDirectoryError(file, $"Read failed: {ex.Message}"));
                continue;
            }

            try
            {
                var parsed = OctopusLibraryImporter.Parse(json, importSource: Path.GetFileName(file));
                var isUpdate = !string.IsNullOrWhiteSpace(parsed.CommunityTemplateId)
                    && existingCommunityIds.Contains(parsed.CommunityTemplateId!);

                await ImportFromJsonAsync(
                    json,
                    importSource: Path.GetFileName(file),
                    source: StepTemplateSource.LocalImport,
                    ct: ct).ConfigureAwait(false);

                if (isUpdate)
                {
                    updated.Add(file);
                }
                else
                {
                    added.Add(file);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                // Treat "Name/ActionType required" failures as "not a step template" —
                // common when scanning a Library clone that also contains README.json etc.
                skipped.Add(file);
                _ = ex;
            }
            catch (Exception ex)
            {
                errors.Add(new ImportFromDirectoryError(file, ex.Message));
            }
        }

        return new ImportFromDirectoryResult(
            ScannedFiles: files.Length,
            Added:        added.Count,
            Updated:      updated.Count,
            Skipped:      skipped.Count,
            Errored:      errors.Count,
            Errors:       errors);
    }
}

/// <summary>Result of <see cref="StepTemplateService.ImportFromDirectoryAsync"/>.</summary>
public sealed record ImportFromDirectoryResult(
    int ScannedFiles,
    int Added,
    int Updated,
    int Skipped,
    int Errored,
    IReadOnlyList<ImportFromDirectoryError> Errors);

/// <summary>A per-file error during a bulk directory import.</summary>
public sealed record ImportFromDirectoryError(string File, string Message);

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

// ── Octopus Library exporter ───────────────────────────────────────────────────

/// <summary>
/// Serialises a <see cref="StepTemplate"/> back to the JSON format used by the
/// <c>OctopusDeploy/Library</c> repository. Round-trips with
/// <see cref="OctopusLibraryImporter.Parse(string, string?)"/>.
/// </summary>
public static class OctopusLibraryExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Returns the template as pretty-printed Library JSON.</summary>
    public static string Serialize(StepTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var root = new JsonObject
        {
            ["Id"]                        = template.CommunityTemplateId ?? template.Id.ToString(),
            ["Name"]                      = template.Name,
            ["Description"]               = template.Description ?? "",
            ["ActionType"]                = template.ActionType,
            ["Version"]                   = template.Version,
            ["CommunityActionTemplateId"] = template.CommunityTemplateId,
            ["Properties"]                = BuildPropertiesNode(template.Properties),
            ["Parameters"]                = BuildParametersNode(template.Parameters),
            ["Category"]                  = template.Category,
            ["Author"]                    = template.Author,
            ["Website"]                   = template.Website,
            ["LogoUrl"]                   = template.LogoUrl,
            ["$Meta"] = new JsonObject
            {
                ["ExportedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["ExportedBy"] = "KrakenDeploy",
            },
        };

        return root.ToJsonString(JsonOpts);
    }

    private static JsonObject BuildPropertiesNode(Dictionary<string, string> properties)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in properties)
        {
            obj[k] = v;
        }
        return obj;
    }

    private static JsonArray BuildParametersNode(List<StepTemplateParameter> parameters)
    {
        var arr = new JsonArray();
        foreach (var p in parameters)
        {
            var entry = new JsonObject
            {
                ["Name"]         = p.Name,
                ["Label"]        = p.Label,
                ["HelpText"]     = p.HelpText,
                ["DefaultValue"] = p.DefaultValue,
                ["DisplaySettings"] = BuildDisplaySettings(p),
            };
            arr.Add(entry);
        }
        return arr;
    }

    private static JsonObject BuildDisplaySettings(StepTemplateParameter p)
    {
        var ds = new JsonObject
        {
            ["Octopus.ControlType"] = MapKrakenControlTypeToOctopus(p.ControlType),
        };

        if (p.SelectOptions.Count > 0)
        {
            // Octopus stores them newline-joined as "value|Label" lines.
            ds["Octopus.SelectOptions"] = string.Join('\n', p.SelectOptions);
        }

        return ds;
    }

    private static string MapKrakenControlTypeToOctopus(string krakenControlType) =>
        krakenControlType switch
        {
            "SingleLineText" => "SingleLineText",
            "MultiLineText"  => "MultiLineText",
            "Sensitive"      => "Sensitive",
            "Checkbox"       => "Checkbox",
            "Package"        => "Package",
            "Select"         => "Select",
            _                => "SingleLineText",
        };
}

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
        // Library JSON files use "Id"; the Octopus API uses "CommunityActionTemplateId".
        // Accept both so imports work from either source.
        var communityId      = (root["CommunityActionTemplateId"] ?? root["Id"])
                               ?.GetValue<string>()?.Trim();
        var category         = root["Category"]?.GetValue<string>()?.Trim();
        var author           = root["Author"]?.GetValue<string>()?.Trim();
        var website          = (root["Website"] ?? root["WebsiteUrl"])
                               ?.GetValue<string>()?.Trim();
        var logoUrl          = (root["LogoUrl"] ?? root["Logo"])
                               ?.GetValue<string>()?.Trim();

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
            Name                = name,
            Description         = description,
            ActionType          = actionType,
            Properties          = properties,
            Parameters          = parameters,
            CommunityTemplateId = communityId,
            ImportedFrom        = importSource,
            Category            = category,
            Author              = author,
            Website             = website,
            LogoUrl             = logoUrl,
            // Caller (StepTemplateService.ImportFromJsonAsync) overrides this
            // to CommunityLibrary / LocalImport based on the actual entry point.
            Source              = StepTemplateSource.LocalImport,
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
            // Keep the full "value|Label" string so the UI can show human-readable
            // labels while submitting the machine value.  Real Library templates use
            // entries like "0|Local System" — stripping to "0" makes dropdowns unreadable.
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    options.Add(trimmed);
                }
            }
        }

        return "Select";
    }
}
