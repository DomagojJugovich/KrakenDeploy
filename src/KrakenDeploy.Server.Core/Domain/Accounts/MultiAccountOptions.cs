namespace KrakenDeploy.Server.Core.Domain.Accounts;

/// <summary>
/// Configuration for the multi-account (SaaS) layer, bound from the
/// <c>MultiAccount</c> configuration section.
/// <para>
/// When <see cref="Enabled"/> is <c>false</c> (the default) the platform runs as a
/// single-instance install exactly as before: one fixed tenant connection string,
/// no subdomain resolution, no control-plane catalog. The account layer only
/// activates when this is set, so the on-prem topology is unchanged.
/// </para>
/// </summary>
public sealed class MultiAccountOptions
{
    public const string SectionName = "MultiAccount";

    /// <summary>Master switch for the SaaS account layer.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Platform base domain under which account subdomains live
    /// (e.g. <c>krakendeploy.com</c>, or <c>localhost</c> for local <c>*.localhost</c>
    /// development). A host equal to this is the apex / control-plane host.
    /// </summary>
    public string BaseDomain { get; set; } = "localhost";

    /// <summary>Seconds to cache a resolved subdomain → account mapping. Default 60.</summary>
    public int CacheSeconds { get; set; } = 60;
}
