using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD for <see cref="Team"/>s and their child collections (members,
/// external groups, role assignments). Does not touch built-in teams' core
/// attributes; callers should check <see cref="Team.IsBuiltIn"/> before
/// calling mutating methods.
/// </summary>
public class TeamService(KrakenDbContext db)
{
    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>
    /// All teams visible from the given Space: system-level teams (SpaceId null)
    /// plus Space-scoped teams. Built-in teams appear first.
    /// </summary>
    public Task<List<Team>> GetAllAsync(Guid? spaceId = null, CancellationToken ct = default)
        => db.Teams
            .Include(t => t.Members)
            .Include(t => t.ExternalGroups).ThenInclude(eg => eg.IdentityProvider)
            .Include(t => t.RoleAssignments).ThenInclude(a => a.Role)
            .Where(t => t.SpaceId == null || t.SpaceId == spaceId)
            .OrderByDescending(t => t.IsBuiltIn)
            .ThenBy(t => t.SpaceId == null ? 0 : 1)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

    public Task<Team?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Teams
            .Include(t => t.Members)
            .Include(t => t.ExternalGroups).ThenInclude(eg => eg.IdentityProvider)
            .Include(t => t.RoleAssignments).ThenInclude(a => a.Role)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<Team> CreateAsync(
        string name, string? description, Guid? spaceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

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
        List<Guid>? environmentIds, List<Guid>? tenantIds, List<Guid>? tenantTagIds,
        CancellationToken ct = default)
    {
        var assignment = new RoleAssignment
        {
            TeamId         = teamId,
            RoleId         = roleId,
            SpaceId        = spaceId,
            ProjectGroupIds = projectGroupIds ?? [],
            ProjectIds      = projectIds      ?? [],
            EnvironmentIds  = environmentIds  ?? [],
            TenantIds       = tenantIds       ?? [],
            TenantTagIds    = tenantTagIds    ?? [],
        };

        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return assignment;
    }

    public async Task<bool> RemoveRoleAssignmentAsync(
        Guid assignmentId, CancellationToken ct = default)
    {
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
