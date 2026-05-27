using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;

/// <summary>
/// Builds LLM-shaped deployment summaries + log tails (M11.B). Shared by
/// the MCP tools (<c>list_failed_deployments</c>, <c>get_deployment_log</c>)
/// and the M11.C diagnosis context assembler.
/// </summary>
public sealed class DeploymentContextBuilder(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>
    /// Lists deployments, newest first, optionally filtered by terminal
    /// status, environment name, project slug, and a "since N hours ago"
    /// window. <paramref name="onlyFailed"/> narrows to the failure states
    /// (Failed / SucceededWithWarnings) — the common "what's broken" query.
    /// </summary>
    public async Task<IReadOnlyList<DeploymentSummaryDto>> ListAsync(
        bool onlyFailed = false,
        string? environmentName = null,
        string? projectSlug = null,
        int? sinceHours = null,
        int take = 50,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var q = db.Deployments.AsNoTracking()
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Targets).ThenInclude(a => a.Target!)
            .AsQueryable();

        if (onlyFailed)
        {
            q = q.Where(d => d.Status == DeploymentStatus.Failed
                          || d.Status == DeploymentStatus.SucceededWithWarnings);
        }
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            q = q.Where(d => d.Environment.Name == environmentName);
        }
        if (!string.IsNullOrWhiteSpace(projectSlug))
        {
            q = q.Where(d => d.Release.Project.Slug == projectSlug);
        }
        if (sinceHours is > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-sinceHours.Value);
            q = q.Where(d => d.CreatedUtc >= cutoff);
        }

        var rows = await q
            .OrderByDescending(d => d.CreatedUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(ToSummary).ToList();
    }

    /// <summary>Single deployment summary, or null when the id is unknown.</summary>
    public async Task<DeploymentSummaryDto?> GetAsync(Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var d = await LoadAsync(db, deploymentId, ct).ConfigureAwait(false);
        return d is null ? null : ToSummary(d);
    }

    /// <summary>
    /// Deployment summary + the last <paramref name="tailLines"/> log lines
    /// (tail-of-failure focus). Returns null when the id is unknown.
    /// </summary>
    public async Task<DeploymentLogTailDto?> GetLogTailAsync(
        Guid deploymentId, int tailLines = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var d = await LoadAsync(db, deploymentId, ct).ConfigureAwait(false);
        if (d is null)
        {
            return null;
        }

        var total = await db.DeploymentLogEntries.AsNoTracking()
            .CountAsync(l => l.DeploymentId == deploymentId, ct).ConfigureAwait(false);

        var clamped = Math.Clamp(tailLines, 1, 1000);
        // Pull the last N by sequence-desc, then re-order ascending so the
        // tail reads top-to-bottom like the log itself.
        var tail = await db.DeploymentLogEntries.AsNoTracking()
            .Where(l => l.DeploymentId == deploymentId)
            .OrderByDescending(l => l.Sequence)
            .Take(clamped)
            .ToListAsync(ct).ConfigureAwait(false);
        tail.Reverse();

        var tailDtos = tail
            .Select(l => new DeploymentLogLineDto(l.Sequence, l.Timestamp, l.Level, l.Message))
            .ToList();

        return new DeploymentLogTailDto(ToSummary(d), total, tailDtos);
    }

    private static Task<Deployment?> LoadAsync(
        KrakenDbContext db, Guid deploymentId, CancellationToken ct)
        => db.Deployments.AsNoTracking()
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Targets).ThenInclude(a => a.Target!)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);

    private static DeploymentSummaryDto ToSummary(Deployment d)
    {
        var targetNames = d.Targets
            .Where(a => a.Target is not null)
            .Select(a => a.Target!.Name)
            .ToList();
        // Fall back to the legacy single-target nav for joinless rows.
        if (targetNames.Count == 0 && d.Target is not null)
        {
            targetNames.Add(d.Target.Name);
        }

        return new DeploymentSummaryDto(
            Id:              d.Id,
            ProjectName:     d.Release?.Project?.Name ?? "",
            ProjectSlug:     d.Release?.Project?.Slug ?? "",
            ReleaseVersion:  d.Release?.Version ?? "",
            EnvironmentName: d.Environment?.Name ?? "",
            TargetNames:     targetNames,
            Status:          d.Status,
            StartedUtc:      d.StartedUtc,
            CompletedUtc:    d.CompletedUtc);
    }
}
