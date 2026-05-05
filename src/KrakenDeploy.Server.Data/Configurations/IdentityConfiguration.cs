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
    }
}
