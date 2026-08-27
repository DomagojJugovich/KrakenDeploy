namespace KrakenDeploy.Server.Core.Domain.Accounts;

/// <summary>
/// Configuration for the multi-account (SaaS) layer, bound from the
/// <c>MultiAccount</c> configuration section.
/// <para>
/// The layer activates when <c>Deployment:Topology</c> is <c>Saas</c> (BG1/T2 —
/// this section used to carry an <c>Enabled</c> master switch; a config still
/// setting it fails boot/CLI with a named migration message). Under the on-prem
/// topologies the platform runs single-tenant: one fixed connection string, no
/// subdomain resolution, no control-plane catalog.
/// </para>
/// </summary>
public sealed class MultiAccountOptions
{
    public const string SectionName = "MultiAccount";

    /// <summary>
    /// Name of the REMOVED master-switch key — kept only so the topology resolver
    /// can refuse configs that still carry <c>MultiAccount:Enabled</c>.
    /// </summary>
    public const string RemovedEnabledKeyName = "Enabled";

    /// <summary>
    /// Platform base domain under which account subdomains live
    /// (e.g. <c>krakendeploy.com</c>, or <c>localhost</c> for local <c>*.localhost</c>
    /// development). A host equal to this is the apex / control-plane host.
    /// </summary>
    public string BaseDomain { get; set; } = "localhost";

    /// <summary>Seconds to cache a resolved subdomain → account mapping. Default 60.</summary>
    public int CacheSeconds { get; set; } = 60;
}
