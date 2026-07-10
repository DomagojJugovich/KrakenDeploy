using System.Text.Json;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Agent package-download ENTITLEMENT: may the connecting agent's target download
/// a given package / step-package? Unlike <see cref="AgentDeploymentOwnership"/>
/// (which keys on a known deployment id), the gRPC delivery requests carry only
/// the package coordinates, so entitlement is the historical, status-agnostic
/// relation "some deployment dispatched to this target references it".
/// <para>
/// Status-agnostic on purpose: the agent's on-disk cache is the only re-download
/// guard, so a retry of a completed/failed deployment after a cache eviction must
/// still be allowed — gating on "active deployment" would false-deny it.
/// </para>
/// <para>
/// The target→deployment→release hops are SQL (the
/// <c>deployment_target_assignments</c> join, filter-free — the agent
/// control plane has no ambient Space). The last hop release→package is NOT
/// SQL-queryable: <c>Release.ProcessSnapshot</c> is an opaque jsonb-via-ValueConverter
/// string, so the snapshots are materialised and scanned in memory. This runs at
/// most once per (package,version) per agent (the agent caches), never per byte.
/// </para>
/// <para>
/// Matching is on package-ID membership, not exact version: a delta base is a
/// different version of the same id, and referenced-package versions may be
/// "latest"-resolved after the snapshot was taken — requiring an exact version
/// would false-deny legitimate fetches. The hole this closes is cross-package
/// exfiltration (an agent pulling a package id that NO deployment of its target uses).
/// </para>
/// </summary>
public static class AgentPackageEntitlement
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>True if a deployment dispatched to <paramref name="targetId"/>
    /// references <paramref name="packageId"/> as a primary or referenced package.</summary>
    public static async Task<bool> TargetMayDownloadPackageAsync(
        KrakenDbContext db, Guid targetId, string packageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        foreach (var snapshot in await ReachableSnapshotsAsync(db, targetId, ct).ConfigureAwait(false))
        {
            foreach (var step in snapshot)
            {
                if (Matches(step.PackageId, packageId))
                {
                    return true;
                }

                foreach (var referencedId in ReferencedPackageIds(step))
                {
                    if (Matches(referencedId, packageId))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>True if a deployment dispatched to <paramref name="targetId"/>
    /// references step-package <paramref name="stepPackageName"/>.</summary>
    public static async Task<bool> TargetMayDownloadStepPackageAsync(
        KrakenDbContext db, Guid targetId, string stepPackageName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stepPackageName))
        {
            return false;
        }

        foreach (var snapshot in await ReachableSnapshotsAsync(db, targetId, ct).ConfigureAwait(false))
        {
            foreach (var step in snapshot)
            {
                if (Eq(step.StepPackageName, stepPackageName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The release process-snapshots of every deployment dispatched to
    /// <paramref name="targetId"/> (via the assignment join — the single
    /// authority for the target set). Release ids are resolved in SQL; the
    /// snapshots are then materialised (jsonb → CLR) for in-memory scanning.
    /// </summary>
    private static async Task<List<List<StepSnapshot>>> ReachableSnapshotsAsync(
        KrakenDbContext db, Guid targetId, CancellationToken ct)
    {
        // Package entitlement is a deployment concern (releases pin packages);
        // restrict the assignment scan to deployment-kind tasks and read the
        // release id off the Deployment discriminated type.
        var releaseIds = await db.TaskTargetAssignments.IgnoreQueryFilters()
            .Where(a => a.TargetId == targetId && a.Task is Deployment)
            .Select(a => ((Deployment)a.Task).ReleaseId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (releaseIds.Count == 0)
        {
            return [];
        }

        return await db.Releases.IgnoreQueryFilters()
            .Where(r => releaseIds.Contains(r.Id))
            .Select(r => r.ProcessSnapshot)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static List<string> ReferencedPackageIds(StepSnapshot step)
    {
        if (!step.Config.TryGetValue(KrakenScriptConfigKeys.PackageReferences, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<PackageReference>>(raw, JsonOpts);
            return parsed is null
                ? []
                : parsed.Where(r => !string.IsNullOrWhiteSpace(r.PackageId))
                        .Select(r => r.PackageId)
                        .ToList();
        }
        catch (JsonException)
        {
            // A malformed PackageReferences blob grants no entitlement.
            return [];
        }
    }

    private static bool Eq(string? a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches a snapshot package-id <paramref name="candidate"/> against the
    /// <paramref name="requested"/> id. A referenced package id can carry an
    /// Octostache expression (e.g. <c>helper-#{item}</c> on a step inside a
    /// <c>ForEach</c> group) that the orchestrator substitutes at dispatch — the
    /// agent then downloads the resolved id (<c>helper-prod</c>) which this frozen
    /// snapshot cannot reproduce. For a templated id, match on the literal prefix
    /// before the first <c>#{</c> so the resolved download isn't false-denied,
    /// while still denying unrelated package ids. A fully-templated id (empty
    /// prefix) falls back to exact match — primary ids are never substituted, so
    /// the requested value equals the frozen one.
    /// </summary>
    private static bool Matches(string? candidate, string requested)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var exprStart = candidate.IndexOf("#{", StringComparison.Ordinal);
        if (exprStart < 0)
        {
            return Eq(candidate, requested);
        }

        var literalPrefix = candidate[..exprStart];
        return literalPrefix.Length > 0
            ? requested.StartsWith(literalPrefix, StringComparison.OrdinalIgnoreCase)
            : Eq(candidate, requested);
    }
}
