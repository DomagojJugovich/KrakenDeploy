using System.Reflection;
using System.Text.RegularExpressions;
using KrakenDeploy.Platform;
using KrakenDeploy.Platform.Releases;
using KrakenDeploy.Server.Core.Domain.Platform;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// The non-additive migration guard (BG1/T4/T10, marker-aware). Under the
/// blue-green topologies a schema change rolls out while ANOTHER release is still
/// live, so <c>database upgrade</c>/<c>setup</c> must refuse anything that would
/// break the live release:
/// <list type="bullet">
/// <item>an EF migration carrying <see cref="StopTheWorldAttribute"/>;</item>
/// <item>a pending Hangfire storage-schema upgrade (always treated as marked —
/// the schema is SHARED by every slot, and Hangfire.PostgreSql migrates it
/// destructively in place).</item>
/// </list>
/// Purely-additive pending sets pass — that IS the rolling upgrade. The operator
/// override for the documented full-stop runbook is <c>--stop-the-world</c>.
/// Under <c>OnPrem</c> the guard is a no-op (single process; every upgrade is
/// already stop → migrate → start).
/// </summary>
internal static class NonAdditiveUpgradeGuard
{
    /// <summary>
    /// Returns <c>true</c> when the upgrade may proceed; prints the refusal
    /// (naming the migration and the runbook) and returns <c>false</c> otherwise.
    /// </summary>
    public static async Task<bool> AllowAsync(
        DeploymentTopology topology,
        string connectionString,
        IConfiguration configuration,
        bool stopTheWorld)
    {
        if (topology == DeploymentTopology.OnPrem)
        {
            return true;
        }

        var markedPending = await GetMarkedPendingMigrationsAsync(connectionString)
            .ConfigureAwait(false);

        var hangfireConnection = topology == DeploymentTopology.Saas
            ? configuration.GetConnectionString("Catalog")
            : connectionString;
        var hangfirePending = hangfireConnection is not null
            && await HangfireSchemaInspector.IsUpgradePendingAsync(hangfireConnection)
                .ConfigureAwait(false);

        if (markedPending.Count == 0 && !hangfirePending)
        {
            return true; // Purely additive — the rolling upgrade path.
        }

        var otherLiveReleases = await GetOtherLiveReleasesAsync(
            topology, connectionString, configuration).ConfigureAwait(false);
        if (otherLiveReleases.Count == 0)
        {
            // Nothing else is serving (fresh install, or every other release is
            // Retired) — a marked migration is safe to apply.
            return true;
        }

        if (stopTheWorld)
        {
            Console.WriteLine(
                "--stop-the-world: applying non-additive changes while the registry still shows " +
                $"live release(s) [{string.Join(", ", otherLiveReleases)}]. You are asserting the " +
                "stop-the-world runbook was followed (all slots drained and STOPPED).");
            return true;
        }

        Console.Error.WriteLine("REFUSED: this upgrade is non-additive and another release is live.");
        foreach (var (id, reason) in markedPending)
        {
            Console.Error.WriteLine(
                $"  - migration '{id}' is marked [StopTheWorld]{(reason is null ? "" : $": {reason}")}");
        }

        if (hangfirePending)
        {
            Console.Error.WriteLine(
                "  - the Hangfire storage schema has a pending upgrade (shared by every slot; " +
                "always a stop-the-world event).");
        }

        Console.Error.WriteLine(
            $"Live release(s) in the registry: {string.Join(", ", otherLiveReleases)}.");
        Console.Error.WriteLine(
            "Follow the stop-the-world runbook (docs/on-prem-guide.md → \"Non-additive upgrade\"): " +
            "maintenance on → drain all slots → stop them → re-run this command with " +
            "--stop-the-world → start the new release → maintenance off.");
        return false;
    }

    /// <summary>
    /// Pending KrakenDb migrations that carry <see cref="StopTheWorldAttribute"/>,
    /// as (migration id, optional reason). Uses the context's own migrations
    /// assembly so the id → type mapping can never drift from EF's.
    /// </summary>
    public static async Task<IReadOnlyList<(string Id, string? Reason)>>
        GetMarkedPendingMigrationsAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new KrakenDbContext(options, new DefaultSpaceContext());

        var pending = (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).ToList();
        if (pending.Count == 0)
        {
            return [];
        }

        var byId = GetMigrationTypes(db);
        var marked = new List<(string, string?)>();
        foreach (var id in pending)
        {
            if (byId.TryGetValue(id, out var type)
                && type.GetCustomAttribute<StopTheWorldAttribute>() is { } attribute)
            {
                marked.Add((id, attribute.Reason));
            }
        }

        return marked;
    }

    /// <summary>Migration id → CLR type map for <paramref name="db"/>'s model.</summary>
    public static IReadOnlyDictionary<string, TypeInfo> GetMigrationTypes(DbContext db)
    {
#pragma warning disable EF1001 // IMigrationsAssembly.Migrations IS EF's own id → type map — reusing
        // it can never drift from what MigrateAsync will run, unlike a hand-rolled
        // public-API scan of the migrations assembly (MigrationAttribute is public,
        // but the assembly/filtering conventions around it are EF's to change).
        return db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>()
            .Migrations
            .ToDictionary(pair => pair.Key, pair => pair.Value);
#pragma warning restore EF1001
    }

    private static async Task<IReadOnlyList<string>> GetOtherLiveReleasesAsync(
        DeploymentTopology topology, string krakenConnectionString, IConfiguration configuration)
    {
        var connectionString = topology == DeploymentTopology.Saas
            ? configuration.GetConnectionString("Catalog")
            : krakenConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Saas without a catalog connection — the CLI can't see the registry.
            // Fail open here: setup on a tenant DB is the fleet-migrate escape
            // hatch and the fleet migrator carries its own coordination.
            return [];
        }

        // Saas: catalog connection, public schema, no own history table (the
        // catalog migration chain owns DDL there) — hence the parameterized
        // recipe rather than CreateOnPremOptions.
        var optionsBuilder = new DbContextOptionsBuilder<PlatformReleaseDbContext>();
        PlatformReleaseDbContext.ConfigureOptions(
            optionsBuilder, connectionString, ownSchema: topology != DeploymentTopology.Saas);
        await using var db = new PlatformReleaseDbContext(
            optionsBuilder.Options,
            new PlatformReleaseSchema(topology == DeploymentTopology.Saas
                ? null
                : PlatformReleaseSchema.OnPremSchemaName));

        // Own-release exclusion is STATUS-AWARE (the shared registry query): the
        // own Release:Id is exempt only while its row is still Deploying (the
        // release this upgrade is preparing). An Active/Draining own row is live —
        // `docker compose exec` into the serving slot must not make the guard
        // pass in single-live-release steady state.
        var ownReleaseId = configuration["Release:Id"];
        try
        {
            var live = await ReleaseRegistry
                .GetLiveReleasesExceptOwnDeployingAsync(db, ownReleaseId)
                .ConfigureAwait(false);
            return live
                .Select(r => $"{r.Id} (slot {r.SlotNo}, {r.Status}" +
                    $"{(r.Id == ownReleaseId ? "; this container's own release" : "")})")
                .ToList();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Registry tables don't exist yet — fresh install, nothing is live.
            return [];
        }
    }
}

/// <summary>
/// Version-checks the SHARED Hangfire storage schema (BG1/T4): slot boot no
/// longer auto-migrates it under the blue-green topologies
/// (<c>PrepareSchemaIfNecessary=false</c>), so <c>database upgrade</c>/<c>setup</c>
/// owns it — and must know whether this build would CHANGE it. The target version
/// is the highest embedded <c>Install.v{N}.sql</c> script in the loaded
/// Hangfire.PostgreSql assembly (no hardcoded number to drift); the installed
/// version is the <c>hangfire.schema</c> row.
/// </summary>
internal static partial class HangfireSchemaInspector
{
    [GeneratedRegex(@"Install\.v(\d+)\.sql$")]
    private static partial Regex InstallScriptVersion();

    public static int GetTargetSchemaVersion()
    {
        var assembly = typeof(global::Hangfire.PostgreSql.PostgreSqlStorage).Assembly;
        return assembly.GetManifestResourceNames()
            .Select(name => InstallScriptVersion().Match(name))
            .Where(match => match.Success)
            .Select(match => int.Parse(
                match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>Installed schema version, or null when Hangfire has never run here.</summary>
    public static async Task<int?> GetInstalledSchemaVersionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM hangfire.schema LIMIT 1";
        try
        {
            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return result is int version ? version : null;
        }
        catch (PostgresException ex) when (
            ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            return null;
        }
    }

    /// <summary>
    /// True when running the installer would CHANGE the shared schema. A schema
    /// that does not exist yet counts as pending (creating it beside a live
    /// release that predates it would still be a coordinated event). A target
    /// of 0 (the embedded-script discovery matched nothing — a Hangfire.PostgreSql
    /// packaging change) also counts as pending: unknown must FAIL CLOSED while
    /// another release is live, never wave the upgrade through.
    /// </summary>
    public static async Task<bool> IsUpgradePendingAsync(string connectionString)
    {
        var target = GetTargetSchemaVersion();
        if (target == 0)
        {
            return true;
        }

        var installed = await GetInstalledSchemaVersionAsync(connectionString).ConfigureAwait(false);
        return installed is null || installed.Value < target;
    }

    /// <summary>
    /// Creates/upgrades the Hangfire schema now (the blue-green topologies' only
    /// migration path for it — slot boot has <c>PrepareSchemaIfNecessary=false</c>).
    /// Constructing the storage with <c>PrepareSchemaIfNecessary=true</c> runs the
    /// installer synchronously.
    /// </summary>
    public static void EnsureSchema(string connectionString)
    {
        var storageOptions = new global::Hangfire.PostgreSql.PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
        };
        _ = new global::Hangfire.PostgreSql.PostgreSqlStorage(
            new global::Hangfire.PostgreSql.Factories.NpgsqlConnectionFactory(
                connectionString, storageOptions),
            storageOptions);
    }
}
