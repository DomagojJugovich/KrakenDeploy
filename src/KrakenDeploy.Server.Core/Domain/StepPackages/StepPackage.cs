using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.StepPackages;

/// <summary>
/// An installed <c>.kdeploy-step</c> package (Phase D). One row per
/// (<see cref="Name"/>, <see cref="Version"/>) tuple — multiple versions of
/// the same package coexist on disk so a process pinned to an older version
/// keeps deploying while authors prepare for the upgrade.
/// <para>
/// Packages live system-wide (not per-Space) so admins can manage them
/// centrally on a Kraken instance — hence no <c>ISpaceScoped</c>. The
/// platform's <c>Permission.StepPackageManage</c> gates uploads + uninstalls.
/// </para>
/// </summary>
public class StepPackage : AuditableEntity
{
    /// <summary>
    /// Stable manifest id (e.g. <c>kraken.iis</c>). Matches the schema
    /// root id; the renderer keys schemas by this value too. Indexed
    /// (composite-unique with <see cref="Version"/>).
    /// </summary>
    public required string Name { get; set; }

    /// <summary>Semver string (e.g. <c>1.2.0</c> or <c>2.0.0-preview.4</c>).</summary>
    public required string Version { get; set; }

    /// <summary>
    /// SHA-256 of the <c>.kdeploy-step</c> archive, lower-case hex. Used to
    /// detect tampering on the agent side when the package is fetched from
    /// the local store and to prevent re-uploading a corrupted variant on top
    /// of a known-good version.
    /// </summary>
    public required string Sha256 { get; set; }

    /// <summary>
    /// The parsed manifest as <c>jsonb</c>. Server-side surfaces (catalog,
    /// version picker) read fields directly without re-loading the on-disk
    /// JSON.
    /// </summary>
    public required string ManifestJson { get; set; }

    /// <summary>
    /// Optional release notes for this version, taken from the
    /// <c>CHANGELOG.md</c> file at the zip root (Phase D-12.4). Surfaced
    /// in the "Update available" dialog when a process step is pinned to
    /// an older version, and in the catalog UI when browsing available
    /// versions. Plain Markdown text — the renderer (Markdig) handles
    /// it client-side. <c>null</c> when the package didn't ship a
    /// changelog file.
    /// </summary>
    public string? ChangelogMarkdown { get; set; }

    /// <summary>
    /// Where the install came from. Drives the catalog UI's "installed"
    /// badge and informs uninstall confirmations.
    /// </summary>
    public required StepPackageSource Source { get; set; } = StepPackageSource.LocalUpload;

    /// <summary>
    /// Comma-separated step-type ids from <c>manifest.stepTypes</c> — denormalised
    /// for cheap lookup by step type without re-parsing the manifest JSON.
    /// Lower-cased for case-insensitive comparison.
    /// </summary>
    public required string StepTypes { get; set; }
}

/// <summary>How the package landed in the server's local store.</summary>
public enum StepPackageSource
{
    /// <summary>Admin uploaded the .kdeploy-step zip directly.</summary>
    LocalUpload = 0,

    /// <summary>Pulled from the GitHub-hosted catalog (Phase D-9).</summary>
    CatalogPull = 1,

    /// <summary>Seeded by the fresh-install bundle (Phase D-8 built-ins).</summary>
    Preinstalled = 2,
}

/// <summary><see cref="StepPackageSource"/> helpers.</summary>
public static class StepPackageSourceExtensions
{
    /// <summary>
    /// Whether packages of this source own the step types they claim — i.e.
    /// which sources are trusted to define a type. Built-ins (Preinstalled)
    /// and the official GitHub catalog (CatalogPull) are trusted; an admin
    /// <see cref="StepPackageSource.LocalUpload"/> is not, so it cannot claim
    /// (hijack) a type a trusted source already serves. The single source of
    /// truth for both the upload-time reserved-type guard
    /// (<c>StepPackageService.UploadAsync</c>) and the registry ownership pick
    /// (<c>StepTypeRegistry.RebuildAsync</c>), so the two never drift.
    /// </summary>
    public static bool OwnsClaimedTypes(this StepPackageSource source)
        => source is StepPackageSource.Preinstalled or StepPackageSource.CatalogPull;
}
