using System.Text.Json;
using KrakenDeploy.Contracts.Steps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Parses a step's <c>Octopus.Action.Package.PackageReferences</c> JSON-encoded
/// array and resolves any missing <c>Version</c> fields to the latest uploaded
/// version of each <c>PackageId</c>. Used by <c>DeploymentWorker</c> (the unified
/// orchestrator for both deployments and runbook runs) when building
/// <c>DeploymentStepPlan</c>s for the agent so the agent doesn't have to talk to
/// the package store catalog itself.
/// <para>
/// Resolution model is "latest at dispatch time" for unpinned references — the
/// release-snapshot pin for primary packages already covers reproducibility;
/// channel-rule version resolution for referenced packages is a future
/// extension.
/// </para>
/// </summary>
public static class PackageReferenceResolver
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns the parsed + version-resolved list, or an empty array if the
    /// step has no referenced packages. Unknown or empty <c>PackageId</c>s
    /// are silently dropped.
    /// </summary>
    public static async Task<List<PackageReference>> ResolveAsync(
        IReadOnlyDictionary<string, string> stepConfig,
        IDbContextFactory<KrakenDbContext> dbFactory,
        ILogger logger,
        CancellationToken ct)
    {
        if (!stepConfig.TryGetValue(KrakenScriptConfigKeys.PackageReferences, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        List<PackageReference> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<PackageReference>>(raw, JsonOpts) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Failed to parse {Key} as JSON; treating step as having no referenced packages.",
                KrakenScriptConfigKeys.PackageReferences);
            return [];
        }

        if (parsed.Count == 0)
        {
            return [];
        }

        // Resolve missing versions via a single round-trip per distinct PackageId.
        var distinctIds = parsed
            .Where(r => string.IsNullOrWhiteSpace(r.Version) && !string.IsNullOrWhiteSpace(r.PackageId))
            .Select(r => r.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var latestById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (distinctIds.Length > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            foreach (var id in distinctIds)
            {
                var latest = await db.Packages
                    .Where(p => p.PackageId == id)
                    .OrderByDescending(p => p.UploadedUtc)
                    .Select(p => p.Version)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(latest))
                {
                    latestById[id] = latest;
                }
            }
        }

        var resolved = new List<PackageReference>(parsed.Count);
        foreach (var r in parsed)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.PackageId))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(r.Version))
            {
                resolved.Add(r);
                continue;
            }
            if (latestById.TryGetValue(r.PackageId, out var v))
            {
                resolved.Add(r with { Version = v });
            }
            else
            {
                logger.LogWarning(
                    "Referenced package '{Name}' ({PackageId}) has no uploaded versions; skipping.",
                    r.Name, r.PackageId);
            }
        }

        return resolved;
    }
}
