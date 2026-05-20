using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.StepPackages;

/// <summary>
/// A step package discovered on the public KrakenDeploy/StepPackages
/// GitHub repo (Phase D-9). Cached server-side by
/// <c>StepPackageCatalogService</c>'s hourly poll so the
/// <c>/step-packages</c> catalog tab doesn't have to hit GitHub on every
/// page load.
/// <para>
/// Each catalog entry maps one-to-one with a published GitHub Release
/// asset. The poll reads the release notes for an embedded
/// <c>manifest.json</c> fenced block (cheap, no big download) and persists
/// the metadata. Installing one downloads the <c>.kdeploy-step</c> asset,
/// verifies its SHA-256 + signature, and calls into <c>StepPackageService.UploadAsync</c>
/// with <see cref="StepPackageSource.CatalogPull"/>.
/// </para>
/// <para>
/// Lives platform-wide — not <c>ISpaceScoped</c> — same as the underlying
/// <see cref="StepPackage"/> install rows. The catalog tab is gated by
/// <c>Permission.StepPackageView</c>; one-click install needs
/// <c>Permission.StepPackageManage</c>.
/// </para>
/// </summary>
public class StepPackageCatalogEntry : Entity
{
    /// <summary>Step-package id from the manifest (e.g. <c>kraken.iis</c>).</summary>
    public required string Name { get; set; }

    /// <summary>Semver version from the manifest (e.g. <c>1.0.0</c>).</summary>
    public required string Version { get; set; }

    /// <summary>
    /// Direct download URL for the <c>.kdeploy-step</c> asset on the GitHub
    /// release. Used by <c>StepPackageCatalogService.InstallAsync</c>.
    /// </summary>
    public required string DownloadUrl { get; set; }

    /// <summary>
    /// SHA-256 of the archive bytes the publisher declared in the release
    /// notes (or computed from the asset on first sync). The installer
    /// re-computes during download and refuses the install on mismatch.
    /// </summary>
    public required string Sha256 { get; set; }

    /// <summary>The full manifest JSON, exactly as it appears in the release.</summary>
    public required string ManifestJson { get; set; }

    /// <summary>
    /// Optional Markdown changelog the publisher embedded in the release
    /// notes. Surfaced in the catalog tab + the update-available dialog.
    /// </summary>
    public string? Changelog { get; set; }

    /// <summary>When the GitHub Release was published.</summary>
    public DateTimeOffset PublishedUtc { get; set; }

    /// <summary>HTML URL of the GitHub Release page (for "View release" links).</summary>
    public required string ReleaseHtmlUrl { get; set; }

    /// <summary>When this row was last touched by a catalog refresh.</summary>
    public DateTimeOffset LastSyncedUtc { get; set; }
}
