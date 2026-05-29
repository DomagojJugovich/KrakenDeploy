namespace KrakenDeploy.Server.Core.Domain.Targets;

/// <summary>
/// Operator-assigned risk classification for a <see cref="DeploymentTarget"/>
/// (M11.E.11). This is NOT inferable: targets carry no environment association,
/// and environment names are free-text — so risk must be set explicitly.
/// <para>
/// Drives the ad-hoc agent-action approval policy. A session's effective risk is
/// the <em>maximum</em> level across its frozen target set (one Production box
/// makes the whole session Production-risk), evaluated at each approval against
/// targets' current classifications. Production triggers the louder approval
/// banner and — when a Space enables it — the two-person rule.
/// </para>
/// <para>
/// Defaults to <see cref="Production"/> (fail-safe): an unclassified or
/// since-deleted target is treated as highest-risk until an operator explicitly
/// downgrades it.
/// </para>
/// </summary>
public enum TargetRiskLevel
{
    /// <summary>Lowest risk — development/scratch machines.</summary>
    Development = 0,

    /// <summary>Pre-production / staging / UAT.</summary>
    Staging = 1,

    /// <summary>Production. Highest risk; the fail-safe default.</summary>
    Production = 2,
}
