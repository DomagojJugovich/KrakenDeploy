using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Maintains the <c>step_types</c> registry (SC3 / SD-1): the sole authoring
/// authority for what types exist, their picker metadata, and which installed
/// package serves them. <see cref="RebuildAsync"/> is a full, idempotent
/// recompute from installed packages' manifests — called after every install,
/// uninstall, and seed pass, plus once at boot (which also heals the SC2
/// migration's max-version-string approximation into a true semver pick).
/// <para>
/// Package-source rows are entirely machine-managed here; the two System rows
/// (<c>kraken.stepgroup</c>, <c>octopus.deployrelease</c>) are ensured to
/// exist and never derived from packages. Rebuilds are serialised per process
/// — concurrent installs are rare and a rebuild is cheap at catalog scale
/// (tens of packages), so last-writer-wins with a gate is sufficient.
/// </para>
/// </summary>
public sealed class StepTypeRegistry(
    IDbContextFactory<KrakenDbContext> dbFactory,
    ILogger<StepTypeRegistry>? logger = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly ILogger _log = logger ?? NullLogger<StepTypeRegistry>.Instance;

    /// <summary>The System rows, in one place — the migration seeds copies for pre-existing DBs; this heals everything else.</summary>
    private static readonly (string TypeId, string DisplayName, string Category, string Description, bool Featured, StepTypeExecutionLocus Locus)[]
        SystemRows =
        [
            ("kraken.stepgroup", "Step Group", "control",
             "Container for child steps. Add a ForEach loop, or run multiple actions in parallel on the same target.",
             true, StepTypeExecutionLocus.Structural),
            ("octopus.deployrelease", "Deploy a Release", "other",
             "Server-side child deployment of another project's release.",
             false, StepTypeExecutionLocus.ServerRunner),
        ];

    /// <summary>All registry rows, for the picker and admin surfaces (SC5).</summary>
    public async Task<List<StepTypeEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.StepTypes.AsNoTracking()
            .OrderBy(t => t.DisplayName)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task RebuildAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Ordered so a version tie between two packages resolves the same way
            // on every rebuild — an unordered query hands the choice to Postgres
            // heap order, which can flip after any UPDATE or VACUUM and silently
            // move a type's serving package (hence its schema) underneath users.
            var packages = await db.StepPackages.AsNoTracking()
                .OrderBy(p => p.Name).ThenBy(p => p.Version)
                .Select(p => new { p.Name, p.Version, p.ManifestJson, p.Source })
                .ToListAsync(ct).ConfigureAwait(false);

            // type id → candidate claims across every installed (name, version).
            var candidates = new Dictionary<
                string, List<(string Name, string Version, StepTypeDeclaration Decl,
                              string PackageDisplayName, StepPackageSource Source)>>(
                StringComparer.Ordinal);

            foreach (var pkg in packages)
            {
                StepPackageManifest manifest;
                try
                {
                    manifest = StepPackageManifestJson.Deserialize(pkg.ManifestJson);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
                {
                    _log.LogWarning(ex,
                        "StepTypeRegistry: skipping {Name} {Version} — stored manifest failed to parse.",
                        pkg.Name, pkg.Version);
                    continue;
                }

                foreach (var decl in manifest.StepTypes)
                {
                    var typeId = decl.Id.Trim().ToLowerInvariant();
                    if (typeId.Length == 0) { continue; }

                    // type_id is varchar(200) while step_packages.step_types is
                    // varchar(500), so an over-long id can install and then abort
                    // every rebuild on the single SaveChanges below. Uploads are
                    // refused upstream now; skip anything already stored.
                    if (typeId.Length > StepTypeMetadataLimits.TypeId)
                    {
                        _log.LogWarning(
                            "StepTypeRegistry: {Name} {Version} declares a step-type id of " +
                            "{Length} characters (max {Max}) — skipped.",
                            pkg.Name, pkg.Version, typeId.Length, StepTypeMetadataLimits.TypeId);
                        continue;
                    }

                    if (!candidates.TryGetValue(typeId, out var list))
                    {
                        candidates[typeId] = list = [];
                    }
                    list.Add((pkg.Name, pkg.Version, decl, manifest.DisplayName, pkg.Source));
                }
            }

            // Winner per type: highest semver among every claiming (name, version)
            // — the same choice StepPackageResolver makes when pinning.
            var computed = new Dictionary<string, (string Name, string Version, StepTypeDeclaration Decl,
                                                   string PackageDisplayName, StepPackageSource Source)>(
                StringComparer.Ordinal);
            foreach (var (typeId, list) in candidates)
            {
                // Ownership: once a trusted-source package (built-in or the
                // official catalog — StepPackageSourceExtensions.OwnsClaimedTypes)
                // claims a type, only packages of that same NAME may serve it.
                // Without this, any upload declaring e.g. kraken.script at 99.0.0
                // wins the semver pick and takes over the type's schema, picker
                // metadata and — via Apply — its ExecutionLocus, for every user.
                // The upload-time reserved-type guard normally stops such a
                // package installing at all; this is the defence-in-depth pick.
                var ownerNames = list
                    .Where(c => c.Source.OwnsClaimedTypes())
                    .Select(c => c.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var pool = list;
                if (ownerNames.Count > 0)
                {
                    pool = [.. list.Where(c => ownerNames.Contains(c.Name))];
                    foreach (var rejected in list.Where(c => !ownerNames.Contains(c.Name)))
                    {
                        _log.LogWarning(
                            "StepTypeRegistry: package {Name} {Version} claims '{TypeId}', which is " +
                            "owned by built-in package(s) {Owners} — ignored.",
                            rejected.Name, rejected.Version, typeId, string.Join(", ", ownerNames));
                    }
                }

                var winningVersion = StepPackageResolver.PickHighestSemver(
                    [.. pool.Select(c => c.Version)]);
                if (winningVersion is null) { continue; }

                // pool follows the query's (name, version) ordering, so a tie on the
                // version string resolves deterministically instead of by heap order.
                computed[typeId] = pool.First(c => c.Version == winningVersion);
            }

            var existing = await db.StepTypes.ToListAsync(ct).ConfigureAwait(false);
            var systemIds = SystemRows.Select(s => s.TypeId).ToHashSet(StringComparer.Ordinal);

            foreach (var row in existing)
            {
                if (row.Source == StepTypeEntrySource.System) { continue; }

                // A Package-sourced row occupying a System type id must not be fed
                // package metadata — Apply would flip Structural/ServerRunner to
                // AgentPackage and stamp Source=Package. Convert it back in place
                // rather than Remove + Add: both would live in one SaveChanges and
                // collide on ix_step_types_type_id.
                if (systemIds.Contains(row.TypeId))
                {
                    _log.LogWarning(
                        "StepTypeRegistry: restoring System row for '{TypeId}' — it was " +
                        "stored as a package-derived row.", row.TypeId);
                    ApplySystemRow(row, SystemRows.First(s => s.TypeId == row.TypeId));
                    computed.Remove(row.TypeId);
                    continue;
                }

                if (computed.TryGetValue(row.TypeId, out var c))
                {
                    Apply(row, c);
                    computed.Remove(row.TypeId);
                }
                else
                {
                    db.StepTypes.Remove(row); // no installed package claims it anymore
                }
            }

            foreach (var (typeId, c) in computed)
            {
                if (systemIds.Contains(typeId))
                {
                    _log.LogWarning(
                        "StepTypeRegistry: package {Name} claims the System type '{TypeId}' — ignored.",
                        c.Name, typeId);
                    continue;
                }

                var row = new StepTypeEntry
                {
                    TypeId         = typeId,
                    DisplayName    = "", // set by Apply
                    ExecutionLocus = StepTypeExecutionLocus.AgentPackage,
                    Source         = StepTypeEntrySource.Package,
                };
                Apply(row, c);
                db.StepTypes.Add(row);
            }

            // Ensure the System rows exist (heals databases created without
            // running the SC2 migration's data script, e.g. EnsureCreated).
            // Keyed on rows that are System-sourced AFTER the loop above, not on
            // the pre-rebuild snapshot: a snapshot would let a row this pass just
            // deleted suppress its own replacement, leaving the type with no row
            // at all and the dispatch guard refusing every deployment using it.
            var systemRowIds = existing
                .Where(r => r.Source == StepTypeEntrySource.System)
                .Select(r => r.TypeId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var s in SystemRows)
            {
                if (systemRowIds.Contains(s.TypeId)) { continue; }
                var row = new StepTypeEntry
                {
                    TypeId         = s.TypeId,
                    DisplayName    = s.DisplayName,
                    ExecutionLocus = s.Locus,
                    Source         = StepTypeEntrySource.System,
                };
                ApplySystemRow(row, s);
                db.StepTypes.Add(row);
            }

            var changes = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (changes > 0)
            {
                _log.LogInformation(
                    "StepTypeRegistry: rebuild applied {Changes} change(s) across {Types} package-served type(s).",
                    changes, candidates.Count);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Restores a row to its canonical System definition.</summary>
    private static void ApplySystemRow(
        StepTypeEntry row,
        (string TypeId, string DisplayName, string Category, string Description,
         bool Featured, StepTypeExecutionLocus Locus) s)
    {
        row.DisplayName           = s.DisplayName;
        row.Category              = s.Category;
        row.Description           = s.Description;
        row.Featured              = s.Featured;
        row.ExecutionLocus        = s.Locus;
        row.Source                = StepTypeEntrySource.System;
        row.ServingPackageName    = null;
        row.ServingPackageVersion = null;
    }

    /// <summary>
    /// Clips manifest metadata to the registry's column widths. Uploads are
    /// refused upstream when they exceed these, but a package installed before
    /// that validation existed must degrade to a truncated label rather than
    /// abort the whole recompute with a Postgres 22001 that
    /// <c>RefreshRegistryAsync</c> then swallows.
    /// </summary>
    private static string? Clip(string? value, int max)
    {
        if (value is null || value.Length <= max) { return value; }
        // Don't split a surrogate pair at the boundary — a lone half renders
        // as mojibake. Back off one code unit when the cut lands mid-pair.
        var end = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..end];
    }

    private static void Apply(
        StepTypeEntry row,
        (string Name, string Version, StepTypeDeclaration Decl, string PackageDisplayName,
         StepPackageSource Source) c)
    {
        row.DisplayName           = Clip(c.Decl.DisplayName ?? c.PackageDisplayName,
                                         StepTypeMetadataLimits.DisplayName)!;
        row.Category              = Clip(c.Decl.Category,    StepTypeMetadataLimits.Category);
        row.Description           = Clip(c.Decl.Description, StepTypeMetadataLimits.Description);
        row.Featured              = c.Decl.Featured;
        // SC4: a package type may declare server-side orchestration in its
        // manifest (Octopus.Manual's task-global gate); default is the agent.
        row.ExecutionLocus        = string.Equals(
                c.Decl.ExecutionLocus, StepTypeDeclaration.ServerLocus,
                StringComparison.OrdinalIgnoreCase)
            ? StepTypeExecutionLocus.ServerRunner
            : StepTypeExecutionLocus.AgentPackage;
        row.Source                = StepTypeEntrySource.Package;
        // No clip: c.Name/c.Version come from step_packages rows whose columns
        // are already varchar(200)/(64) (StepPackageConfiguration), so they can
        // never exceed the matching step_types column widths.
        row.ServingPackageName    = c.Name;
        row.ServingPackageVersion = c.Version;
    }
}
