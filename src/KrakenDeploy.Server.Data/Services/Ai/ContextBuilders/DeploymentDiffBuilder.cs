using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;

/// <summary>
/// Structured delta of a deployment vs the last GREEN run of the same
/// (project, environment) — the "what changed since it last worked?" view
/// (M11.B). The single most useful signal for diagnosing a regression:
/// release version bump, package version changes per step, variable
/// changes (names only — never values), target-set changes.
/// <para>
/// Shared by the <c>get_deployment_diff</c> tool and the M11.C diagnosis
/// context. When there's no prior green run,
/// <see cref="DeploymentDiffDto.HasBaseline"/> is false and the deltas are
/// empty — a clear "first deployment, nothing to compare" signal.
/// </para>
/// </summary>
public sealed record DeploymentDiffDto(
    Guid DeploymentId,
    bool HasBaseline,
    Guid? BaselineDeploymentId,
    string? FromReleaseVersion,
    string ToReleaseVersion,
    IReadOnlyList<PackageDeltaDto> PackageChanges,
    VariableDeltaDto VariableChanges,
    IReadOnlyList<string> TargetsAdded,
    IReadOnlyList<string> TargetsRemoved);

public sealed record PackageDeltaDto(string StepName, string? FromVersion, string? ToVersion);

/// <summary>Variable-name deltas. Values are deliberately excluded — a
/// changed value could be a secret; the names tell the AI where to look
/// without leaking content.</summary>
public sealed record VariableDeltaDto(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed);

public sealed class DeploymentDiffBuilder(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>
    /// Computes the diff for <paramref name="deploymentId"/> vs the last
    /// Succeeded run of the same project + environment that completed
    /// before it. Returns null when the deployment id is unknown.
    /// </summary>
    public async Task<DeploymentDiffDto?> BuildAsync(Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var current = await db.Deployments.AsNoTracking()
            .Include(d => d.Release)
            .Include(d => d.Targets).ThenInclude(a => a.Target!)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        // Last green run of the same (project, environment) created before
        // this one — the baseline we diff against.
        var baseline = await db.Deployments.AsNoTracking()
            .Include(d => d.Release)
            .Include(d => d.Targets).ThenInclude(a => a.Target!)
            .Where(d => d.Id != current.Id
                     && d.Status == DeploymentStatus.Succeeded
                     && d.EnvironmentId == current.EnvironmentId
                     && d.Release.ProjectId == current.Release.ProjectId
                     && d.CreatedUtc < current.CreatedUtc)
            .OrderByDescending(d => d.CreatedUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (baseline is null)
        {
            return new DeploymentDiffDto(
                DeploymentId:         current.Id,
                HasBaseline:          false,
                BaselineDeploymentId: null,
                FromReleaseVersion:   null,
                ToReleaseVersion:     current.Release.Version,
                PackageChanges:       [],
                VariableChanges:      new VariableDeltaDto([], [], []),
                TargetsAdded:         [],
                TargetsRemoved:       []);
        }

        return new DeploymentDiffDto(
            DeploymentId:         current.Id,
            HasBaseline:          true,
            BaselineDeploymentId: baseline.Id,
            FromReleaseVersion:   baseline.Release.Version,
            ToReleaseVersion:     current.Release.Version,
            PackageChanges:       DiffPackages(baseline.Release.ProcessSnapshot, current.Release.ProcessSnapshot),
            VariableChanges:      DiffVariables(baseline.Release.VariableSnapshot, current.Release.VariableSnapshot),
            TargetsAdded:         TargetNames(current).Except(TargetNames(baseline)).ToList(),
            TargetsRemoved:       TargetNames(baseline).Except(TargetNames(current)).ToList());
    }

    private static List<PackageDeltaDto> DiffPackages(
        IReadOnlyList<StepSnapshot> from, IReadOnlyList<StepSnapshot> to)
    {
        // Key by step name — the stable identifier an operator recognises.
        var fromByName = from
            .Where(s => !string.IsNullOrEmpty(s.PackageId))
            .GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().PackageVersion);
        var toByName = to
            .Where(s => !string.IsNullOrEmpty(s.PackageId))
            .GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().PackageVersion);

        var deltas = new List<PackageDeltaDto>();
        foreach (var (name, toVer) in toByName)
        {
            fromByName.TryGetValue(name, out var fromVer);
            if (!string.Equals(fromVer, toVer, StringComparison.Ordinal))
            {
                deltas.Add(new PackageDeltaDto(name, fromVer, toVer));
            }
        }
        // Packages present in baseline but gone now.
        foreach (var (name, fromVer) in fromByName)
        {
            if (!toByName.ContainsKey(name))
            {
                deltas.Add(new PackageDeltaDto(name, fromVer, null));
            }
        }
        return deltas;
    }

    private static VariableDeltaDto DiffVariables(
        IReadOnlyList<VariableSnapshot> from, IReadOnlyList<VariableSnapshot> to)
    {
        var fromByName = from.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Value);
        var toByName = to.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Value);

        var added = toByName.Keys.Where(k => !fromByName.ContainsKey(k)).OrderBy(k => k).ToList();
        var removed = fromByName.Keys.Where(k => !toByName.ContainsKey(k)).OrderBy(k => k).ToList();
        var changed = toByName
            .Where(kv => fromByName.TryGetValue(kv.Key, out var fv)
                         && !string.Equals(fv, kv.Value, StringComparison.Ordinal))
            .Select(kv => kv.Key).OrderBy(k => k).ToList();

        return new VariableDeltaDto(added, removed, changed);
    }

    private static List<string> TargetNames(Deployment d)
    {
        var names = d.Targets
            .Where(a => a.Target is not null)
            .Select(a => a.Target!.Name)
            .ToList();
        if (names.Count == 0 && d.Target is not null)
        {
            names.Add(d.Target.Name);
        }
        return names;
    }
}
