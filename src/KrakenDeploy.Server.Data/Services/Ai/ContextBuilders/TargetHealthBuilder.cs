using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;

/// <summary>LLM-shaped target health snapshot (M11.B).</summary>
public sealed record TargetHealthDto(
    Guid Id,
    string Name,
    string Status,
    DateTimeOffset? LastSeenUtc,
    string? MachineName,
    string? OperatingSystem,
    string? AgentVersion,
    IReadOnlyList<string> Roles,
    string TransportMode,
    DateTimeOffset? LastDeploymentUtc,
    string? LastDeploymentStatus);

/// <summary>A slim target row for the <c>query_targets</c> tool.</summary>
public sealed record TargetSummaryDto(
    Guid Id,
    string Name,
    string Status,
    IReadOnlyList<string> Roles,
    DateTimeOffset? LastSeenUtc);

/// <summary>
/// Builds target health snapshots + slim target listings (M11.B). Shared by
/// the <c>get_target_health</c> / <c>query_targets</c> tools, the
/// <c>kraken://targets/{name}/health</c> resource, and the diagnosis
/// context assembler.
/// </summary>
public sealed class TargetHealthBuilder(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>Health snapshot for one target by name, or null when unknown.</summary>
    public async Task<TargetHealthDto?> GetByNameAsync(string targetName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var target = await db.DeploymentTargets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Name == targetName, ct).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        // Most recent deployment that ran against this target (via the
        // assignments join — the single authority for the target set).
        var lastDeployment = await db.Deployments.AsNoTracking()
            .Where(d => d.Targets.Any(a => a.TargetId == target.Id))
            .OrderByDescending(d => d.CreatedUtc)
            .Select(d => new { d.Status, d.CompletedUtc, d.CreatedUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new TargetHealthDto(
            Id:                   target.Id,
            Name:                 target.Name,
            Status:               target.Status.ToString(),
            LastSeenUtc:          target.LastSeenUtc,
            MachineName:          target.MachineName,
            OperatingSystem:      target.OperatingSystem,
            AgentVersion:         target.AgentVersion,
            Roles:                target.Roles.ToArray(),
            TransportMode:        target.TransportMode.ToString(),
            LastDeploymentUtc:    lastDeployment?.CompletedUtc ?? lastDeployment?.CreatedUtc,
            LastDeploymentStatus: lastDeployment?.Status.ToString());
    }

    /// <summary>
    /// Lists targets, optionally filtered by role + environment. Environment
    /// filtering matches targets that have at least one deployment in that
    /// environment (Kraken targets aren't statically bound to environments,
    /// so "used in env X" is the meaningful interpretation).
    /// </summary>
    public async Task<IReadOnlyList<TargetSummaryDto>> QueryAsync(
        string? role = null, string? environmentName = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var q = db.DeploymentTargets.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(role))
        {
            q = q.Where(t => t.Roles.Contains(role));
        }
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            q = q.Where(t => db.Deployments.Any(d =>
                d.Targets.Any(a => a.TargetId == t.Id)
                && d.Environment.Name == environmentName));
        }

        var rows = await q.OrderBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Select(t => new TargetSummaryDto(
                t.Id, t.Name, t.Status.ToString(), t.Roles.ToArray(), t.LastSeenUtc))
            .ToList();
    }
}
