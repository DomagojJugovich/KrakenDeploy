using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Creates and queries releases.
/// A release locks in a specific version of the project's deployment process
/// with pinned package versions.
/// </summary>
public class ReleaseService(KrakenDbContext db)
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

        // Build the process snapshot, resolving package versions.
        var snapshot = new List<StepSnapshot>(process.Steps.Count);
        foreach (var step in process.Steps)
        {
            // Explicit version wins; fall back to the latest uploaded version.
            var pinned = (packageVersions is not null && packageVersions.TryGetValue(step.Name, out var v))
                ? v
                : await ResolveLatestVersionAsync(step.PackageId, ct).ConfigureAwait(false);

            snapshot.Add(new StepSnapshot
            {
                Name = step.Name,
                StepType = step.StepType,
                PackageId = step.PackageId,
                PackageVersion = pinned,
                TargetRoles = [.. step.TargetRoles],
                Config = new Dictionary<string, string>(step.Config),
                SortOrder = step.SortOrder,
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
        => await db.Releases
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public Task<Release?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Releases
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<string> ResolveLatestVersionAsync(string packageId, CancellationToken ct)
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
}
