using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;

/// <summary>LLM-shaped release manifest (M11.B).</summary>
public sealed record ReleaseManifestDto(
    Guid Id,
    string ProjectName,
    string ProjectSlug,
    string Version,
    string? ReleaseNotes,
    string? ChannelName,
    DateTimeOffset CreatedUtc,
    int StepCount,
    bool HasVariableSnapshot);

/// <summary>
/// Builds release manifests + history (M11.B). Shared by the
/// <c>get_release_history</c> tool, the
/// <c>kraken://releases/{slug}/{version}</c> resource, and the diagnosis
/// context (the diff builder uses release history to find the last green
/// run).
/// </summary>
public sealed class ReleaseContextBuilder(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>Single release manifest by project slug + version, or null.</summary>
    public async Task<ReleaseManifestDto?> GetAsync(
        string projectSlug, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var release = await db.Releases.AsNoTracking()
            .Include(r => r.Project)
            .Include(r => r.Channel)
            .FirstOrDefaultAsync(r => r.Project.Slug == projectSlug && r.Version == version, ct)
            .ConfigureAwait(false);
        return release is null ? null : ToManifest(release);
    }

    /// <summary>Release history for a project, newest first.</summary>
    public async Task<IReadOnlyList<ReleaseManifestDto>> GetHistoryAsync(
        string projectSlug, int count = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var releases = await db.Releases.AsNoTracking()
            .Include(r => r.Project)
            .Include(r => r.Channel)
            .Where(r => r.Project.Slug == projectSlug)
            .OrderByDescending(r => r.CreatedUtc)
            .Take(Math.Clamp(count, 1, 100))
            .ToListAsync(ct).ConfigureAwait(false);
        return releases.Select(ToManifest).ToList();
    }

    private static ReleaseManifestDto ToManifest(Core.Domain.Releases.Release r)
        => new(
            Id:                  r.Id,
            ProjectName:         r.Project?.Name ?? "",
            ProjectSlug:         r.Project?.Slug ?? "",
            Version:             r.Version,
            ReleaseNotes:        r.ReleaseNotes,
            ChannelName:         r.Channel?.Name,
            CreatedUtc:          r.CreatedUtc,
            StepCount:           r.ProcessSnapshot.Count,
            HasVariableSnapshot: r.VariableSnapshotUpdatedUtc is not null);
}
