using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// SC4: the ONE schema-resolution path for the step editor, replacing the
/// hardcoded <c>BuiltInStepSchemas</c> registry. Resolution order (SD-6):
/// <list type="number">
///   <item>the schema shipped by the package VERSION the step is pinned to;</item>
///   <item>the registry's serving package's schema for the type — with a
///         notice when a pin existed but carried no schema (pre-SC1
///         version still installed);</item>
///   <item>nothing — the caller falls back to preset parameters
///         (<c>StepTemplateSchemaAdapter</c>) or shows its no-schema error.</item>
/// </list>
/// Community/user presets are NOT resolved here: a preset's form is its own
/// parameter list, owned by the template row, below packages by design (SD-2).
/// </summary>
public sealed class StepSchemaResolver(
    IDbContextFactory<KrakenDbContext> dbFactory,
    ILogger<StepSchemaResolver> logger)
{
    /// <summary>A resolved schema plus where it came from.</summary>
    public sealed record Resolution(
        StepUiSchema Schema,
        string SourcePackageName,
        string SourcePackageVersion,
        /// <summary>Non-null when the step's pin could not provide the schema — tells the operator what they're looking at instead.</summary>
        string? Notice);

    /// <summary>
    /// Resolves the schema for <paramref name="stepType"/>, preferring the
    /// pinned package version when given. Returns <c>null</c> when neither
    /// the pin nor the registry's serving package carries a schema for the
    /// type — the caller decides the fallback.
    /// </summary>
    public async Task<Resolution?> ResolveAsync(
        string stepType,
        string? pinnedPackageName = null,
        string? pinnedPackageVersion = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepType);
        var typeId = stepType.Trim().ToLowerInvariant();

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // 1 — the pinned version's own schema.
        if (!string.IsNullOrWhiteSpace(pinnedPackageName)
            && !string.IsNullOrWhiteSpace(pinnedPackageVersion))
        {
            var pinned = await LoadAsync(
                db, pinnedPackageName, pinnedPackageVersion, typeId, ct).ConfigureAwait(false);
            if (pinned is not null)
            {
                return new Resolution(pinned, pinnedPackageName, pinnedPackageVersion, Notice: null);
            }
        }

        // 2 — the serving package's newest schema, per the registry.
        var entry = await db.StepTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TypeId == typeId, ct).ConfigureAwait(false);
        if (entry?.ServingPackageName is null || entry.ServingPackageVersion is null)
        {
            return null;
        }

        var serving = await LoadAsync(
            db, entry.ServingPackageName, entry.ServingPackageVersion, typeId, ct)
            .ConfigureAwait(false);
        if (serving is null) { return null; }

        var notice = pinnedPackageVersion is not null
            && !string.Equals(pinnedPackageVersion, entry.ServingPackageVersion, StringComparison.Ordinal)
            ? $"Pinned version {pinnedPackageVersion} ships no schema for this step type — " +
              $"showing the form of {entry.ServingPackageName} {entry.ServingPackageVersion}."
            : null;

        return new Resolution(
            serving, entry.ServingPackageName, entry.ServingPackageVersion, notice);
    }

    /// <summary>
    /// The exact schema one installed (name, version) ships for a type —
    /// used by the editor's version-switch dropdown (D-7.2). <c>null</c>
    /// when that version carries no schema for the type.
    /// </summary>
    public async Task<StepUiSchema?> GetSchemaAsync(
        string packageName, string packageVersion, string stepType,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await LoadAsync(
            db, packageName, packageVersion, stepType.Trim().ToLowerInvariant(), ct)
            .ConfigureAwait(false);
    }

    private async Task<StepUiSchema?> LoadAsync(
        KrakenDbContext db, string name, string version, string typeId, CancellationToken ct)
    {
        var json = await db.StepPackageSchemas.AsNoTracking()
            .Where(s => s.StepType == typeId)
            .Join(db.StepPackages.Where(p => p.Name == name && p.Version == version),
                  s => s.StepPackageId, p => p.Id, (s, _) => s.SchemaJson)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (json is null) { return null; }

        try
        {
            return StepUiSchemaJson.Deserialize(json);
        }
        catch (Exception ex) when (
            ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Upload validation should make this unreachable; a corrupt row
            // must degrade to "no schema", not take down the editor.
            logger.LogError(ex,
                "Stored schema for {Name} {Version} / {Type} failed to parse.",
                name, version, typeId);
            return null;
        }
    }
}
