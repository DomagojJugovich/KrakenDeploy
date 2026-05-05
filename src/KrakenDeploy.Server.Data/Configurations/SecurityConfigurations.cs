using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Conventions;
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

        // Names are unique within (SpaceId, name); for system teams (SpaceId
        // is null) Postgres treats NULL as distinct from NULL, so we index
        // separately to enforce per-Space uniqueness without colliding with
        // system teams.
        builder.HasIndex(x => new { x.SpaceId, x.Name }).IsUnique();

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

        // Same group claim cannot map to the same team twice.
        builder.HasIndex(x => new { x.TeamId, x.IdentityProviderId, x.GroupClaim }).IsUnique();
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

        // Scope dimensions stored as jsonb arrays of Guid. Empty = "all".
        builder.Property(x => x.ProjectGroupIds)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<List<Guid>>())
            .IsRequired();

        builder.Property(x => x.ProjectIds)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<List<Guid>>())
            .IsRequired();

        builder.Property(x => x.EnvironmentIds)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<List<Guid>>())
            .IsRequired();

        builder.Property(x => x.TenantIds)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<List<Guid>>())
            .IsRequired();

        builder.Property(x => x.TenantTagIds)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<List<Guid>>())
            .IsRequired();

        // IsUnscoped is a computed property — don't try to map it.
        builder.Ignore(x => x.IsUnscoped);

        // Composite lookup index: when evaluating "what permissions does
        // team T have in space S?" we filter on both columns.
        builder.HasIndex(x => new { x.TeamId, x.SpaceId });

        builder.Property(x => x.CreatedUtc).IsRequired();
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
