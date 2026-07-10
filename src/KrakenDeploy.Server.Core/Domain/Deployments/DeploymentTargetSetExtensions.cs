using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// Read helpers over <see cref="Deployment.Targets"/> — the single authority
/// for a deployment's target set since the transitional
/// <c>deployments.target_id</c> column was dropped (2026-07 schema hardening).
/// All members require the <c>Targets</c> collection (and, where names are
/// involved, its <c>Target</c> navigations) to be loaded; on an unloaded
/// collection they degrade to "no targets", never throw.
/// </summary>
public static class DeploymentTargetSetExtensions
{
    /// <summary>
    /// The deployment's resolved targets, first-assigned first (AddedUtc
    /// carries assignment order; TargetId tie-breaks equal timestamps).
    /// The first element is the canonical target where a single
    /// representative is needed (server-wave machine variables).
    /// </summary>
    public static List<DeploymentTarget> ResolvedTargets(this Deployment d) =>
        d.Targets
            .Where(a => a.Target is not null)
            .OrderBy(a => a.AddedUtc)
            .ThenBy(a => a.TargetId)
            .Select(a => a.Target!)
            .ToList();

    /// <summary>Target names in assignment order (loaded navigations only).</summary>
    public static List<string> TargetNames(this Deployment d) =>
        d.ResolvedTargets().Select(t => t.Name).ToList();

    /// <summary>
    /// One-line label for list surfaces: the target's name for a
    /// single-target deployment, "N targets" for fan-out, "—" when the set
    /// is empty or not loaded.
    /// </summary>
    public static string TargetLabel(this Deployment d)
    {
        var names = d.TargetNames();
        return names.Count switch
        {
            0 => "—",
            1 => names[0],
            _ => $"{names.Count} targets",
        };
    }
}
