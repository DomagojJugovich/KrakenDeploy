using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Creates and queries releases.
/// A release locks in a specific version of the project's deployment process
/// with pinned package versions.
/// </summary>
/// <remarks>
/// <paramref name="stepPackageResolver"/> is optional so tests/fixtures can
/// keep the legacy single-arg construction. In production it's wired through
/// DI; when null, <see cref="StepSnapshot.StepPackageVersion"/> is copied
/// from the underlying <see cref="DeploymentStep"/> as-is (no re-resolution).
/// </remarks>
public class ReleaseService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    StepPackageResolver? stepPackageResolver = null)
{
    // ── Create ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a release by snapshotting the current deployment process and
    /// pinning the supplied package version for each step.
    /// </summary>
    /// <param name="projectId">The project to release.</param>
    /// <param name="version">Semantic version string (must be unique per project).</param>
    /// <param name="packageVersions">
    /// Map from step name → package version to pin.
    /// Steps not present in this map use the latest uploaded version of their package.
    /// </param>
    /// <param name="releaseNotes">Optional human-readable notes.</param>
    public async Task<Release> CreateAsync(
        Guid projectId,
        string version,
        IReadOnlyDictionary<string, string>? packageVersions = null,
        string? releaseNotes = null,
        Guid? channelId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var duplicate = await db.Releases
            .AnyAsync(r => r.ProjectId == projectId && r.Version == version, ct)
            .ConfigureAwait(false);

        if (duplicate)
        {
            throw new InvalidOperationException(
                $"Release '{version}' already exists for this project.");
        }

        // Validate channel if provided.
        if (channelId.HasValue)
        {
            var channel = await db.Channels
                .FirstOrDefaultAsync(c => c.Id == channelId.Value && c.ProjectId == projectId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Channel {channelId} not found for this project.");
        }

        // Load the current process snapshot.
        var process = await db.DeploymentProcesses
            .Include(p => p.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(p => p.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (process is null || process.Steps.Count == 0)
        {
            throw new InvalidOperationException(
                "The project has no deployment process steps. Add at least one step before creating a release.");
        }

        // Build the process snapshot, resolving package versions (primary +
        // referenced packages). Pinning the referenced-package versions here
        // matches what we do for the primary PackageVersion — every deploy of
        // this release will then use the exact same set of helper packages.
        var snapshot = new List<StepSnapshot>(process.Steps.Count);
        foreach (var step in process.Steps)
        {
            // Explicit version wins; fall back to the latest uploaded version
            // (only required when the step actually has a primary package).
            string pinned = "";
            if (!string.IsNullOrWhiteSpace(step.PackageId))
            {
                pinned = (packageVersions is not null && packageVersions.TryGetValue(step.Name, out var v))
                    ? v
                    : await ResolveLatestVersionAsync(db, step.PackageId, ct).ConfigureAwait(false);
            }

            // Copy the source Config, then pin any referenced packages in it.
            var snapshotConfig = new Dictionary<string, string>(step.Config);
            await PinReferencedPackagesAsync(snapshotConfig, db, ct).ConfigureAwait(false);

            // D-6: freeze the step-package pin. If the deployment step
            // didn't have one (older row, or no package installed when the
            // step was added), re-resolve "latest installed" *now* so the
            // release is reproducible. Resolver is optional in test fixtures.
            string? snapshotPackageName    = step.StepPackageName;
            string? snapshotPackageVersion = step.StepPackageVersion;
            if (snapshotPackageVersion is null && stepPackageResolver is not null)
            {
                var pin = await stepPackageResolver
                    .ResolveLatestForStepTypeAsync(step.StepType, ct)
                    .ConfigureAwait(false);
                if (pin is not null)
                {
                    snapshotPackageName    = pin.Name;
                    snapshotPackageVersion = pin.Version;
                }
            }

            snapshot.Add(new StepSnapshot
            {
                Name               = step.Name,
                StepType           = step.StepType,
                PackageId          = step.PackageId,
                PackageVersion     = pinned,
                TargetRoles        = [.. step.TargetRoles],
                Config             = snapshotConfig,
                SortOrder          = step.SortOrder,
                StepPackageName    = snapshotPackageName,
                StepPackageVersion = snapshotPackageVersion,
            });
        }

        var release = new Release
        {
            ProjectId = projectId,
            Version = version,
            ProcessSnapshot = snapshot,
            ReleaseNotes = releaseNotes,
            ChannelId = channelId,
        };

        db.Releases.Add(release);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return release;
    }

    // ── Query ──────────────────────────────────────────────────────────────

    public async Task<List<Release>> GetAllAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Releases
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Release>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Releases
            .Include(r => r.Project)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Release?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Releases
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<string> ResolveLatestVersionAsync(
        KrakenDbContext db, string packageId, CancellationToken ct)
    {
        var latest = await db.Packages
            .Where(p => p.PackageId == packageId)
            .OrderByDescending(p => p.UploadedUtc)
            .Select(p => p.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return latest
            ?? throw new InvalidOperationException(
                $"No package with ID '{packageId}' has been uploaded. " +
                "Upload it first or provide an explicit version.");
    }

    private static readonly System.Text.Json.JsonSerializerOptions PackageRefJsonOpts =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Pins any <see cref="PackageReference"/> entries in
    /// <c>config[Octopus.Action.Package.PackageReferences]</c> that don't
    /// have an explicit <c>Version</c>. Mirrors the strict semantics of
    /// <see cref="ResolveLatestVersionAsync"/>: refs to packages with zero
    /// uploaded versions throw rather than silently fall through.
    /// No-op when the key is missing or empty.
    /// </summary>
    private static async Task PinReferencedPackagesAsync(
        Dictionary<string, string> config,
        KrakenDbContext db,
        CancellationToken ct)
    {
        if (!config.TryGetValue(KrakenScriptConfigKeys.PackageReferences, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        List<PackageReference>? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<List<PackageReference>>(
                raw, PackageRefJsonOpts);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                $"Step has malformed {KrakenScriptConfigKeys.PackageReferences} JSON: {ex.Message}", ex);
        }

        if (parsed is null || parsed.Count == 0)
        {
            return;
        }

        var pinned = new List<PackageReference>(parsed.Count);
        foreach (var r in parsed)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.PackageId))
            {
                continue; // skip malformed rows
            }

            if (!string.IsNullOrWhiteSpace(r.Version))
            {
                pinned.Add(r);
                continue;
            }

            var latest = await ResolveLatestVersionAsync(db, r.PackageId, ct).ConfigureAwait(false);
            pinned.Add(r with { Version = latest });
        }

        config[KrakenScriptConfigKeys.PackageReferences] =
            System.Text.Json.JsonSerializer.Serialize(pinned, PackageRefJsonOpts);
    }
}
