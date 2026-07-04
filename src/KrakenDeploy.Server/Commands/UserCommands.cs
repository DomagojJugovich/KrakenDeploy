using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace KrakenDeploy.Server.Commands;

internal static class UserCommands
{
    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        if (args.Length == 0)
        {
            PrintTopLevelUsage();
            return 1;
        }

        return args[0] switch
        {
            "create-admin" => await CreateAdminAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "--help" or "-h" or "help" => PrintTopLevelUsage(success: true),
            _ => UnknownSubcommand(args[0])
        };
    }

    private static async Task<int> CreateAdminAsync(string[] args, string contentRoot)
    {
        string? email = null;
        string? password = null;
        string? account = null;

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
            else if (args[i] == "--account")
            {
                account = args[i + 1];
            }
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Usage: users create-admin --email <e> --password <p> [--account <subdomain>]");
            return 1;
        }

        var builder = CliHost.CreateBuilder(contentRoot);

        // Single-instance binds to KrakenDb; multi-account requires --account and binds
        // to that tenant's DB via the catalog (mirrors `restore --account`). Without
        // this guard, multi-account would migrate + seed an admin into the stale
        // fallback KrakenDb — a silent write to the wrong database.
        var connectionString = await CliHost
            .ResolveTenantConnectionStringAsync(builder, contentRoot, account)
            .ConfigureAwait(false);
        if (connectionString is null)
        {
            return 1; // the resolver already printed the reason
        }

        builder.Services.AddKrakenDeployData(connectionString);
        builder.Services.AddKrakenDeployIdentityCore();
        // create-admin never encrypts — register envelope encryption only so the
        // DI graph resolves. The DEK is loaded lazily on first Encrypt/Decrypt,
        // which never happens here, so a throwaway KEK is harmless.
        builder.Services.AddKrakenDeployEncryption(
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);

        var spaceSvc = scope.ServiceProvider.GetRequiredService<SpaceService>();
        await spaceSvc.EnsureDefaultAsync().ConfigureAwait(false);
        var rbacSeeder = scope.ServiceProvider.GetRequiredService<BuiltInRbacSeeder>();
        await rbacSeeder.SeedAsync().ConfigureAwait(false);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var existing = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (existing is not null)
        {
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

        await EnsureKrakenAdministratorAsync(db, user.Id).ConfigureAwait(false);

        Console.WriteLine($"Admin user '{email}' created and added to Kraken Administrators.");
        return 0;
    }

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
        stream.WriteLine("  create-admin --email <e> --password <p> [--account <subdomain>]");
        stream.WriteLine("      Create (or verify) an admin user. In multi-account mode --account is");
        stream.WriteLine("      required and targets that tenant's database.");
        return success ? 0 : 1;
    }

    private static int UnknownSubcommand(string name)
    {
        Console.Error.WriteLine($"Unknown subcommand: '{name}'.");
        PrintTopLevelUsage();
        return 1;
    }
}
