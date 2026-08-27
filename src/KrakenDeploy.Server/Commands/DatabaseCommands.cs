using KrakenDeploy.Platform;
using KrakenDeploy.Server.Core.Domain.Platform;
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
/// <para>
/// <c>setup</c> (first install) and <c>upgrade</c> (new release over an existing
/// database) run the same idempotent body: the non-additive guard (BG1/T4), the
/// app-schema migrations, the platform-schema migrations (OnPremBlueGreen), the
/// Hangfire schema (blue-green topologies — slot boot never touches it), and the
/// seeders. They exist as two verbs so runbooks and error messages can name the
/// intent.
/// </para>
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
            "setup" => await SetupAsync(args.AsSpan(1).ToArray(), contentRoot, verb: "setup").ConfigureAwait(false),
            "upgrade" => await SetupAsync(args.AsSpan(1).ToArray(), contentRoot, verb: "upgrade").ConfigureAwait(false),
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

    private static async Task<int> SetupAsync(string[] args, string contentRoot, string verb)
    {
        string? connectionString = null;
        string? account = null;
        string? topologyArg = null;
        var stopTheWorld = args.Contains("--stop-the-world");

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
            else if (args[i] == "--topology")
            {
                topologyArg = args[i + 1];
            }
        }

        var topologyBuilder = CliHost.CreateBuilder(contentRoot);
        var resolvedTopology = ResolveSetupTopology(topologyBuilder.Configuration, topologyArg, verb);
        if (resolvedTopology is null)
        {
            return 1; // the resolver already printed the reason
        }

        var topology = resolvedTopology.Value;

        // An explicit --connection-string wins in any mode (operator names the exact
        // DB). Otherwise resolve per mode: single-tenant topologies → KrakenDb; Saas →
        // the tenant named by --account. NOTE: under Saas, tenant schemas are
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

            // BG1/T4 — non-additive guard, BEFORE anything migrates: a
            // [StopTheWorld]-marked pending migration (or a pending Hangfire
            // storage upgrade) is refused while the release registry shows
            // another live release, unless --stop-the-world asserts the
            // documented full-stop runbook was followed.
            if (!await NonAdditiveUpgradeGuard.AllowAsync(
                    topology, connectionString, builder.Configuration, stopTheWorld)
                .ConfigureAwait(false))
            {
                return 1;
            }
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

            if (topology == DeploymentTopology.OnPremBlueGreen)
            {
                // BG1/T3: the release registry lives in KrakenDb under the
                // dedicated `platform` schema with its OWN history table — this
                // command is its only migration path (never slot boot).
                Console.Write("Applying platform-schema migrations (release registry)... ");
                var platformOptions = new DbContextOptionsBuilder<PlatformReleaseDbContext>()
                    .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                        PlatformReleaseSchema.MigrationsHistoryTableName,
                        PlatformReleaseSchema.OnPremSchemaName))
                    .UseSnakeCaseNamingConvention()
                    .Options;
                await using (var platformDb = new PlatformReleaseDbContext(
                    platformOptions,
                    new PlatformReleaseSchema(PlatformReleaseSchema.OnPremSchemaName)))
                {
                    await platformDb.Database.MigrateAsync().ConfigureAwait(false);
                }

                Console.WriteLine("done.");
            }

            if (topology != DeploymentTopology.OnPrem)
            {
                // BG1/T4: slot boot has PrepareSchemaIfNecessary=false in the
                // blue-green topologies, so this command owns the SHARED Hangfire
                // schema (guard above already version-checked it against live
                // releases). Saas keeps the job store in the catalog.
                var hangfireConnection = topology == DeploymentTopology.Saas
                    ? builder.Configuration.GetConnectionString("Catalog")
                    : connectionString;
                if (!string.IsNullOrWhiteSpace(hangfireConnection))
                {
                    Console.Write("Ensuring Hangfire storage schema... ");
                    HangfireSchemaInspector.EnsureSchema(hangfireConnection);
                    Console.WriteLine("done.");
                }
            }

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

            // SC2: template seeding retired (registry-derived picker). Step
            // packages seed here too so `database setup` and the web-host
            // startup produce the same catalog.
            Console.Write("Seeding built-in step packages... ");
            var packageSeeder = scope.ServiceProvider.GetRequiredService<BuiltInStepPackageSeeder>();
            await packageSeeder.SeedAsync().ConfigureAwait(false);
            Console.WriteLine("done.");

            Console.Write("Rebuilding step-type registry... ");
            var stepTypeRegistry = scope.ServiceProvider.GetRequiredService<StepTypeRegistry>();
            await stepTypeRegistry.RebuildAsync().ConfigureAwait(false);
            Console.WriteLine("done.");

            Console.WriteLine();
            Console.WriteLine($"Database {verb} complete.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{(verb == "upgrade" ? "Upgrade" : "Setup")} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Topology selection for <c>setup</c>/<c>upgrade</c> (BG1 item 6 — the
    /// kraken-init prompt). Precedence:
    /// <list type="number">
    /// <item>Configured <c>Deployment:Topology</c> — authoritative. An explicit
    /// <c>--topology</c> that CONTRADICTS it is refused (the flag cannot silently
    /// diverge from what the server will boot with).</item>
    /// <item><c>--topology &lt;value&gt;</c> — the non-interactive install path
    /// (kraken-init in docker-compose has no TTY).</item>
    /// <item>An interactive prompt (default OnPrem) when stdin is a terminal.</item>
    /// <item>OnPrem otherwise.</item>
    /// </list>
    /// </summary>
    private static DeploymentTopology? ResolveSetupTopology(
        Microsoft.Extensions.Configuration.ConfigurationManager configuration,
        string? topologyArg,
        string verb)
    {
        var configured = CliHost.ResolveTopologyOrError(configuration);
        if (configured is null)
        {
            return null; // stale MultiAccount:Enabled or invalid value — already printed
        }

        var configuredExplicitly =
            !string.IsNullOrWhiteSpace(configuration[DeploymentOptions.TopologyKey]);

        DeploymentTopology? fromArg = null;
        if (topologyArg is not null)
        {
            if (!Enum.TryParse<DeploymentTopology>(topologyArg, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                Console.Error.WriteLine(
                    $"--topology has the unrecognised value '{topologyArg}'. " +
                    $"Valid values: {string.Join(" | ", Enum.GetNames<DeploymentTopology>())}.");
                return null;
            }

            fromArg = parsed;
        }

        if (configuredExplicitly)
        {
            if (fromArg is not null && fromArg != configured)
            {
                Console.Error.WriteLine(
                    $"--topology {fromArg} contradicts the configured " +
                    $"{DeploymentOptions.TopologyKey}={configured}. The configuration is what the " +
                    "server boots with — change Deployment__Topology there instead.");
                return null;
            }

            return configured;
        }

        if (fromArg is not null)
        {
            PrintTopologyChoice(fromArg.Value);
            return fromArg;
        }

        // Nothing configured, no flag. Prompt when a human is attached; otherwise
        // default to OnPrem exactly like the server boot would.
        if (verb == "setup" && !Console.IsInputRedirected)
        {
            Console.WriteLine("Deployment topology (Deployment:Topology) is not configured. Choose one:");
            Console.WriteLine("  1) OnPrem          — single instance; upgrades are stop → migrate → start. (default)");
            Console.WriteLine("  2) OnPremBlueGreen — 3 slots + router; zero-downtime rolling upgrades.");
            Console.WriteLine("  3) Saas            — multi-account control plane (requires the catalog).");
            Console.Write("Topology [1]: ");
            var answer = Console.ReadLine()?.Trim();
            var chosen = answer switch
            {
                null or "" or "1" => DeploymentTopology.OnPrem,
                "2" => DeploymentTopology.OnPremBlueGreen,
                "3" => DeploymentTopology.Saas,
                _ => Enum.TryParse<DeploymentTopology>(answer, ignoreCase: true, out var byName)
                        && Enum.IsDefined(byName)
                    ? byName
                    : DeploymentTopology.OnPrem,
            };
            PrintTopologyChoice(chosen);
            return chosen;
        }

        return DeploymentTopology.OnPrem;
    }

    private static void PrintTopologyChoice(DeploymentTopology topology)
    {
        Console.WriteLine($"Topology: {topology}.");
        if (topology == DeploymentTopology.OnPremBlueGreen)
        {
            Console.WriteLine(
                "NOTE: OnPremBlueGreen commits you to ADDITIVE-ONLY migrations between live " +
                "releases; a non-additive upgrade (a [StopTheWorld]-marked migration, or a " +
                "Hangfire storage upgrade) needs a stop window — the stop-the-world runbook in " +
                "docs/on-prem-guide.md. In exchange, ordinary upgrades are zero-downtime: " +
                "register slot → health-check → flip → drain → retire.");
        }

        Console.WriteLine(
            $"Persist it as Deployment__Topology={topology} (environment) or " +
            $"\"Deployment\": {{ \"Topology\": \"{topology}\" }} (appsettings) — the server " +
            "boots with the configured value, not with this command's choice.");
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
            var types = NonAdditiveUpgradeGuard.GetMigrationTypes(db);
            Console.WriteLine($"Pending migrations ({pending.Count}):");
            foreach (var m in pending)
            {
                var marked = types.TryGetValue(m, out var type)
                    && type.GetCustomAttributes(typeof(StopTheWorldAttribute), inherit: false).Length > 0;
                Console.WriteLine($"  - {m}{(marked ? "  [StopTheWorld]" : "")}");
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
        stream.WriteLine("  setup    [--connection-string <cs> | --account <subdomain>] [--topology <t>] [--stop-the-world]");
        stream.WriteLine("           First install: apply migrations + seed data. Idempotent.");
        stream.WriteLine("  upgrade  [--connection-string <cs> | --account <subdomain>] [--stop-the-world]");
        stream.WriteLine("           New release over an existing database — same idempotent body as setup.");
        stream.WriteLine("           Blue-green topologies: refuses a [StopTheWorld]-marked pending migration");
        stream.WriteLine("           (or a pending Hangfire storage upgrade) while another release is live;");
        stream.WriteLine("           --stop-the-world overrides after the documented full-stop runbook.");
        stream.WriteLine("  status   [--account <subdomain>]");
        stream.WriteLine("           Check connectivity and pending migrations ([StopTheWorld] flagged).");
        stream.WriteLine();
        stream.WriteLine("  Under Deployment:Topology=Saas, --account selects the tenant DB (there is no");
        stream.WriteLine("  single KrakenDb); tenant schemas are normally migrated by provisioning / fleet-migrate.");
        return success ? 0 : 1;
    }

    private static int UnknownSubcommand(string name)
    {
        Console.Error.WriteLine($"Unknown subcommand: '{name}'.");
        PrintTopLevelUsage();
        return 1;
    }
}
