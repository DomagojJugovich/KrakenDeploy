using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD for <see cref="Team"/>s and their child collections (members,
/// external groups, role assignments). Does not touch built-in teams' core
/// attributes; callers should check <see cref="Team.IsBuiltIn"/> before
/// calling mutating methods.
/// </summary>
public class TeamService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>
    /// All teams visible from the given Space: system-level teams (SpaceId null)
    /// plus Space-scoped teams. Built-in teams appear first.
    /// </summary>
    public async Task<List<Team>> GetAllAsync(Guid? spaceId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Teams
            .Include(t => t.Members)
            .Include(t => t.ExternalGroups).ThenInclude(eg => eg.IdentityProvider)
            .Include(t => t.RoleAssignments).ThenInclude(a => a.Role)
            .Include(t => t.RoleAssignments).ThenInclude(a => a.Scopes)
            .Where(t => t.SpaceId == null || t.SpaceId == spaceId)
            .OrderByDescending(t => t.IsBuiltIn)
            .ThenBy(t => t.SpaceId == null ? 0 : 1)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// WP3-b — teams offerable as manual-intervention approvers: visible from
    /// <paramref name="spaceId"/>, not an "Everyone" team, projected to (Id, Name).
    /// <para>
    /// Deliberately separate from <see cref="GetAllAsync"/>, which <c>Include</c>s
    /// <c>Members</c>, <c>ExternalGroups.IdentityProvider</c> and
    /// <c>RoleAssignments.Role</c>/<c>.Scopes</c> — the whole RBAC graph. That is right
    /// for the teams admin page and wrong for a step-editor dropdown needing two
    /// columns: any user with process-edit rights can open that editor, and the previous
    /// call site materialised every team's full membership and role assignments on their
    /// behalf.
    /// </para>
    /// <para>
    /// "Everyone" teams are excluded at the SOURCE rather than merely refused on save —
    /// offering one invites an operator to pick a "restriction" that restricts nobody,
    /// since every authenticated user belongs to it.
    /// </para>
    /// </summary>
    public async Task<List<ApproverTeamOption>> GetSelectableApproverTeamsAsync(
        Guid? spaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Where(t => (t.SpaceId == null || t.SpaceId == spaceId) && !t.IsEveryoneTeam)
            .OrderBy(t => t.Name)
            .Select(t => new ApproverTeamOption(t.Id, t.Name))
            .ToListAsync(ct);
    }

    public async Task<Team?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Teams
            .Include(t => t.Members)
            .Include(t => t.ExternalGroups).ThenInclude(eg => eg.IdentityProvider)
            .Include(t => t.RoleAssignments).ThenInclude(a => a.Role)
            .Include(t => t.RoleAssignments).ThenInclude(a => a.Scopes)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<Team> CreateAsync(
        string name, string? description, Guid? spaceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var team = new Team
        {
            Name           = name.Trim(),
            Description    = description?.Trim(),
            SpaceId        = spaceId,
            IsBuiltIn      = false,
            IsEveryoneTeam = false,
        };

        db.Teams.Add(team);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return team;
    }

    public async Task<Team?> UpdateAsync(
        Guid id, string name, string? description,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var team = await db.Teams.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (team is null)
        {
            return null;
        }

        team.Name        = name.Trim();
        team.Description = description?.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return team;
    }

    /// <summary>
    /// Deletes a custom (non-built-in) team and all its child rows.
    /// Returns false if the team is not found or is built-in.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var team = await db.Teams
            .Include(t => t.Members)
            .Include(t => t.ExternalGroups)
            .Include(t => t.RoleAssignments)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);

        if (team is null || team.IsBuiltIn)
        {
            return false;
        }

        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Members ───────────────────────────────────────────────────────────────

    public async Task AddMemberAsync(
        Guid teamId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var already = await db.TeamMembers
            .AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct)
            .ConfigureAwait(false);

        if (already)
        {
            return;
        }

        db.TeamMembers.Add(new TeamMember
        {
            TeamId   = teamId,
            UserId   = userId,
            AddedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveMemberAsync(
        Guid teamId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var member = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct)
            .ConfigureAwait(false);

        if (member is null)
        {
            return false;
        }

        db.TeamMembers.Remove(member);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── External groups ───────────────────────────────────────────────────────

    public async Task<TeamExternalGroup> AddExternalGroupAsync(
        Guid teamId, string groupClaim, string? displayName,
        Guid? identityProviderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupClaim);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var eg = new TeamExternalGroup
        {
            TeamId             = teamId,
            GroupClaim         = groupClaim.Trim(),
            DisplayName        = displayName?.Trim(),
            IdentityProviderId = identityProviderId,
        };

        db.TeamExternalGroups.Add(eg);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return eg;
    }

    public async Task<bool> RemoveExternalGroupAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var eg = await db.TeamExternalGroups
            .FindAsync(new object?[] { id }, ct)
            .ConfigureAwait(false);

        if (eg is null)
        {
            return false;
        }

        db.TeamExternalGroups.Remove(eg);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Role assignments ──────────────────────────────────────────────────────

    public async Task<RoleAssignment> AddRoleAssignmentAsync(
        Guid teamId, Guid roleId, Guid? spaceId,
        List<Guid>? projectGroupIds, List<Guid>? projectIds,
        List<Guid>? environmentIds, List<Guid>? tenantIds,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var assignment = new RoleAssignment
        {
            TeamId  = teamId,
            RoleId  = roleId,
            SpaceId = spaceId,
        };

        // One scope row per (dimension, id). No rows for a dimension = "all"
        // (see RoleAssignmentScopeMatcher). Empty/null lists add nothing.
        AddScopeRows(assignment, projectGroupIds, id => new RoleAssignmentScope { ProjectGroupId = id });
        AddScopeRows(assignment, projectIds,      id => new RoleAssignmentScope { ProjectId = id });
        AddScopeRows(assignment, environmentIds,  id => new RoleAssignmentScope { EnvironmentId = id });
        AddScopeRows(assignment, tenantIds,       id => new RoleAssignmentScope { TenantId = id });

        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return assignment;
    }

    private static void AddScopeRows(
        RoleAssignment assignment, List<Guid>? ids, Func<Guid, RoleAssignmentScope> factory)
    {
        if (ids is null)
        {
            return;
        }
        // De-dupe defensively; the unique index would otherwise reject repeats.
        foreach (var id in ids.Distinct())
        {
            assignment.Scopes.Add(factory(id));
        }
    }

    public async Task<bool> RemoveRoleAssignmentAsync(
        Guid assignmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var assignment = await db.RoleAssignments
            .FindAsync(new object?[] { assignmentId }, ct)
            .ConfigureAwait(false);

        if (assignment is null)
        {
            return false;
        }

        db.RoleAssignments.Remove(assignment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// A team as offered in an approver picker — id plus display name, nothing else.
/// WP3-b: a named type rather than a tuple so the Razor dropdown can bind
/// <c>TextProperty</c>/<c>ValueProperty</c> by name.
/// </summary>
public sealed record ApproverTeamOption(Guid Id, string Name);
