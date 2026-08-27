namespace KrakenDeploy.Server.Core.Domain.Platform;

/// <summary>
/// Installation topology, chosen at install time (<c>Deployment:Topology</c>;
/// kraken-init prompts for it — BG1/T2). Ends <c>MultiAccount:Enabled</c>'s triple
/// duty: tenancy/control-plane concerns key on <see cref="Saas"/>, blue-green
/// concerns key on "not <see cref="OnPrem"/>". A config still carrying the old
/// <c>MultiAccount:Enabled</c> key fails boot/CLI with a named migration message.
/// </summary>
public enum DeploymentTopology
{
    /// <summary>
    /// Single-instance on-prem install (the default): one app process, one
    /// KrakenDb, upgrades via stop → migrate → start. No release registry, no
    /// router, no account layer.
    /// </summary>
    OnPrem = 0,

    /// <summary>
    /// On-prem blue-green (BG1/T1): 3 slots per node + per-node YARP router,
    /// release registry in KrakenDb under the <c>platform</c> schema. Single
    /// tenant — no account layer. Commits the install to additive-only migrations
    /// while more than one release is live (T4); non-additive upgrades use the
    /// stop-the-world runbook.
    /// </summary>
    OnPremBlueGreen = 1,

    /// <summary>
    /// SaaS multi-account: control-plane catalog, DB-per-account, subdomain
    /// resolution, per-account OIDC — plus the blue-green slot scheme with the
    /// registry in the catalog.
    /// </summary>
    Saas = 2,
}

/// <summary>
/// Options bound from the <c>Deployment</c> configuration section.
/// </summary>
public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    /// <summary>Full configuration key of <see cref="Topology"/>.</summary>
    public const string TopologyKey = $"{SectionName}:{nameof(Topology)}";

    public DeploymentTopology Topology { get; set; } = DeploymentTopology.OnPrem;
}
