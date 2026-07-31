using System.IO.Compression;
using System.Security.Cryptography;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Processes;
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
            string? changelogMarkdown;
            var perTypeSchemas = new Dictionary<string, string>(StringComparer.Ordinal);
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

                // SC2: per-step-type schemas at ui/schemas/{typeId}.json.
                // Validated hard at upload — a stray file for an undeclared
                // type or an unparseable schema is a broken package, and the
                // server is the last gate before it reaches the editor.
                var declaredTypes = manifest.StepTypeIds
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t.Length > 0)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith(StepPackageFiles.UiSchemasDirectory + "/",
                            StringComparison.OrdinalIgnoreCase)
                        || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.Length > 1024 * 1024)
                    {
                        return Fail(
                            $"Schema file '{name}' exceeds 1 MB — refusing the archive.");
                    }

                    var typeId = Path.GetFileNameWithoutExtension(entry.Name).ToLowerInvariant();
                    if (!declaredTypes.Contains(typeId))
                    {
                        return Fail(
                            $"Schema file '{name}' has no matching stepTypes entry " +
                            $"'{typeId}' in the manifest.");
                    }

                    var schemaJson = await ReadAllTextAsync(entry, ct).ConfigureAwait(false);
                    try
                    {
                        _ = StepUiSchemaJson.Deserialize(schemaJson);
                    }
                    catch (Exception ex) when (
                        ex is System.Text.Json.JsonException or InvalidOperationException)
                    {
                        return Fail($"Schema file '{name}' is not a valid step UI schema: {ex.Message}");
                    }

                    perTypeSchemas[typeId] = schemaJson;
                }

                // CHANGELOG.md at the zip root is optional (Phase D-12.4).
                // The renderer treats null vs empty differently — empty means
                // "package shipped an explicit zero-changes note", null means
                // "no changelog file at all". Cap at 256 KB so a malicious
                // package can't blow up the DB row; real changelogs are
                // single-digit KB.
                var changelogEntry = zip.GetEntry(StepPackageFiles.ChangelogFileName);
                changelogMarkdown = changelogEntry is null
                    ? null
                    : await ReadTextWithCapAsync(changelogEntry, maxBytes: 256 * 1024, ct)
                        .ConfigureAwait(false);
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

            // Signature verification (Phase D-12 — real RSA-SHA256). The
            // verifier needs the executor DLL bytes from inside the zip to
            // compute the canonical signature input; pass the tempFile so
            // VerifySignatureAsync can open its own zip view.
            var sigCheck = await VerifySignatureAsync(manifest, tempFile, ct).ConfigureAwait(false);
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
                Name              = manifest.Id,
                Version           = manifest.Version,
                Sha256            = sha256,
                ManifestJson      = StepPackageManifestJson.Serialize(manifest),
                UiSchemaJson      = uiSchemaJson,
                ChangelogMarkdown = changelogMarkdown,
                Source            = source,
                // Trim each claim: a padded entry (e.g. manifest authored as
                // "A, B") would otherwise be stored verbatim and never match
                // StepPackageResolver's ",{type}," sentinel lookup.
                StepTypes         = string.Join(',',
                    manifest.StepTypeIds
                        .Select(t => t.Trim().ToLowerInvariant())
                        .Where(t => t.Length > 0)),
            };
            db.StepPackages.Add(row);

            // SC2: persist per-type schemas alongside the package row. A
            // claimed type without a per-type file falls back to the legacy
            // single ui-schema.json (one schema served the whole package
            // pre-SC1); a type with neither simply has no schema row.
            foreach (var typeId in row.StepTypes.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var schemaJson = perTypeSchemas.TryGetValue(typeId, out var perType)
                    ? perType
                    : uiSchemaJson;
                if (schemaJson is null) { continue; }

                db.StepPackageSchemas.Add(new StepPackageSchema
                {
                    StepPackageId = row.Id,
                    StepType      = typeId,
                    SchemaJson    = schemaJson,
                });
            }

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

        // ── Live process steps (unified process_steps, both owner kinds) ─
        var liveStepRows = await db.ProcessSteps
            .IgnoreQueryFilters() // step packages are platform-wide; usage scans span all Spaces
            .AsNoTracking()
            .Where(s => s.StepPackageName == name && s.StepPackageVersion == version)
            .Select(s => new { s.Id, s.Name, s.Process.OwnerKind, s.Process.OwnerId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var resolveLiveProject = await BuildProjectResolverAsync(
            db, liveStepRows.Select(r => (r.OwnerKind, r.OwnerId)), ct).ConfigureAwait(false);
        var liveSteps = liveStepRows
            .Select(r =>
            {
                var (pName, pSlug) = resolveLiveProject(r.OwnerKind, r.OwnerId);
                return new StepPackageUsageReport.LiveStepRef(
                    r.Id, pName, pSlug, r.Name, r.OwnerKind == ProcessOwnerKind.Runbook);
            })
            .ToList();

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

        if (liveSteps.Count > 0 || releaseRefs.Count > 0)
        {
            return new UninstallResult(
                UninstallStatus.Blocked,
                new StepPackageUsageReport(name, version, liveSteps, releaseRefs));
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

    // ── Bulk usage + upgrade (Phase D-10) ──────────────────────────────────

    /// <summary>
    /// Returns every live step (deployment process + runbook process)
    /// pinned to any version of <paramref name="packageName"/>, grouped by
    /// the pinned version. Released snapshots are excluded — they're
    /// permanent by contract and the bulk-upgrade tool deliberately does
    /// not touch them.
    /// </summary>
    public async Task<StepPackageUsage> GetUsageAsync(
        string packageName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rawRows = await db.ProcessSteps
            .IgnoreQueryFilters() // step packages are platform-wide; usage/upgrade scans span all Spaces
            .AsNoTracking()
            .Where(s => s.StepPackageName == packageName && s.StepPackageVersion != null)
            .Select(s => new
            {
                s.Id, s.Name, s.StepType,
                Version = s.StepPackageVersion!,
                s.Process.OwnerKind, s.Process.OwnerId,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var resolveProject = await BuildProjectResolverAsync(
            db, rawRows.Select(r => (r.OwnerKind, r.OwnerId)), ct).ConfigureAwait(false);

        var groups = rawRows
            .Select(r =>
            {
                var (pName, pSlug) = resolveProject(r.OwnerKind, r.OwnerId);
                return (r.Version, Row: new StepPackageUsage.UsageRow(
                    r.Id, pName, pSlug, r.Name, r.StepType,
                    r.OwnerKind == ProcessOwnerKind.Runbook));
            })
            .GroupBy(x => x.Version)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new StepPackageUsage.VersionGroup(
                g.Key,
                [.. g.Select(x => x.Row).OrderBy(r => r.ProjectName).ThenBy(r => r.StepName)]))
            .ToList();

        return new StepPackageUsage(packageName, groups);
    }

    /// <summary>
    /// Bumps the <c>(StepPackageName, StepPackageVersion)</c> pin on a
    /// batch of live steps to a target version (Phase D-10 bulk upgrade).
    /// <para>
    /// Rules:
    /// </para>
    /// <list type="bullet">
    ///   <item>Target version must exist in the catalog (an installed
    ///   <see cref="StepPackage"/> row at <c>(packageName, targetVersion)</c>).
    ///   Throws when it doesn't.</item>
    ///   <item>Step IDs that are already on the target version count as
    ///   <c>Skipped</c> with reason <c>"already-target"</c>.</item>
    ///   <item>Step IDs that don't exist (race with a delete) count as
    ///   <c>Skipped</c> with reason <c>"not-found"</c>.</item>
    ///   <item>Released snapshots (<c>StepSnapshot</c>) are never touched.</item>
    /// </list>
    /// </summary>
    public async Task<BulkUpgradeResult> BulkUpgradeAsync(
        string packageName,
        string targetVersion,
        IReadOnlyList<Guid> deploymentStepIds,
        IReadOnlyList<Guid> runbookStepIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var targetExists = await db.StepPackages
            .AnyAsync(p => p.Name == packageName && p.Version == targetVersion, ct)
            .ConfigureAwait(false);
        if (!targetExists)
        {
            throw new InvalidOperationException(
                $"Target version '{targetVersion}' of step package '{packageName}' " +
                "is not installed. Install it first or pick a different target.");
        }

        var skipped = new List<BulkUpgradeResult.SkippedRow>();
        var touched = 0;

        // Deployment + runbook steps are now one table (process_steps); the two id
        // lists are kept in the API surface only so the UI can present them
        // separately. Bump both in a single scan.
        var allIds = deploymentStepIds.Concat(runbookStepIds).Distinct().ToList();
        if (allIds.Count > 0)
        {
            var rows = await db.ProcessSteps
                .IgnoreQueryFilters() // step packages are platform-wide; upgrade scans span all Spaces
                .Where(s => allIds.Contains(s.Id) && s.StepPackageName == packageName)
                .ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);

            foreach (var id in allIds)
            {
                if (!rows.TryGetValue(id, out var row))
                {
                    skipped.Add(new BulkUpgradeResult.SkippedRow(id, "not-found"));
                    continue;
                }
                if (string.Equals(row.StepPackageVersion, targetVersion, StringComparison.Ordinal))
                {
                    skipped.Add(new BulkUpgradeResult.SkippedRow(id, "already-target"));
                    continue;
                }
                row.StepPackageVersion = targetVersion;
                touched++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "StepPackageService.BulkUpgradeAsync: {Name} → {Version}, touched={Touched}, skipped={Skipped}.",
            packageName, targetVersion, touched, skipped.Count);

        return new BulkUpgradeResult(packageName, targetVersion, touched, skipped);
    }

    /// <summary>
    /// Full path to the stored <c>.kdeploy-step</c> archive for
    /// (<paramref name="name"/>, <paramref name="version"/>), or <c>null</c> if
    /// not installed on disk. Used by the offline bundle generator to embed
    /// step-handler archives so the offline runner loads them without server
    /// connectivity.
    /// </summary>
    public string? TryGetArchivePath(string name, string version)
    {
        var path = Path.Combine(ResolveDir(name, version), "package" + StepPackageFiles.Extension);
        return File.Exists(path) ? path : null;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a resolver mapping a process owner (kind + id) to its owning
    /// project's (name, slug). Runbook-owned processes resolve through the
    /// runbook's ProjectId. Platform-wide (IgnoreQueryFilters) so usage /
    /// uninstall scans see every Space's steps.
    /// </summary>
    private static async Task<Func<ProcessOwnerKind, Guid, (string Name, string Slug)>>
        BuildProjectResolverAsync(
            KrakenDbContext db,
            IEnumerable<(ProcessOwnerKind Kind, Guid OwnerId)> owners,
            CancellationToken ct)
    {
        var ownerList = owners.ToList();

        var runbookIds = ownerList
            .Where(o => o.Kind == ProcessOwnerKind.Runbook)
            .Select(o => o.OwnerId).Distinct().ToList();
        var runbookToProject = runbookIds.Count == 0
            ? []
            : await db.Runbooks.IgnoreQueryFilters().AsNoTracking()
                .Where(r => runbookIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.ProjectId, ct).ConfigureAwait(false);

        Guid ProjectIdOf(ProcessOwnerKind kind, Guid ownerId) =>
            kind == ProcessOwnerKind.Project
                ? ownerId
                : runbookToProject.GetValueOrDefault(ownerId);

        var projectIds = ownerList
            .Select(o => ProjectIdOf(o.Kind, o.OwnerId))
            .Where(id => id != Guid.Empty).Distinct().ToList();
        var projRows = projectIds.Count == 0
            ? []
            : await db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => projectIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Slug })
                .ToListAsync(ct).ConfigureAwait(false);
        var projects = projRows.ToDictionary(p => p.Id, p => (p.Name, p.Slug));

        return (kind, ownerId) =>
            projects.TryGetValue(ProjectIdOf(kind, ownerId), out var proj)
                ? proj
                : ("(unknown)", "unknown");
    }

    private string ResolveDir(string name, string version)
    {
        var root = config["Server:DataPath"] ?? "data";
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

    /// <summary>
    /// Reads a zip entry's text content with an upper-bound on bytes —
    /// truncates with a trailing marker if the entry exceeds the cap.
    /// Used for CHANGELOG.md (256 KB cap) so a malicious or accidentally
    /// huge file can't blow up the DB row that lives in <c>varchar(max)</c>.
    /// </summary>
    private static async Task<string> ReadTextWithCapAsync(
        ZipArchiveEntry entry, int maxBytes, CancellationToken ct)
    {
        await using var s = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await s.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                var spare = maxBytes - (int)ms.Length;
                if (spare > 0) { ms.Write(buffer, 0, spare); }
                ms.Write(System.Text.Encoding.UTF8.GetBytes(
                    $"\n\n_…truncated at {maxBytes:N0} bytes._\n"));
                break;
            }
            ms.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
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
        if (m.StepTypes.Any(t => string.IsNullOrWhiteSpace(t.Id)))
        {
            return "Manifest stepTypes entries must each carry a non-empty id.";
        }
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

    /// <summary>
    /// Verifies the step-package signature (Phase D-12). Returns <c>null</c>
    /// when the manifest passes (or when the dev-mode allowlist is enabled);
    /// otherwise returns the user-facing failure reason.
    /// <para>
    /// Recipe: extract the executor DLL bytes from inside the zip, compute
    /// SHA-256, build the canonical signature input via
    /// <see cref="StepPackageManifestJson.CanonicalSignatureInput"/>, verify
    /// against the trusted public key configured under
    /// <c>StepPackages:TrustedPublicKey</c> (either inline PEM text or a path
    /// to a <c>.pem</c> file).
    /// </para>
    /// </summary>
    private async Task<string?> VerifySignatureAsync(
        StepPackageManifest manifest, string archivePath, CancellationToken ct)
    {
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
                return null;
            }
            return
                "Manifest is unsigned. Configure StepPackages:AllowUnsignedUploads=true " +
                "to accept unsigned uploads (dev only), or sign the package.";
        }

        // The dev-build sentinel — explicit opt-in only.
        if (string.Equals(manifest.Signature, "unsigned-dev-build", StringComparison.Ordinal))
        {
            if (allowUnsigned)
            {
                logger.LogWarning(
                    "Step package {Id} {Version} accepted with the 'unsigned-dev-build' " +
                    "sentinel signature (StepPackages:AllowUnsignedUploads is true).",
                    manifest.Id, manifest.Version);
                return null;
            }
            return
                "Manifest carries the 'unsigned-dev-build' sentinel signature. " +
                "Set StepPackages:AllowUnsignedUploads=true (dev only) to accept it, " +
                "or sign the package with a trusted key.";
        }

        // Real signature → need a trusted public key configured.
        var trustedKey = await LoadTrustedPublicKeyAsync(ct).ConfigureAwait(false);
        if (trustedKey is null)
        {
            return
                "Manifest is signed but no trusted public key is configured on this server. " +
                "Set StepPackages:TrustedPublicKey to the PEM-encoded RSA public key (or a " +
                "path to a .pem file) of the project that signs your packages.";
        }

        // Extract the executor DLL bytes from the zip so we can compute its SHA-256.
        try
        {
            await using var fs = File.OpenRead(archivePath);
            using var zip      = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            var executorEntry  = zip.GetEntry(
                $"{StepPackageFiles.ExecutorDirectory}/{manifest.ExecutorAssembly}");

            if (executorEntry is null)
            {
                return
                    $"Manifest's executorAssembly '{manifest.ExecutorAssembly}' is not present " +
                    $"under '{StepPackageFiles.ExecutorDirectory}/' inside the archive.";
            }

            // Stage the DLL to a temp file because the signer takes a path; the
            // canonical recipe is "manifest-without-signature ++ sha256(executor.dll)"
            // and we want one code path used by both server + agent + CLI.
            var tempDll = Path.Combine(Path.GetTempPath(),
                $"kraken-verify-{Guid.NewGuid():N}.dll");
            try
            {
                await using (var src  = executorEntry.Open())
                await using (var dest = File.Create(tempDll))
                {
                    await src.CopyToAsync(dest, ct).ConfigureAwait(false);
                }

                var verify = StepPackageSigner.Verify(manifest, tempDll, trustedKey);
                if (!verify.IsValid)
                {
                    return $"Signature verification failed: {verify.Reason}";
                }
            }
            finally
            {
                try { File.Delete(tempDll); } catch { /* best effort */ }
            }
        }
        finally
        {
            trustedKey.Dispose();
        }

        logger.LogInformation(
            "Step package {Id} {Version} signature verified against the trusted public key.",
            manifest.Id, manifest.Version);
        return null;
    }

    /// <summary>
    /// Loads the configured trusted public key. Accepts either an inline PEM
    /// block (multi-line) or a path to a <c>.pem</c> file. Returns null when
    /// nothing is configured.
    /// </summary>
    private async Task<RSA?> LoadTrustedPublicKeyAsync(CancellationToken ct)
    {
        var raw = config["StepPackages:TrustedPublicKey"];
        if (string.IsNullOrWhiteSpace(raw)) { return null; }

        string pem;
        if (raw.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            pem = raw;
        }
        else if (File.Exists(raw))
        {
            pem = await File.ReadAllTextAsync(raw, ct).ConfigureAwait(false);
        }
        else
        {
            logger.LogWarning(
                "StepPackages:TrustedPublicKey is set but is neither inline PEM " +
                "(must contain BEGIN marker) nor a path to an existing file: '{Raw}'.",
                raw);
            return null;
        }

        try
        {
            return StepPackageSigner.ImportPublicKeyFromPem(pem);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to load StepPackages:TrustedPublicKey. Check the PEM format " +
                "(SubjectPublicKeyInfo or RSA public key).");
            return null;
        }
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
