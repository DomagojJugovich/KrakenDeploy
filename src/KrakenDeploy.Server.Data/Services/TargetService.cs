using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

public class TargetService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>
    /// The target with its direct associations (tenants + environments)
    /// loaded — for the target Settings page. Keep <see cref="GetAsync"/>
    /// association-free so <see cref="UpdateAsync"/>'s detached
    /// <c>db.Update</c> never drags join rows into a foreign context.
    /// </summary>
    public async Task<DeploymentTarget?> GetWithAssociationsAsync(
        Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DeploymentTargets
            .AsNoTracking()
            .Include(t => t.Tenants)
            .Include(t => t.Environments)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Persists the target Settings form in one transaction: display name,
    /// roles, risk level, the F2 parallel-task-execution flag, and the direct
    /// tenant / environment associations (collections are replaced to match the
    /// given id sets).
    /// </summary>
    public async Task SaveSettingsAsync(
        Guid id,
        string name,
        List<string> roles,
        TargetRiskLevel riskLevel,
        bool allowParallelTaskExecution,
        IReadOnlyCollection<Guid> environmentIds,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var target = await db.DeploymentTargets
            .Include(t => t.Tenants)
            .Include(t => t.Environments)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Target {id} not found.");

        target.Name = name.Trim();
        target.Roles = roles;
        target.RiskLevel = riskLevel;
        target.AllowParallelTaskExecution = allowParallelTaskExecution;

        var envs = await db.Environments
            .Where(e => environmentIds.Contains(e.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        target.Environments.Clear();
        foreach (var env in envs)
        {
            target.Environments.Add(env);
        }

        var tenants = await db.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        target.Tenants.Clear();
        foreach (var tenant in tenants)
        {
            target.Tenants.Add(tenant);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Which tenants each target is DIRECTLY associated with (the
    /// "Associated Tenants" relation, not tenant tags): target id → tenant
    /// ids. Targets with no association are absent. Powers tenant-aware
    /// target filtering (e.g. the variable scope editor).
    /// </summary>
    public async Task<Dictionary<Guid, HashSet<Guid>>> GetTenantAssociationMapAsync(
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pairs = await db.DeploymentTargets
            .SelectMany(t => t.Tenants.Select(tn => new { TargetId = t.Id, TenantId = tn.Id }))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return pairs
            .GroupBy(p => p.TargetId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.TenantId).ToHashSet());
    }

    public async Task<List<DeploymentTarget>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentTargets.OrderBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>
    /// All targets with their environment AND tenant associations eager-loaded —
    /// for the Deploy dialog, which filters selectable targets by the chosen
    /// environment and, for tenanted deploys, by tenant association. Read-only
    /// (<c>AsNoTracking</c>); do not feed into <see cref="UpdateAsync"/>
    /// (see <see cref="GetAsync"/>). Two collection Includes → split queries,
    /// otherwise the join would produce a cartesian result set.
    /// </summary>
    public async Task<List<DeploymentTarget>> GetAllWithEnvironmentsAsync(
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DeploymentTargets
            .AsNoTracking()
            .Include(t => t.Environments)
            .Include(t => t.Tenants)
            .AsSplitQuery()
            .OrderBy(t => t.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Distinct roles across every deployment target, sorted. Roles live in a
    /// jsonb list so flattening happens in memory — target counts are small.
    /// </summary>
    public async Task<List<string>> GetAllRolesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var roleLists = await db.DeploymentTargets
            .AsNoTracking()
            .Select(t => t.Roles)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return roleLists
            .SelectMany(r => r)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<DeploymentTarget?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DeploymentTargets.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(DeploymentTarget target, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.DeploymentTargets.Update(target);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A8/T1-12: revokes all outstanding agent bearer tokens for the target by
    /// bumping its <see cref="DeploymentTarget.AgentTokenVersion"/>. The agent's
    /// next connect/call fails the version check (see
    /// <see cref="AgentTokenValidator"/>); the operator must re-enroll it. Atomic
    /// increment (no read-modify-write race). Returns the new version, or
    /// <c>null</c> if the target does not exist (or is outside the caller's Space —
    /// ExecuteUpdate honours the Space query filter).
    /// </summary>
    public async Task<int?> RevokeAgentTokenAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.DeploymentTargets
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.AgentTokenVersion, t => t.AgentTokenVersion + 1),
                ct)
            .ConfigureAwait(false);

        if (rows == 0)
        {
            return null;
        }

        return await db.DeploymentTargets
            .Where(t => t.Id == id)
            .Select(t => (int?)t.AgentTokenVersion)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
