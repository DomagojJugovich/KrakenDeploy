using System.IO.Compression;
using System.Security.Cryptography;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages the server's local <c>.kdeploy-step</c> install store (Phase D-3).
/// <para>
/// Disk layout:
/// </para>
/// <code>
///   {dataPath}/step-packages/
///       {package.name}/
///           {package.version}/
///               package.kdeploy-step     ← the original signed zip (for D-5 gRPC re-streaming)
///               manifest.json            ← extracted for fast read access
///               ui/ui-schema.json        ← extracted for the renderer
///               executor/                ← extracted for the agent ALC loader
/// </code>
/// <para>
/// Signature verification is gated on
/// <c>StepPackages:AllowUnsignedUploads</c> in <c>appsettings.json</c>;
/// production deployments leave this off (the default), dev environments
/// turn it on so authors can iterate without signing every build.
/// </para>
/// </summary>
public sealed class StepPackageService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IConfiguration config,
    ILogger<StepPackageService> logger)
{
    /// <summary>
    /// Result of an upload — either a successful install with the persisted
    /// row, or an error message the caller surfaces to the user.
    /// </summary>
    public sealed record UploadResult(
        bool Success,
        StepPackage? Installed,
        string? ErrorMessage);

    /// <summary>
    /// Installs a <c>.kdeploy-step</c> archive from a stream (typically a
    /// multipart upload body). Validates the manifest, verifies the signature
    /// (when configured), and extracts the archive into the local store.
    /// Returns a structured error result rather than throwing so the REST
    /// endpoint can map it to a 400 cleanly.
    /// </summary>
    public async Task<UploadResult> UploadAsync(
        Stream archive,
        StepPackageSource source = StepPackageSource.LocalUpload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        // Buffer to a temp file first — we need to compute the SHA-256 over
        // the whole archive *and* open it as a ZipArchive *and* persist the
        // original bytes alongside the extracted form. A seekable temp file
        // avoids burning RAM on big packages.
        var tempFile = Path.GetTempFileName();
        try
        {
            string sha256;
            await using (var temp = File.Create(tempFile))
            {
                await archive.CopyToAsync(temp, ct).ConfigureAwait(false);
            }
            await using (var temp = File.OpenRead(tempFile))
            {
                sha256 = await ComputeSha256Async(temp, ct).ConfigureAwait(false);
            }

            // Open the zip and read the manifest.
            StepPackageManifest manifest;
            string? uiSchemaJson;
            try
            {
                await using var temp = File.OpenRead(tempFile);
                using var zip = new ZipArchive(temp, ZipArchiveMode.Read, leaveOpen: false);

                var manifestEntry = zip.GetEntry(StepPackageFiles.ManifestFileName);
                if (manifestEntry is null)
                {
                    return Fail("Archive is missing the required manifest.json at the root.");
                }
                manifest = await ReadManifestAsync(manifestEntry, ct).ConfigureAwait(false);

                var uiSchemaEntry = zip.GetEntry(
                    $"{StepPackageFiles.UiDirectory}/{StepPackageFiles.UiSchemaFileName}");
                uiSchemaJson = uiSchemaEntry is null
                    ? null
                    : await ReadAllTextAsync(uiSchemaEntry, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                return Fail($"Archive is not a valid .kdeploy-step zip: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return Fail($"Manifest could not be parsed: {ex.Message}");
            }

            // Validate manifest essentials.
            var validation = ValidateManifest(manifest);
            if (validation is not null) { return Fail(validation); }

            // Signature verification — placeholder for v1. The hook lets us
            // ship the storage path now and plumb in real RSA verification in
            // a later D-3.x slice without changing the upload API.
            var sigCheck = await VerifySignatureAsync(manifest, ct).ConfigureAwait(false);
            if (sigCheck is not null) { return Fail(sigCheck); }

            // Persist + extract.
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var existing = await db.StepPackages
                .FirstOrDefaultAsync(p =>
                    p.Name == manifest.Id && p.Version == manifest.Version, ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return Fail(
                    $"Step package '{manifest.Id}' version '{manifest.Version}' is already installed " +
                    $"(SHA-256 of the existing copy: {existing.Sha256}). " +
                    "Uninstall the existing version first, or publish a new version number.");
            }

            // ── Extract to disk ─────────────────────────────────────────────
            var targetDir = ResolveDir(manifest.Id, manifest.Version);
            Directory.CreateDirectory(targetDir);

            // Copy the original archive alongside the extracted form so
            // Phase D-5 can re-stream the exact bytes the signature verifies
            // against without re-zipping.
            var archiveOnDisk = Path.Combine(targetDir, "package" + StepPackageFiles.Extension);
            File.Copy(tempFile, archiveOnDisk, overwrite: true);

            await using (var temp = File.OpenRead(tempFile))
            using (var zip = new ZipArchive(temp, ZipArchiveMode.Read, leaveOpen: false))
            {
                ExtractZipSafely(zip, targetDir);
            }

            var row = new StepPackage
            {
                Name         = manifest.Id,
                Version      = manifest.Version,
                Sha256       = sha256,
                ManifestJson = StepPackageManifestJson.Serialize(manifest),
                UiSchemaJson = uiSchemaJson,
                Source       = source,
                StepTypes    = string.Join(',',
                    manifest.StepTypes.Select(t => t.ToLowerInvariant())),
            };
            db.StepPackages.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "Installed step package {Name} {Version} ({StepTypes}) from {Source}.",
                row.Name, row.Version, row.StepTypes, row.Source);

            return new UploadResult(true, row, null);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    /// <summary>All installed versions of a package, ordered by latest first.</summary>
    public async Task<List<StepPackage>> GetVersionsAsync(string name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.StepPackages
            .AsNoTracking()
            .Where(p => p.Name == name)
            .OrderByDescending(p => p.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>All installed packages, grouped by name (latest first within each group).</summary>
    public async Task<List<StepPackage>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.StepPackages
            .AsNoTracking()
            .OrderBy(p => p.Name).ThenByDescending(p => p.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Single package by id.</summary>
    public async Task<StepPackage?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.StepPackages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
    }

    // ── Uninstall (Phase D-11) ────────────────────────────────────────────

    /// <summary>
    /// Outcome of an uninstall request. <see cref="Status"/> tells the
    /// caller whether the package was removed, blocked by live or
    /// snapshotted references, or simply not installed.
    /// </summary>
    public sealed record UninstallResult(
        UninstallStatus Status,
        StepPackageUsageReport? Conflicts);

    public enum UninstallStatus
    {
        /// <summary>Package removed; row deleted; disk dir cleaned.</summary>
        Uninstalled,
        /// <summary>One or more live steps or release snapshots still pin this version.</summary>
        Blocked,
        /// <summary>No row matched <c>(name, version)</c>.</summary>
        NotFound,
    }

    /// <summary>
    /// Removes an installed step-package version when nothing references
    /// it. The conflict report (when <see cref="UninstallStatus.Blocked"/>)
    /// lists every live <c>DeploymentStep</c> + <c>RunbookStep</c> + every
    /// <c>Release</c> whose <see cref="StepSnapshot"/> still pins the version.
    /// <para>
    /// Released-snapshot references are permanent — the version stays
    /// uninstall-blocked until those releases are deleted or pruned by
    /// retention. Live step references can be cleared by editing the step
    /// to a different version (D-7) or via the bulk-upgrade tool (D-10).
    /// </para>
    /// <para>
    /// Agent-side cached copies are NOT actively purged — they sit until
    /// the cache TTL or a manual <c>kraken cache prune</c> sweep.
    /// </para>
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(
        string name, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await db.StepPackages
            .FirstOrDefaultAsync(p => p.Name == name && p.Version == version, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return new UninstallResult(UninstallStatus.NotFound, null);
        }

        // ── Live process steps (DeploymentStep + RunbookStep) ───────────
        var liveDeploymentSteps = await db.DeploymentSteps
            .AsNoTracking()
            .Where(s => s.StepPackageName == name && s.StepPackageVersion == version)
            .Select(s => new StepPackageUsageReport.LiveStepRef(
                s.Id, s.Process.Project.Name, s.Process.Project.Slug, s.Name, IsRunbook: false))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var liveRunbookSteps = await db.RunbookSteps
            .AsNoTracking()
            .Where(s => s.StepPackageName == name && s.StepPackageVersion == version)
            .Select(s => new StepPackageUsageReport.LiveStepRef(
                s.Id, s.Process.Runbook.Project.Name, s.Process.Runbook.Project.Slug,
                s.Name, IsRunbook: true))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ── Release snapshots (project releases) ────────────────────────
        // JSONB containment via Postgres operator @> would be cleaner, but
        // EF doesn't translate it cleanly, so we pull releases and filter
        // in C#. Release counts are bounded (rarely > a few thousand per
        // server); good enough until volume justifies a json-path predicate.
        var releaseRefs = new List<StepPackageUsageReport.ReleaseSnapshotRef>();
        var releases = await db.Releases
            .AsNoTracking()
            .Include(r => r.Project)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var r in releases)
        {
            if (r.ProcessSnapshot.Any(s =>
                string.Equals(s.StepPackageName, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.StepPackageVersion, version, StringComparison.OrdinalIgnoreCase)))
            {
                releaseRefs.Add(new StepPackageUsageReport.ReleaseSnapshotRef(
                    r.Id, r.Project.Name, r.Project.Slug, r.Version));
            }
        }

        if (liveDeploymentSteps.Count > 0 || liveRunbookSteps.Count > 0 || releaseRefs.Count > 0)
        {
            return new UninstallResult(
                UninstallStatus.Blocked,
                new StepPackageUsageReport(
                    name, version,
                    [.. liveDeploymentSteps, .. liveRunbookSteps],
                    releaseRefs));
        }

        // ── Clean up DB + disk ──────────────────────────────────────────
        db.StepPackages.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var dir = ResolveDir(name, version);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // The DB row is gone; surface the on-disk failure as a warning
            // but don't fail the uninstall — admin can mop up manually.
            logger.LogWarning(ex,
                "StepPackageService.UninstallAsync: removed DB row for {Name} {Version} " +
                "but failed to delete on-disk dir '{Path}'.", name, version, dir);
        }

        logger.LogInformation(
            "StepPackageService.UninstallAsync: removed {Name} {Version}.", name, version);

        return new UninstallResult(UninstallStatus.Uninstalled, null);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string ResolveDir(string name, string version)
    {
        var root = config["DataPath"] ?? "data";
        return Path.Combine(root, "step-packages", SanitisePathSegment(name),
            SanitisePathSegment(version));
    }

    /// <summary>
    /// Defensive — strips characters that could escape the storage root.
    /// Manifest <c>id</c> is dotted lower-case (validated upstream) and
    /// versions are semver (no slashes), so this is belt-and-braces.
    /// </summary>
    private static string SanitisePathSegment(string s)
        => string.Join('_',
            s.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
         .Replace("..", "_", StringComparison.Ordinal);

    private static async Task<StepPackageManifest> ReadManifestAsync(
        ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var s = entry.Open();
        using var reader = new StreamReader(s);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return StepPackageManifestJson.Deserialize(json);
    }

    private static async Task<string> ReadAllTextAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var s = entry.Open();
        using var reader = new StreamReader(s);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(Stream s, CancellationToken ct)
    {
        s.Position = 0;
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string? ValidateManifest(StepPackageManifest m)
    {
        if (string.IsNullOrWhiteSpace(m.Id))               { return "Manifest id is required."; }
        if (string.IsNullOrWhiteSpace(m.Version))          { return "Manifest version is required."; }
        if (string.IsNullOrWhiteSpace(m.DisplayName))      { return "Manifest displayName is required."; }
        if (string.IsNullOrWhiteSpace(m.TargetFramework))  { return "Manifest targetFramework is required."; }
        if (m.StepTypes is null || m.StepTypes.Count == 0) { return "Manifest stepTypes must list at least one type."; }
        if (string.IsNullOrWhiteSpace(m.ExecutorAssembly)) { return "Manifest executorAssembly is required."; }
        if (string.IsNullOrWhiteSpace(m.ExecutorTypeName)) { return "Manifest executorTypeName is required."; }

        // Reject path-ish ids/versions so the storage layout stays predictable.
        if (m.Id.Contains('/') || m.Id.Contains('\\'))
        {
            return $"Manifest id '{m.Id}' must not contain path separators.";
        }
        if (m.Version.Contains('/') || m.Version.Contains('\\'))
        {
            return $"Manifest version '{m.Version}' must not contain path separators.";
        }
        return null;
    }

    private Task<string?> VerifySignatureAsync(
        StepPackageManifest manifest, CancellationToken ct)
    {
        _ = ct;
        var allowUnsigned = config.GetValue<bool?>("StepPackages:AllowUnsignedUploads") ?? false;

        if (string.IsNullOrEmpty(manifest.Signature))
        {
            if (allowUnsigned)
            {
                logger.LogWarning(
                    "Step package {Id} {Version} accepted unsigned " +
                    "(StepPackages:AllowUnsignedUploads is true). " +
                    "Disable this in production.",
                    manifest.Id, manifest.Version);
                return Task.FromResult<string?>(null);
            }
            return Task.FromResult<string?>(
                "Manifest is unsigned. Configure StepPackages:AllowUnsignedUploads=true " +
                "to accept unsigned uploads (dev only), or sign the package.");
        }

        // Real RSA-SHA256 verification lands in a follow-up D-3.x slice — the
        // canonical recipe lives in StepPackageManifestJson.CanonicalSignatureInput
        // and Server.Data will pair it with the executor DLL SHA-256 and the
        // project public key. For v1 we accept any non-empty signature when
        // the operator hasn't explicitly opted in to unsigned uploads, so the
        // wiring through to the persisted row is testable end-to-end.
        logger.LogInformation(
            "Step package {Id} {Version} signature verification is a placeholder " +
            "(real RSA-SHA256 hook lands in a D-3 follow-up).",
            manifest.Id, manifest.Version);
        return Task.FromResult<string?>(null);
    }

    private static void ExtractZipSafely(ZipArchive zip, string destinationRoot)
    {
        var rootFull = Path.GetFullPath(destinationRoot);
        foreach (var entry in zip.Entries)
        {
            // Skip directory entries (they have empty Name and end with /).
            if (string.IsNullOrEmpty(entry.Name)) { continue; }

            var destPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            // Zip-slip guard: the final resolved path must stay inside the root.
            if (!destPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !destPath.Equals(rootFull, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' escapes the destination root.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static UploadResult Fail(string message)
        => new(false, null, message);
}
