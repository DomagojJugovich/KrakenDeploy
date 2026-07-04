using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// CLI subcommands rooted at <c>database</c>. Invoked by <see cref="Program.Main"/>
/// when the first argument is <c>database</c>; the web server is not started.
/// </summary>
internal static class DatabaseCommands
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
            "create" => await CreateAsync(args.AsSpan(1).ToArray()).ConfigureAwait(false),
            "setup" => await SetupAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "status" => await StatusAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "--help" or "-h" or "help" => PrintTopLevelUsage(success: true),
            _ => UnknownSubcommand(args[0])
        };
    }

    private static async Task<int> CreateAsync(string[] args)
    {
        string? host = null, port = null, username = null, password = null, dbName = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--host": host = args[i + 1]; break;
                case "--port": port = args[i + 1]; break;
                case "--username": username = args[i + 1]; break;
                case "--password": password = args[i + 1]; break;
                case "--database-name": dbName = args[i + 1]; break;
            }
        }

        if (host is null || username is null || password is null || dbName is null)
        {
            Console.Error.WriteLine(
                "Usage: database create --host <h> --username <u> --password <p> " +
                "--database-name <db> [--port 5432]");
            return 1;
        }

        port ??= "5432";

        var adminCs = $"Host={host};Port={port};Database=postgres;Username={username};Password={password}";

        try
        {
            await using var conn = new NpgsqlConnection(adminCs);
            await conn.OpenAsync().ConfigureAwait(false);

            if (!System.Text.RegularExpressions.Regex.IsMatch(dbName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            {
                Console.Error.WriteLine(
                    "Database name must start with a letter or underscore and contain " +
                    "only letters, digits, and underscores.");
                return 1;
            }

            var exists = await DatabaseExistsAsync(conn, dbName).ConfigureAwait(false);
            if (exists)
            {
                Console.WriteLine($"Database '{dbName}' already exists.");
            }
            else
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                Console.WriteLine($"Database '{dbName}' created.");
            }

            var cs = $"Host={host};Port={port};Database={dbName};Username={username};Password={password}";
            Console.WriteLine();
            Console.WriteLine("Connection string:");
            Console.WriteLine(cs);
            Console.WriteLine();
            Console.WriteLine("Next: KrakenDeploy.Server database setup --connection-string \"<above>\"");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to create database: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> SetupAsync(string[] args, string contentRoot)
    {
        string? connectionString = null;
        string? account = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--connection-string")
            {
                connectionString = args[i + 1];
            }
            else if (args[i] == "--account")
            {
                account = args[i + 1];
            }
        }

        // An explicit --connection-string wins in any mode (operator names the exact
        // DB). Otherwise resolve per mode: single-instance → KrakenDb; multi-account →
        // the tenant named by --account. NOTE: in multi-account, tenant schemas are
        // normally migrated by provisioning / fleet-migrate — this is the manual
        // per-tenant escape hatch, not the primary path.
        if (connectionString is null)
        {
            var resolveBuilder = CliHost.CreateBuilder(contentRoot);
            connectionString = await CliHost
                .ResolveTenantConnectionStringAsync(resolveBuilder, contentRoot, account)
                .ConfigureAwait(false);
            if (connectionString is null)
            {
                return 1; // the resolver already printed the reason
            }
        }

        try
        {
            var builder = CliHost.CreateBuilder(contentRoot);
            // Register envelope encryption (KEK from config) so AddKrakenDeployData
            // services (VariableService, IdentityProviderService, etc.) can resolve
            // IEncryptionService. The DEK is generated + wrapped under this KEK
            // after migrate below.
            var encKey = builder.Configuration["Encryption:MasterKey"];
            if (string.IsNullOrWhiteSpace(encKey))
            {
                // Refuse OUTSIDE Development. Provisioning the DEK under an
                // ephemeral random KEK (discarded on exit) would permanently and
                // unrecoverably lock the DB — the DEK could never be unwrapped
                // again. Mirror the web host's non-Dev fail-fast (Program.cs).
                var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                    ?? "Development";
                if (!string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        "Encryption:MasterKey (the KEK) is not configured. Refusing to provision a DEK " +
                        "under an ephemeral key outside Development — it would be permanently unrecoverable. " +
                        "Set a base64-encoded 32-byte key (Encryption__MasterKey) before running 'database setup'.");
                    return 1;
                }

                encKey = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                Console.WriteLine(
                    "WARNING: Encryption:MasterKey not set — provisioning the DEK under an EPHEMERAL " +
                    "Development key. It is UNRECOVERABLE after this process exits; set a real key for any " +
                    "database you intend to keep.");
            }
            builder.Services.AddKrakenDeployEncryption(encKey);
            builder.Services.AddKrakenDeployData(connectionString);
            builder.Services.AddKrakenDeployIdentityCore();

            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();

            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

            Console.Write("Applying migrations... ");
            await db.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine("done.");

            // Envelope encryption: generate + cache the wrapped DEK (idempotent).
            // This is the real prod first-boot path — the web host's prod branch
            // does not migrate/seed.
            Console.Write("Provisioning data-encryption key... ");
            await scope.ServiceProvider
                .GetRequiredService<KrakenDeploy.Server.Data.Encryption.IDekProvider>()
                .EnsureDekAsync().ConfigureAwait(false);
            Console.WriteLine("done.");

            Console.Write("Seeding Default Space... ");
            var spaceSvc = scope.ServiceProvider.GetRequiredService<SpaceService>();
            await spaceSvc.EnsureDefaultAsync().ConfigureAwait(false);
            Console.WriteLine("done.");

            Console.Write("Seeding built-in RBAC... ");
            var rbacSeeder = scope.ServiceProvider.GetRequiredService<BuiltInRbacSeeder>();
            await rbacSeeder.SeedAsync().ConfigureAwait(false);
            Console.WriteLine("done.");

            Console.Write("Seeding built-in step templates... ");
            var templateSeeder = scope.ServiceProvider.GetRequiredService<BuiltInStepTemplateSeeder>();
            await templateSeeder.SeedAsync().ConfigureAwait(false);
            Console.WriteLine("done.");

            Console.WriteLine();
            Console.WriteLine("Database setup complete.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Setup failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> StatusAsync(string[] args, string contentRoot)
    {
        string? account = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--account")
            {
                account = args[i + 1];
            }
        }

        // Single-instance → KrakenDb; multi-account → the tenant named by --account
        // (no single KrakenDb to report on).
        var builder = CliHost.CreateBuilder(contentRoot);
        var connectionString = await CliHost
            .ResolveTenantConnectionStringAsync(builder, contentRoot, account)
            .ConfigureAwait(false);
        if (connectionString is null)
        {
            return 1; // the resolver already printed the reason
        }

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            Console.WriteLine($"Connected to Postgres at {conn.DataSource}:{conn.Port}/{conn.Database}.");
            Console.WriteLine($"Server version: {conn.PostgreSqlVersion}");
            await conn.CloseAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connection failed: {ex.Message}");
            return 1;
        }

        // Check pending migrations via a standalone DbContext (no full DI).
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new KrakenDbContext(options, new DefaultSpaceContext());
        var pending = (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).ToList();
        if (pending.Count == 0)
        {
            Console.WriteLine("Migrations: up to date.");
        }
        else
        {
            Console.WriteLine($"Pending migrations ({pending.Count}):");
            foreach (var m in pending)
            {
                Console.WriteLine($"  - {m}");
            }
        }

        return 0;
    }

    private static async Task<bool> DatabaseExistsAsync(NpgsqlConnection conn, string dbName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
        cmd.Parameters.AddWithValue("name", dbName);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is not null;
    }

    private static int PrintTopLevelUsage(bool success = false)
    {
        var stream = success ? Console.Out : Console.Error;
        stream.WriteLine("Usage: KrakenDeploy.Server database <subcommand> [options]");
        stream.WriteLine();
        stream.WriteLine("Subcommands:");
        stream.WriteLine("  create   Create the Postgres database (connects to 'postgres' maintenance db).");
        stream.WriteLine("  setup    [--connection-string <cs> | --account <subdomain>]");
        stream.WriteLine("           Apply migrations + seed data. Idempotent — safe on upgrades.");
        stream.WriteLine("  status   [--account <subdomain>]");
        stream.WriteLine("           Check connectivity and pending migrations.");
        stream.WriteLine();
        stream.WriteLine("  In multi-account mode --account selects the tenant DB (there is no single");
        stream.WriteLine("  KrakenDb); tenant schemas are normally migrated by provisioning / fleet-migrate.");
        return success ? 0 : 1;
    }

    private static int UnknownSubcommand(string name)
    {
        Console.Error.WriteLine($"Unknown subcommand: '{name}'.");
        PrintTopLevelUsage();
        return 1;
    }
}
