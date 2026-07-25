using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Renames ASP.NET Identity tables from <c>asp_net_*</c> defaults to
/// concise KrakenDeploy-style names. Combined with EFCore.NamingConventions,
/// the columns end up snake_cased automatically.
/// <para>
/// Identity-managed roles (<c>IdentityRole</c>, <c>IdentityUserRole</c>,
/// <c>IdentityRoleClaim</c>) are <em>not</em> mapped here — KrakenDeploy uses
/// its own Role/Team/RoleAssignment model in Server.Core.Domain.Security, and
/// the DbContext uses <c>IdentityUserContext</c> to keep them out of the schema.
/// </para>
/// </summary>
public static class IdentityConfiguration
{
    public static void ConfigureIdentity(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationUser>().ToTable("users");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        // The IdP a user last signed in with (used to scope external-group
        // team mapping). FK SET NULL + index: deleting an identity provider
        // clears the stamp rather than blocking or cascading the user.
        // Mirrors team_external_groups.identity_provider_id (also SetNull).
        modelBuilder.Entity<ApplicationUser>()
            .HasOne<IdentityProvider>()
            .WithMany()
            .HasForeignKey(u => u.LastOidcProviderId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ApplicationUser>()
            .HasIndex(u => u.LastOidcProviderId);

        // WP5 item 4: optional human-readable display name (preferred UI/audit
        // label; falls back to UserName/Email when null).
        modelBuilder.Entity<ApplicationUser>()
            .Property(u => u.DisplayName)
            .HasMaxLength(200);
    }
}
