using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Runs schema migration, seeding, and first-admin creation against a <em>specific</em>
/// tenant database by opening a DI scope and pushing the account onto
/// <see cref="IAccountContext"/>. The account-aware <c>IDbContextFactory</c> then
/// builds every <see cref="KrakenDbContext"/> (and the Identity user store's scoped
/// context) against that account's connection — so all the existing seed/admin
/// services are reused unchanged, just pointed at the new database.
/// </summary>
public sealed class TenantInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<TenantInitializer> logger)
{
    /// <summary>Applies all pending EF migrations to the account's tenant database.</summary>
    public async Task MigrateAsync(ResolvedAccount account, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        using (sp.GetRequiredService<IAccountContext>().WithAccount(account))
        {
            var dbFactory = sp.GetRequiredService<IDbContextFactory<KrakenDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }

        logger.LogInformation("Migrated tenant database for {Subdomain}.", account.Subdomain);
    }

    /// <summary>Seeds the Default Space + built-in RBAC and creates the first admin user.</summary>
    public async Task SeedAsync(ResolvedAccount account, NewAccountRequest req, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        using (sp.GetRequiredService<IAccountContext>().WithAccount(account))
        {
            await sp.GetRequiredService<SpaceService>().EnsureDefaultAsync(ct).ConfigureAwait(false);
            await sp.GetRequiredService<BuiltInRbacSeeder>().SeedAsync(ct).ConfigureAwait(false);
            await CreateFirstAdminAsync(sp, req, ct).ConfigureAwait(false);
        }

        logger.LogInformation("Seeded tenant database for {Subdomain}.", account.Subdomain);
    }

    private static async Task CreateFirstAdminAsync(
        IServiceProvider sp, NewAccountRequest req, CancellationToken ct)
    {
        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByEmailAsync(req.AdminEmail).ConfigureAwait(false);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = req.AdminEmail,
                Email = req.AdminEmail,
                EmailConfirmed = true,
            };
            var result = await userManager.CreateAsync(user, req.AdminPassword).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    "Failed to create the first admin user: " +
                    string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
            }
        }

        // Add the admin to the system-level "Kraken Administrators" team.
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KrakenDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var already = await db.TeamMembers
            .AnyAsync(m => m.TeamId == BuiltInRbacSeeder.KrakenAdministratorsTeamId && m.UserId == user.Id, ct)
            .ConfigureAwait(false);
        if (!already)
        {
            db.TeamMembers.Add(new TeamMember
            {
                TeamId = BuiltInRbacSeeder.KrakenAdministratorsTeamId,
                UserId = user.Id,
                AddedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
