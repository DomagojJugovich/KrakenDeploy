using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Whole-project export / import for the Projects page (Octopus's
/// "Import/Export" parity). The export is a Kraken envelope whose
/// <c>DeploymentProcess</c> is the exact Octopus <c>deploymentprocess</c>
/// shape, so the SAME file round-trips through
/// <see cref="OctopusDeploymentProcessImporter"/> AND a raw process JSON
/// taken from a real Octopus instance imports too (the probe accepts both).
/// </summary>
public sealed class ProjectTransferService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<ProjectExportResult> ExportAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var project = await db.Projects
            .AsNoTracking()
            .Include(p => p.ProjectGroup)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Project not found.");

        var process = await db.DeploymentProcesses
            .AsNoTracking()
            .Include(p => p.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(p => p.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        var (processObj, warnings) = OctopusDeploymentProcessExporter.Export(
            process?.Steps.ToList() ?? []);

        var envelope = new JsonObject
        {
            ["FormatVersion"] = 1,
            ["Exporter"] = "KrakenDeploy",
            ["ExportedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["Project"] = new JsonObject
            {
                ["Name"] = project.Name,
                ["Slug"] = project.Slug,
                ["Description"] = project.Description,
                ["ProjectGroup"] = project.ProjectGroup?.Name,
            },
            ["DeploymentProcess"] = processObj,
        };

        // Mapping notes ride along in the file so the operator who receives
        // it sees what didn't translate 1:1 (ignored on import).
        if (warnings.Count > 0)
        {
            envelope["ExportNotes"] = new JsonArray(
                [.. warnings.Select(w => (JsonNode)$"{w.StepName}: {w.Message}")]);
        }

        return new ProjectExportResult(
            $"kraken-project-{project.Slug}.json",
            envelope.ToJsonString(Indented),
            warnings);
    }

    /// <summary>
    /// Inspects pasted/uploaded JSON and classifies it: a Kraken project
    /// envelope (project metadata + embedded process) or a raw Octopus
    /// <c>deploymentprocess</c> (top-level <c>Steps</c>). Throws
    /// <see cref="InvalidOperationException"/> for anything else. The
    /// returned <c>ProcessJson</c> always feeds
    /// <see cref="ProcessService.ImportDeploymentProcessAsync"/> directly.
    /// </summary>
    public static ProjectImportProbe Probe(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Not valid JSON: {ex.Message}", ex);
        }

        if (node is not JsonObject obj)
        {
            throw new InvalidOperationException("Expected a JSON object at the top level.");
        }

        if (obj["DeploymentProcess"] is JsonObject processObj)
        {
            var proj = obj["Project"] as JsonObject;
            return new ProjectImportProbe(
                IsKrakenEnvelope: true,
                Name: proj?["Name"]?.GetValue<string>(),
                Slug: proj?["Slug"]?.GetValue<string>(),
                Description: proj?["Description"]?.GetValue<string>(),
                ProjectGroup: proj?["ProjectGroup"]?.GetValue<string>(),
                ProcessJson: processObj.ToJsonString());
        }

        if (obj["Steps"] is JsonArray)
        {
            return new ProjectImportProbe(
                IsKrakenEnvelope: false,
                Name: null, Slug: null, Description: null, ProjectGroup: null,
                ProcessJson: json);
        }

        throw new InvalidOperationException(
            "JSON is neither a Kraken project export (missing 'DeploymentProcess') " +
            "nor an Octopus deploymentprocess (missing 'Steps').");
    }
}

public sealed record ProjectExportResult(
    string FileName,
    string Json,
    IReadOnlyList<ImportDeploymentProcessWarning> Warnings);

public sealed record ProjectImportProbe(
    bool IsKrakenEnvelope,
    string? Name,
    string? Slug,
    string? Description,
    string? ProjectGroup,
    string ProcessJson);
