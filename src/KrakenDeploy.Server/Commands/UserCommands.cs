using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// CLI subcommands rooted at <c>users</c>. Invoked by <see cref="Program.Main"/>
/// when the first argument is <c>users</c>; the web server is not started.
/// </summary>
internal static class UserCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintTopLevelUsage();
            return 1;
        }

        return args[0] switch
        {
            "create-admin" => await CreateAdminAsync(args.AsSpan(1).ToArray()).ConfigureAwait(false),
            "--help" or "-h" or "help" => PrintTopLevelUsage(success: true),
            _ => UnknownSubcommand(args[0])
        };
    }

    private static async Task<int> CreateAdminAsync(string[] args)
    {
        string? email = null;
        string? password = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--email")
            {
                email = args[i + 1];
            }
            else if (args[i] == "--password")
            {
                password = args[i + 1];
            }
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Usage: users create-admin --email <e> --password <p>");
            return 1;
        }

        var builder = Host.CreateApplicationBuilder();
        var connectionString = builder.Configuration.GetConnectionString("KrakenDb")
            ?? throw new InvalidOperationException(
                "Connection string 'KrakenDb' is not configured. " +
                "Set ConnectionStrings:KrakenDb in appsettings.{Environment}.json or via user-secrets.");

        builder.Services.AddKrakenDeployData(connectionString);
        builder.Services.AddKrakenDeployIdentityCore();

        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);

        // Defensive: ensure RBAC seed exists before we add the admin to a team
        // (the admin CLI may run before the web server has started for the
        // first time on a fresh DB).
        var spaceSvc = scope.ServiceProvider.GetRequiredService<SpaceService>();
        await spaceSvc.EnsureDefaultAsync().ConfigureAwait(false);
        var rbacSeeder = scope.ServiceProvider.GetRequiredService<BuiltInRbacSeeder>();
        await rbacSeeder.SeedAsync().ConfigureAwait(false);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var existing = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (existing is not null)
        {
            // Idempotent membership check: ensure the existing user IS in
            // Kraken Administrators (covers re-runs after team data was reset).
            await EnsureKrakenAdministratorAsync(db, existing.Id).ConfigureAwait(false);
            Console.WriteLine($"User '{email}' already exists. Membership in Kraken Administrators verified.");
            return 0;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine("Failed to create admin user:");
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"  {error.Code}: {error.Description}");
            }
            return 1;
        }

        // Add the new user to "Kraken Administrators" so they actually have
        // permissions to do anything — without this they'd be a logged-in
        // user with zero authorized actions.
        await EnsureKrakenAdministratorAsync(db, user.Id).ConfigureAwait(false);

        Console.WriteLine($"Admin user '{email}' created and added to Kraken Administrators.");
        return 0;
    }

    /// <summary>
    /// Idempotently adds the user to the Kraken Administrators system team.
    /// </summary>
    private static async Task EnsureKrakenAdministratorAsync(KrakenDbContext db, Guid userId)
    {
        var alreadyMember = await db.TeamMembers
            .AnyAsync(m => m.TeamId == BuiltInRbacSeeder.KrakenAdministratorsTeamId
                        && m.UserId == userId)
            .ConfigureAwait(false);

        if (alreadyMember)
        {
            return;
        }

        db.TeamMembers.Add(new TeamMember
        {
            TeamId   = BuiltInRbacSeeder.KrakenAdministratorsTeamId,
            UserId   = userId,
            AddedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static int PrintTopLevelUsage(bool success = false)
    {
        var stream = success ? Console.Out : Console.Error;
        stream.WriteLine("Usage: KrakenDeploy.Server users <subcommand> [options]");
        stream.WriteLine();
        stream.WriteLine("Subcommands:");
        stream.WriteLine("  create-admin --email <e> --password <p>   Create the initial admin user.");
        return success ? 0 : 1;
    }

    private static int UnknownSubcommand(string name)
    {
        Console.Error.WriteLine($"Unknown subcommand: '{name}'.");
        PrintTopLevelUsage();
        return 1;
    }
}
