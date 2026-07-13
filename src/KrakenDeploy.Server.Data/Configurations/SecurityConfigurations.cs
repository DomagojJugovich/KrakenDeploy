using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Data.Conventions;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

// All the M10 RBAC entities live here together because they form a single
// tightly-coupled cluster (Role ←→ RoleAssignment ←→ Team ←→ Members ←→ User).
// Splitting them across files would just spread the relationship configuration
// over multiple call sites with no maintainability gain.

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => x.Name).IsUnique();

        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.IsBuiltIn).IsRequired();
        builder.Property(x => x.IsSystemOnly).IsRequired();

        // Permission set as jsonb integer array. Stored as List<Permission>;
        // EF round-trips through the JsonbValueConverter just like step
        // configs and lifecycle phases.
        builder.Property(x => x.GrantedPermissions)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<List<Permission>>())
            .IsRequired();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.IsBuiltIn).IsRequired();
        builder.Property(x => x.IsEveryoneTeam).IsRequired();

        // Nullable SpaceId — null = system-level team, visible everywhere.
        builder.Property(x => x.SpaceId);
        builder.HasIndex(x => x.SpaceId);

        // Names are unique within (SpaceId, name). NULLS NOT DISTINCT so
        // system teams (SpaceId = null) also cannot share a name — without it
        // Postgres treats each NULL SpaceId as distinct, letting two system
        // teams called "Everyone" coexist.
        builder.HasIndex(x => new { x.SpaceId, x.Name })
            .IsUnique()
            .AreNullsDistinct(false);

        builder.HasMany(x => x.Members)
            .WithOne(m => m.Team)
            .HasForeignKey(m => m.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.ExternalGroups)
            .WithOne(g => g.Team)
            .HasForeignKey(g => g.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.RoleAssignments)
            .WithOne(a => a.Team)
            .HasForeignKey(a => a.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

public class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("team_members");

        // Composite key — a user is in a team at most once.
        builder.HasKey(x => new { x.TeamId, x.UserId });

        builder.HasIndex(x => x.UserId);

        // Membership dies with the user. Real FK CASCADE (belt-and-braces
        // cleanup still lives in UserService.DeleteAsync). Postgres permits
        // the two cascade paths into this table (Team delete + User delete).
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.AddedUtc).IsRequired();
    }
}

public class TeamExternalGroupConfiguration : IEntityTypeConfiguration<TeamExternalGroup>
{
    public void Configure(EntityTypeBuilder<TeamExternalGroup> builder)
    {
        builder.ToTable("team_external_groups");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.GroupClaim).HasMaxLength(512).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(256);

        builder.HasOne(x => x.IdentityProvider)
            .WithMany()
            .HasForeignKey(x => x.IdentityProviderId)
            .OnDelete(DeleteBehavior.SetNull);

        // Same group claim cannot map to the same team twice. NULLS NOT
        // DISTINCT so an "any provider" mapping (IdentityProviderId = null)
        // also can't be duplicated for a given (team, claim).
        builder.HasIndex(x => new { x.TeamId, x.IdentityProviderId, x.GroupClaim })
            .IsUnique()
            .AreNullsDistinct(false);
    }
}

public class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("role_assignments");
        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.Role)
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        // SpaceId is nullable — null = system-wide assignment.
        builder.Property(x => x.SpaceId);
        builder.HasIndex(x => x.SpaceId);

        // Scope dimensions now live in role_assignment_scopes (child rows with
        // real per-dimension FKs). Cascade so removing the assignment clears
        // its scope rows in the same transaction.
        builder.HasMany(x => x.Scopes)
            .WithOne(s => s.RoleAssignment)
            .HasForeignKey(s => s.RoleAssignmentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite lookup index: when evaluating "what permissions does
        // team T have in space S?" we filter on both columns.
        builder.HasIndex(x => new { x.TeamId, x.SpaceId });

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

public class RoleAssignmentScopeConfiguration : IEntityTypeConfiguration<RoleAssignmentScope>
{
    public void Configure(EntityTypeBuilder<RoleAssignmentScope> builder)
    {
        builder.ToTable("role_assignment_scopes", t => t.HasCheckConstraint(
            "ck_role_assignment_scopes_exactly_one_dimension",
            "num_nonnulls(project_group_id, project_id, environment_id, tenant_id) = 1"));
        builder.HasKey(x => x.Id);

        // Per-dimension FKs, all CASCADE and optional (exactly one is set per
        // row, enforced by the CHECK above). Deleting the referenced entity
        // removes the scope row, so the grant simply loses that restriction.
        builder.HasOne<ProjectGroup>()
            .WithMany()
            .HasForeignKey(x => x.ProjectGroupId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DeploymentEnvironment>()
            .WithMany()
            .HasForeignKey(x => x.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Prevent duplicate grants of the same (assignment, dimension, id).
        // NULLS NOT DISTINCT so the three NULL columns in each row collapse.
        builder.HasIndex(x => new
            {
                x.RoleAssignmentId, x.ProjectGroupId, x.ProjectId, x.EnvironmentId, x.TenantId,
            })
            .IsUnique()
            .AreNullsDistinct(false);
    }
}

public class IdentityProviderConfiguration : IEntityTypeConfiguration<IdentityProvider>
{
    public void Configure(EntityTypeBuilder<IdentityProvider> builder)
    {
        builder.ToTable("identity_providers");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.Name).IsUnique();

        builder.Property(x => x.Type).IsRequired().HasConversion<int>();
        builder.Property(x => x.Authority).HasMaxLength(2048);
        builder.Property(x => x.ClientId).HasMaxLength(512);
        builder.Property(x => x.ClientSecretEncrypted).HasMaxLength(2048);
        builder.Property(x => x.Scopes).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.GroupClaimName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.IconUrl).HasMaxLength(2048);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.AutoProvisionUsers).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();

        builder.HasOne(x => x.DefaultTeam)
            .WithMany()
            .HasForeignKey(x => x.DefaultTeamId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}
