using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

public class TargetService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPermissionEvaluator permissions,
    // Optional (same host-registered / tests-skip pattern as DeploymentService):
    // Retire/Delete record a semantic Target.* audit row so no surface can omit
    // it. Null in tests → only the interceptor's entity-lifecycle row is written.
    IAuditLog? auditLog = null)
{
    // T1-8: the target-id-keyed mutations authorize against the target's Space
    // (targets carry no sub-Space dimension). Resolve the Space filter-free so a
    // foreign-Space id fails closed; System (internal) callers skip the check.
    private async Task EnsureTargetScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, Guid targetId,
        Permission permission, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var spaceId = await db.DeploymentTargets.IgnoreQueryFilters()
            .Where(t => t.Id == targetId)
            .Select(t => (Guid?)t.SpaceId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, permission, new PermissionScope(SpaceId: spaceId), ct).ConfigureAwait(false);
    }

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
    /// roles, risk level, and the direct tenant / environment associations
    /// (collections are replaced to match the given id sets).
    /// </summary>
    public async Task SaveSettingsAsync(
        Guid id,
        string name,
        List<string> roles,
        TargetRiskLevel riskLevel,
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

    /// <summary>
    /// All targets, name-ordered. Includes retired (soft-decommissioned) targets
    /// by default so management / display / scope-editing surfaces still see and
    /// can manage them. Dispatch / matching surfaces (runbook trigger) pass
    /// <paramref name="includeRetired"/> = false to hide retired targets, which
    /// can no longer be deployed to.
    /// </summary>
    public async Task<List<DeploymentTarget>> GetAllAsync(
        bool includeRetired = true, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.DeploymentTargets.AsQueryable();
        if (!includeRetired)
        {
            q = q.Where(t => !t.IsRetired);
        }
        return await q.OrderBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// All NON-retired targets with their environment AND tenant associations
    /// eager-loaded — for the Deploy dialog, which filters selectable targets by
    /// the chosen environment and, for tenanted deploys, by tenant association.
    /// Retired targets are hidden from this matching surface (they can't be
    /// deployed to). Read-only (<c>AsNoTracking</c>); do not feed into
    /// <see cref="UpdateAsync"/> (see <see cref="GetAsync"/>). Two collection
    /// Includes → split queries, otherwise the join would produce a cartesian
    /// result set.
    /// </summary>
    public async Task<List<DeploymentTarget>> GetAllWithEnvironmentsAsync(
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DeploymentTargets
            .AsNoTracking()
            .Where(t => !t.IsRetired)
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

    /// <summary>
    /// Retires (soft-decommission) a target: sets <see cref="DeploymentTarget.IsRetired"/>
    /// (hidden from matching/dispatch, agent rejected at connect), flips the status
    /// to <see cref="TargetStatus.Disabled"/>, and bumps the agent-token version so
    /// any outstanding token is rejected and the live tunnel drops. The row and all
    /// execution history are preserved — retire is the ONLY supported path for a
    /// target that has ever been deployed to (the RESTRICT FKs on
    /// <c>task_target_assignments</c> / <c>task_step_outcomes</c> refuse a hard
    /// delete while history exists). Idempotent. Returns false if the target does
    /// not exist (or is outside the caller's Space).
    /// </summary>
    public async Task<bool> RetireAsync(
        Guid id, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureTargetScopeAsync(db, caller, id, Permission.MachineRetire, ct).ConfigureAwait(false);

        var target = await db.DeploymentTargets.FindAsync([id], ct).ConfigureAwait(false);
        if (target is null)
        {
            return false;
        }

        if (target.IsRetired)
        {
            return true; // no-op
        }

        target.IsRetired = true;
        target.Status = TargetStatus.Disabled;
        // Revoke outstanding agent tokens in the same save so the agent's next
        // connect/call fails the version check (AgentTokenValidator) and the
        // AgentHub retired-target gate rejects it.
        target.AgentTokenVersion += 1;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (auditLog is not null)
        {
            await auditLog.RecordAsync(
                AuditEventType.TargetRetired,
                subjectType: "DeploymentTarget",
                subjectId:   id.ToString(),
                subjectName: target.Name,
                details:     "Target retired; hidden from matching and its agent rejected at connect.",
                ct:          ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Hard-deletes a target. REFUSED (throws <see cref="InvalidOperationException"/>)
    /// while any execution history references it — <c>task_target_assignments</c> and
    /// <c>task_step_outcomes</c> both carry RESTRICT FKs to the target, so history is
    /// never orphaned; retire such a target instead. A history-free target (never
    /// deployed to) deletes cleanly; its <c>target_tenants</c> / <c>target_environments</c>
    /// join rows cascade and its polymorphic <c>tag_applications</c> are cleaned by
    /// the <c>TagApplicationCleanupInterceptor</c> in the same save. Returns false if
    /// the target does not exist (or is outside the caller's Space).
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid id, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureTargetScopeAsync(db, caller, id, Permission.MachineDelete, ct).ConfigureAwait(false);

        var target = await db.DeploymentTargets.FindAsync([id], ct).ConfigureAwait(false);
        if (target is null)
        {
            return false;
        }

        // Execution history pins its targets (RESTRICT). Refuse loudly rather than
        // orphan assignments / step outcomes — retire preserves history, delete does not.
        if (await db.TaskTargetAssignments
            .AnyAsync(a => a.TargetId == id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Target '{target.Name}' has execution history and cannot be deleted. " +
                "Retire it instead — retiring hides it from matching and dispatch while " +
                "preserving its deployment and runbook history.");
        }
        if (await db.TaskStepOutcomes
            .AnyAsync(o => o.TargetId == id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Target '{target.Name}' has execution history and cannot be deleted. " +
                "Retire it instead — retiring hides it from matching and dispatch while " +
                "preserving its deployment and runbook history.");
        }

        db.DeploymentTargets.Remove(target);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (auditLog is not null)
        {
            await auditLog.RecordAsync(
                AuditEventType.TargetDeleted,
                subjectType: "DeploymentTarget",
                subjectId:   id.ToString(),
                subjectName: target.Name,
                details:     "Target hard-deleted (no execution history).",
                ct:          ct).ConfigureAwait(false);
        }
        return true;
    }
}
