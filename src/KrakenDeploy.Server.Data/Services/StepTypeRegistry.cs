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

    public async Task RebuildAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var packages = await db.StepPackages.AsNoTracking()
                .Select(p => new { p.Name, p.Version, p.ManifestJson })
                .ToListAsync(ct).ConfigureAwait(false);

            // type id → candidate claims across every installed (name, version).
            var candidates = new Dictionary<
                string, List<(string Name, string Version, StepTypeDeclaration Decl, string PackageDisplayName)>>(
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

                    if (!candidates.TryGetValue(typeId, out var list))
                    {
                        candidates[typeId] = list = [];
                    }
                    list.Add((pkg.Name, pkg.Version, decl, manifest.DisplayName));
                }
            }

            // Winner per type: highest semver among every claiming (name, version)
            // — the same choice StepPackageResolver makes when pinning.
            var computed = new Dictionary<string, (string Name, string Version, StepTypeDeclaration Decl, string PackageDisplayName)>(
                StringComparer.Ordinal);
            foreach (var (typeId, list) in candidates)
            {
                var winningVersion = StepPackageResolver.PickHighestSemver(
                    [.. list.Select(c => c.Version)]);
                if (winningVersion is null) { continue; }
                computed[typeId] = list.First(c => c.Version == winningVersion);
            }

            var existing = await db.StepTypes.ToListAsync(ct).ConfigureAwait(false);
            var systemIds = SystemRows.Select(s => s.TypeId).ToHashSet(StringComparer.Ordinal);

            foreach (var row in existing)
            {
                if (row.Source == StepTypeEntrySource.System) { continue; }

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
            var existingIds = existing.Select(r => r.TypeId).ToHashSet(StringComparer.Ordinal);
            foreach (var s in SystemRows)
            {
                if (existingIds.Contains(s.TypeId)) { continue; }
                db.StepTypes.Add(new StepTypeEntry
                {
                    TypeId         = s.TypeId,
                    DisplayName    = s.DisplayName,
                    Category       = s.Category,
                    Description    = s.Description,
                    Featured       = s.Featured,
                    ExecutionLocus = s.Locus,
                    Source         = StepTypeEntrySource.System,
                });
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

    private static void Apply(
        StepTypeEntry row,
        (string Name, string Version, StepTypeDeclaration Decl, string PackageDisplayName) c)
    {
        row.DisplayName           = c.Decl.DisplayName ?? c.PackageDisplayName;
        row.Category              = c.Decl.Category;
        row.Description           = c.Decl.Description;
        row.Featured              = c.Decl.Featured;
        // SC4: a package type may declare server-side orchestration in its
        // manifest (Octopus.Manual's task-global gate); default is the agent.
        row.ExecutionLocus        = string.Equals(
                c.Decl.ExecutionLocus, StepTypeDeclaration.ServerLocus,
                StringComparison.OrdinalIgnoreCase)
            ? StepTypeExecutionLocus.ServerRunner
            : StepTypeExecutionLocus.AgentPackage;
        row.Source                = StepTypeEntrySource.Package;
        row.ServingPackageName    = c.Name;
        row.ServingPackageVersion = c.Version;
    }
}
