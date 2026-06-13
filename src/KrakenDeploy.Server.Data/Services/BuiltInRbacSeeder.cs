using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Seeds the built-in <see cref="Role"/>s (system-level) and the per-Space
/// built-in <see cref="Team"/>s (Space Managers, Project Deployers,
/// Project Contributors, Project Viewers, Everyone) plus the system-level
/// "Kraken Administrators" team. Idempotent — runs on every server startup
/// and re-applies built-in role permission sets so seeder edits ship with
/// the upgrade.
/// </summary>
public class BuiltInRbacSeeder(
    IDbContextFactory<KrakenDbContext> dbFactory,
    ILogger<BuiltInRbacSeeder> logger)
{
    /// <summary>Stable Guid for the system-level "Kraken Administrators" team.</summary>
    public static readonly Guid KrakenAdministratorsTeamId =
        new("00000000-0000-0000-0002-000000000001");

    /// <summary>Stable Guid for the system-level "Everyone" team.</summary>
    public static readonly Guid EveryoneSystemTeamId =
        new("00000000-0000-0000-0002-000000000002");

    /// <summary>
    /// Seeds the system-level RBAC objects (built-in roles + Kraken
    /// Administrators + system Everyone) and ensures every existing Space
    /// has its per-Space built-in teams + role assignments wired up.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        await SeedRolesAsync(db, ct).ConfigureAwait(false);
        await SeedSystemTeamsAsync(db, ct).ConfigureAwait(false);

        // For each existing Space, seed its per-Space built-in teams.
        // Cross-Space query: Space itself is platform-level (not ISpaceScoped)
        // so the global filter doesn't apply, but Team is also intentionally
        // unfiltered (nullable SpaceId pattern).
        var spaceIds = await db.Spaces
            .Select(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var spaceId in spaceIds)
        {
            await SeedSpaceTeamsAsync(db, spaceId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Seeds the per-Space built-in teams for a single Space. Called on Space
    /// creation in addition to startup-wide <see cref="SeedAsync"/>, so brand-new
    /// Spaces immediately have the standard team inventory.
    /// </summary>
    public async Task SeedSpaceTeamsAsync(Guid spaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await SeedSpaceTeamsAsync(db, spaceId, ct).ConfigureAwait(false);
    }

    private static async Task SeedSpaceTeamsAsync(KrakenDbContext db, Guid spaceId, CancellationToken ct)
    {
        await UpsertSpaceTeamAsync(db, spaceId,
            id: DeterministicSpaceTeamId(spaceId, "space-managers"),
            name: "Space Managers",
            description: "Full administrative control over this Space.",
            roleId: BuiltInRoles.SpaceManagerId,
            isEveryone: false, ct).ConfigureAwait(false);

        await UpsertSpaceTeamAsync(db, spaceId,
            id: DeterministicSpaceTeamId(spaceId, "project-deployers"),
            name: "Project Deployers",
            description: "Creates releases and runs deployments in this Space.",
            roleId: BuiltInRoles.ProjectDeployerId,
            isEveryone: false, ct).ConfigureAwait(false);

        await UpsertSpaceTeamAsync(db, spaceId,
            id: DeterministicSpaceTeamId(spaceId, "project-contributors"),
            name: "Project Contributors",
            description: "Edits projects, processes, and variables in this Space.",
            roleId: BuiltInRoles.ProjectContributorId,
            isEveryone: false, ct).ConfigureAwait(false);

        await UpsertSpaceTeamAsync(db, spaceId,
            id: DeterministicSpaceTeamId(spaceId, "project-viewers"),
            name: "Project Viewers",
            description: "Read-only access to this Space.",
            roleId: BuiltInRoles.ProjectViewerId,
            isEveryone: false, ct).ConfigureAwait(false);

        await UpsertSpaceTeamAsync(db, spaceId,
            id: DeterministicSpaceTeamId(spaceId, "everyone"),
            name: "Everyone",
            description: "Every user with access to this Space. Auto-membership; " +
                         "no explicit team_members rows.",
            roleId: BuiltInRoles.ProjectViewerId,
            isEveryone: true, ct).ConfigureAwait(false);
    }

    // ── Roles ──────────────────────────────────────────────────────────────────

    private async Task SeedRolesAsync(KrakenDbContext db, CancellationToken ct)
    {
        foreach (var def in BuiltInRoles.All)
        {
            var existing = await db.Roles
                .FirstOrDefaultAsync(r => r.Id == def.Id, ct)
                .ConfigureAwait(false);

            if (existing is null)
            {
                db.Roles.Add(new Role
                {
                    Id                 = def.Id,
                    Name               = def.Name,
                    Description        = def.Description,
                    GrantedPermissions = [.. def.Permissions],
                    IsBuiltIn          = true,
                    IsSystemOnly       = def.IsSystemOnly,
                });
                logger.LogInformation("Seeded built-in role '{Name}' ({Count} permissions).",
                    def.Name, def.Permissions.Count);
            }
            else
            {
                // Re-apply the seeded definition so role-permission edits ship
                // with the upgrade.
                existing.Name               = def.Name;
                existing.Description        = def.Description;
                existing.GrantedPermissions = [.. def.Permissions];
                existing.IsSystemOnly       = def.IsSystemOnly;
                existing.IsBuiltIn          = true;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── System teams ──────────────────────────────────────────────────────────

    private async Task SeedSystemTeamsAsync(KrakenDbContext db, CancellationToken ct)
    {
        await UpsertSystemTeamAsync(db,
            id: KrakenAdministratorsTeamId,
            name: "Kraken Administrators",
            description: "Unrestricted system-wide administration. Members " +
                         "implicitly have every permission in every Space.",
            roleId: BuiltInRoles.SystemAdministratorId,
            isEveryone: false, ct).ConfigureAwait(false);

        await UpsertSystemTeamAsync(db,
            id: EveryoneSystemTeamId,
            name: "Everyone",
            description: "Every authenticated user. System-level visibility " +
                         "only; per-Space access is granted via Space teams.",
            roleId: null, // No automatic role — this team exists so admins can
                          // explicitly grant cross-Space permissions to all
                          // authenticated users by adding role assignments.
            isEveryone: true, ct).ConfigureAwait(false);
    }

    private async Task UpsertSystemTeamAsync(
        KrakenDbContext db,
        Guid id, string name, string description, Guid? roleId, bool isEveryone,
        CancellationToken ct)
    {
        var existing = await db.Teams
            .Include(t => t.RoleAssignments)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new Team
            {
                Id             = id,
                SpaceId        = null, // system-level
                Name           = name,
                Description    = description,
                IsBuiltIn      = true,
                IsEveryoneTeam = isEveryone,
            };
            db.Teams.Add(existing);
            logger.LogInformation("Seeded system team '{Name}'.", name);
        }
        else
        {
            existing.Name           = name;
            existing.Description    = description;
            existing.IsBuiltIn      = true;
            existing.IsEveryoneTeam = isEveryone;
            existing.SpaceId        = null;
        }

        if (roleId is not null && !existing.RoleAssignments.Any(a => a.RoleId == roleId))
        {
            db.RoleAssignments.Add(new RoleAssignment
            {
                TeamId  = id,
                RoleId  = roleId.Value,
                SpaceId = null, // system-wide
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Per-Space teams ───────────────────────────────────────────────────────

    private static async Task UpsertSpaceTeamAsync(
        KrakenDbContext db,
        Guid spaceId, Guid id, string name, string description,
        Guid roleId, bool isEveryone, CancellationToken ct)
    {
        var existing = await db.Teams
            .Include(t => t.RoleAssignments)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new Team
            {
                Id             = id,
                SpaceId        = spaceId,
                Name           = name,
                Description    = description,
                IsBuiltIn      = true,
                IsEveryoneTeam = isEveryone,
            };
            db.Teams.Add(existing);
        }
        else
        {
            existing.Name           = name;
            existing.Description    = description;
            existing.IsBuiltIn      = true;
            existing.IsEveryoneTeam = isEveryone;
            existing.SpaceId        = spaceId;
        }

        // Ensure the team has its built-in role assignment (unscoped, so it
        // applies across the whole Space).
        if (!existing.RoleAssignments.Any(a => a.RoleId == roleId && a.SpaceId == spaceId))
        {
            db.RoleAssignments.Add(new RoleAssignment
            {
                TeamId  = id,
                RoleId  = roleId,
                SpaceId = spaceId, // Space-scoped, no further dimensions = whole Space
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stable Guid of the per-Space "Space Managers" built-in team. Exposed so
    /// <see cref="SpaceService.CreateAsync"/> can add the creating user to it
    /// (anti-lockout) without re-deriving the hash convention.
    /// </summary>
    public static Guid SpaceManagersTeamId(Guid spaceId) =>
        DeterministicSpaceTeamId(spaceId, "space-managers");

    /// <summary>
    /// Generates a stable Guid for a per-Space built-in team given the Space ID
    /// and the team's slug. Deterministic so re-seeding doesn't create
    /// duplicates and so reference-by-Guid works across re-seeds.
    /// </summary>
    private static Guid DeterministicSpaceTeamId(Guid spaceId, string teamSlug)
    {
        // SHA-256 the (spaceId, teamSlug) tuple and take the first 16 bytes
        // as a UUID. Stable across processes, no DB round-trip needed.
        var input = $"{spaceId:N}:{teamSlug}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16).ToArray());
    }
}
