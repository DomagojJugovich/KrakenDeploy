using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Identity;
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

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var existing = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (existing is not null)
        {
            Console.WriteLine($"User '{email}' already exists. Nothing to do.");
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

        Console.WriteLine($"Admin user '{email}' created.");
        return 0;
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
