namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Parsed view of a Kraken / Octopus IIS step deploying a <strong>web application</strong>
/// (a sub-application beneath an existing IIS site). Created by the Octopus-shape
/// mapper when <c>Octopus.Action.IISWebSite.DeploymentType="webApplication"</c>.
/// <para>
/// Distinct from <see cref="KrakenIisConfig"/> because the field surfaces barely
/// overlap: a web application has no bindings, no rapid-fail policy, no specific
/// recycling — those belong to the parent <see cref="ParentSiteName"/>. The
/// application carries its own <see cref="AppPool"/> and physical path; the parent
/// site is left untouched.
/// </para>
/// </summary>
public sealed record KrakenIisWebApplicationConfig
{
    /// <summary>Required. Existing IIS site to host the web application under.</summary>
    public required string ParentSiteName { get; init; }

    /// <summary>
    /// Required. Virtual path of the sub-application, relative to the parent site
    /// (e.g. <c>/arr</c>). The renderer / generator normalises the leading slash.
    /// </summary>
    public required string VirtualPath { get; init; }

    /// <summary>
    /// Required. Filesystem directory the application's content is served from —
    /// typically the package's extracted directory or a
    /// <c>CustomInstallationDirectory</c>.
    /// </summary>
    public required string PhysicalPath { get; init; }

    /// <summary>
    /// App-pool configuration. Web applications run in their own pool — the
    /// parent site's pool is irrelevant.
    /// </summary>
    public KrakenIisAppPool AppPool { get; init; } = new();
}

/// <summary>
/// Parsed view of a Kraken / Octopus IIS step deploying a <strong>virtual
/// directory</strong> beneath an existing IIS site. Created by the Octopus-shape
/// mapper when <c>Octopus.Action.IISWebSite.DeploymentType="virtualDirectory"</c>.
/// <para>
/// Simpler than a web application: a virtual directory is just a path-to-disk
/// alias. It has no application pool of its own (it inherits the parent app's
/// pool), no authentication settings (inherited from the parent), no bindings.
/// </para>
/// </summary>
public sealed record KrakenIisVirtualDirectoryConfig
{
    /// <summary>Required. Existing IIS site to host the virtual directory under.</summary>
    public required string ParentSiteName { get; init; }

    /// <summary>
    /// Required. Virtual path of the directory, relative to the parent site
    /// (e.g. <c>/static-content</c>). The renderer / generator normalises the
    /// leading slash.
    /// </summary>
    public required string VirtualPath { get; init; }

    /// <summary>Required. Filesystem directory the virtual directory points at.</summary>
    public required string PhysicalPath { get; init; }
}
