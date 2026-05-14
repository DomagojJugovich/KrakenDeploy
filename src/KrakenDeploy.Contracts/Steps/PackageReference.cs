namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// A package a step depends on alongside its primary package. Used for steps
/// that need tools shipped as packages (e.g. a deploy script that needs a
/// PowerShell helper module, or a Bash script that needs <c>jq</c>).
/// <para>
/// Stored as a JSON-encoded array in step <c>Config</c> under the Octopus-
/// compatible key <c>Octopus.Action.Package.PackageReferences</c>. Version
/// is resolved at release-creation time the same way the primary package's
/// version is — "latest" semantics today; channel rules later.
/// </para>
/// </summary>
public sealed record PackageReference
{
    /// <summary>
    /// Friendly name surfaced as <c>Octopus.Action.Package[Name].*</c>
    /// variables and as <c>OCTOPUS_REFERENCED_PACKAGE_&lt;Name&gt;_PATH</c>
    /// in the script's environment. Distinct from <see cref="PackageId"/>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The package identifier in the feed (e.g. <c>MyHelperLib</c>).</summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Pinned version selected at release-creation time. May be null before
    /// resolution (template-author intent) and is always populated by the
    /// time the plan reaches the agent.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// When true, the package is extracted to
    /// <c>extract/refs/&lt;Name&gt;/</c> alongside the primary. When false,
    /// the zip is downloaded but left un-extracted (some steps want the raw
    /// file). Defaults to true.
    /// </summary>
    public bool Extract { get; init; } = true;

    /// <summary>
    /// Octopus's feed-id field — preserved on round-trip so imports + exports
    /// remain compatible. KrakenDeploy currently has a single built-in feed,
    /// so this is informational only.
    /// </summary>
    public string? FeedId { get; init; }
}
