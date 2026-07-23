using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Interceptors;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Process-wide singleton owning the ONE PostgreSQL container the whole test
/// assembly shares. Migrations run exactly once into a template database; each
/// <see cref="PostgresFixture"/> (one per test class) then clones a fresh
/// database from that template via <c>CREATE DATABASE ... TEMPLATE</c> — a
/// ~100 ms operation with no migrations.
/// <para>
/// Why: the previous design started a fresh container and ran the full migration
/// set for every one of the ~78 container-backed test classes (78
/// create/migrate/destroy cycles per run). That churn overwhelmed Docker
/// Desktop's daemon/port-proxy — intermittently aborting connections mid-query
/// (<c>WSAECONNABORTED</c>) or timing out container startup — and leaked
/// containers (a graveyard of 1000+) that degraded the daemon further. One
/// shared container removes the churn and makes the suite faster.
/// </para>
/// </summary>
internal static class SharedPostgres
{
    private const string TemplateDb = "kraken_template";

    private static readonly PostgreSqlContainer Container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        // Default/maintenance DB: admin CREATE/DROP DATABASE commands connect here
        // (you cannot create or drop the database you are connected to).
        .WithDatabase("postgres")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static bool _ready;

    /// <summary>Starts the container (once) and migrates the template DB (once).
    /// Idempotent + safe under concurrent first-callers.</summary>
    public static async Task EnsureReadyAsync()
    {
        if (_ready) { return; }
        await InitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ready) { return; }

            await Container.StartAsync().ConfigureAwait(false);

            await ExecAdminAsync($"CREATE DATABASE \"{TemplateDb}\";").ConfigureAwait(false);

            // Migrate the template with pooling OFF so no connection lingers to it —
            // CREATE DATABASE ... TEMPLATE requires the source to have no sessions.
            var templateConn = ConnStringFor(TemplateDb, pooling: false);
            await using (var ctx = BuildContext(templateConn))
            {
                await ctx.Database.MigrateAsync().ConfigureAwait(false);
            }
            NpgsqlConnection.ClearAllPools();

            _ready = true;
        }
        finally
        {
            InitGate.Release();
        }
    }

    /// <summary>Clones a fresh, pristine database from the migrated template and
    /// returns (database name, connection string).</summary>
    public static async Task<(string Name, string ConnectionString)> CreateFreshDatabaseAsync()
    {
        await EnsureReadyAsync().ConfigureAwait(false);
        var name = "kd_" + Guid.NewGuid().ToString("N");
        await ExecAdminAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDb}\";")
            .ConfigureAwait(false);
        return (name, ConnStringFor(name, pooling: true));
    }

    /// <summary>Drops a cloned database. WITH (FORCE) terminates any pooled
    /// connections still open to it (PostgreSQL 13+).</summary>
    public static async Task DropDatabaseAsync(string name)
    {
        await ExecAdminAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE);")
            .ConfigureAwait(false);
    }

    private static async Task ExecAdminAsync(string sql)
    {
        // Non-pooled admin connection to the maintenance DB. CREATE/DROP DATABASE
        // cannot run inside a transaction, so use a raw command (no EF wrapping).
        await using var conn = new NpgsqlConnection(ConnStringFor("postgres", pooling: false));
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string ConnStringFor(string database, bool pooling)
    {
        var builder = new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = database,
            Pooling = pooling,
        };
        return builder.ConnectionString;
    }

    internal static KrakenDbContext BuildContext(
        string connectionString, bool enableRetryOnFailure = false)
    {
        var spaceContext = new DefaultSpaceContext();
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            // enableRetryOnFailure mirrors the WEB HOST (Program.cs) so a test can
            // exercise the NpgsqlRetryingExecutionStrategy path — specifically that
            // TryClaimAsync's user-initiated transaction runs THROUGH the execution
            // strategy (a bare BeginTransactionAsync would throw under retry).
            .UseNpgsql(connectionString, npgsql =>
            {
                if (enableRetryOnFailure)
                {
                    npgsql.EnableRetryOnFailure();
                }
            })
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                new AuditableEntityInterceptor(TimeProvider.System),
                new TagApplicationCleanupInterceptor(),
                new EnvironmentReferenceCleanupInterceptor(),
                new RoleAssignmentScopeCleanupInterceptor(),
                new SpaceScopingInterceptor(spaceContext))
            // EF Core 9+ promotes PendingModelChangesWarning to an error by default.
            // Lambda-based value converters (jsonb HasConversion) cannot be perfectly
            // round-tripped through the migration snapshot, so the runtime comparison
            // always flags a diff even though the schema is correct.
            // "dotnet ef migrations has-pending-model-changes" confirms no real drift.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new KrakenDbContext(options, spaceContext);
    }
}

/// <summary>
/// Per-test-class fixture (xUnit <c>IClassFixture</c>). Each class gets its own
/// freshly-cloned database on the shared container, so isolation is identical to
/// the old container-per-class design — tests obtain a fresh
/// <see cref="KrakenDbContext"/> via <see cref="CreateContext"/>, and the fixture
/// itself doubles as an <see cref="IDbContextFactory{TContext}"/>.
/// <para>
/// All classes carry <c>[Collection("Postgres")]</c> (see
/// <see cref="PostgresCollectionDefinition"/>) so the per-class clone/drop runs
/// serially — no concurrent <c>CREATE DATABASE ... TEMPLATE</c> contention.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime, IDbContextFactory<KrakenDbContext>
{
    private readonly ServiceProvider _scopeProvider;
    private string _dbName = "";
    private string _connectionString = "";

    public PostgresFixture()
    {
        // Mirrors the production registration enough for the singleton services
        // that open a per-call scope: each scope resolves a fresh KrakenDbContext
        // via the fixture's factory. The scoped context resolves lazily (during a
        // test), after InitializeAsync has set the connection string.
        var services = new ServiceCollection();
        services.AddScoped<KrakenDbContext>(_ => CreateContext());
        services.AddSingleton<IDbContextFactory<KrakenDbContext>>(this);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<KrakenDeploy.Server.Data.Services.SettingsService>();
        _scopeProvider = services.BuildServiceProvider();
    }

    public string ConnectionString => _connectionString;

    /// <summary>
    /// An <see cref="IServiceScopeFactory"/> whose scopes resolve a fresh
    /// <see cref="KrakenDbContext"/>. Pass this to services that open a per-call
    /// scope instead of capturing an <see cref="IDbContextFactory{TContext}"/>.
    /// </summary>
    public IServiceScopeFactory ScopeFactory =>
        _scopeProvider.GetRequiredService<IServiceScopeFactory>();

    public KrakenDbContext CreateContext() => SharedPostgres.BuildContext(_connectionString);

    /// <summary>A context whose Npgsql provider has <c>EnableRetryOnFailure</c>
    /// (the web-host configuration), for tests that must exercise the retrying
    /// execution strategy rather than the default non-retrying one.</summary>
    public KrakenDbContext CreateRetryingContext() =>
        SharedPostgres.BuildContext(_connectionString, enableRetryOnFailure: true);

    public async Task InitializeAsync()
    {
        (_dbName, _connectionString) = await SharedPostgres.CreateFreshDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _scopeProvider.DisposeAsync();
        // Drop this class's cloned database; the shared container lives for the
        // whole run and is reaped by Testcontainers' Ryuk at process exit.
        if (!string.IsNullOrEmpty(_dbName))
        {
            await SharedPostgres.DropDatabaseAsync(_dbName);
        }
    }

    KrakenDbContext IDbContextFactory<KrakenDbContext>.CreateDbContext() => CreateContext();
}
