using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Interceptors;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Spins up a PostgreSQL container once per test class (xUnit IClassFixture)
/// and applies KrakenDeploy migrations during initialization. Tests obtain
/// a fresh <see cref="KrakenDbContext"/> via <see cref="CreateContext"/>, and
/// the fixture itself can be passed wherever an
/// <see cref="IDbContextFactory{TContext}"/> is required.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime, IDbContextFactory<KrakenDbContext>
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("krakendeploy_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    private readonly ServiceProvider _scopeProvider;

    public PostgresFixture()
    {
        // Mirrors the production registration enough for the singleton
        // services that now open a per-call scope: each scope resolves a
        // fresh KrakenDbContext via the fixture's factory.
        var services = new ServiceCollection();
        services.AddScoped<KrakenDbContext>(_ => CreateContext());
        services.AddSingleton<IDbContextFactory<KrakenDbContext>>(this);
        _scopeProvider = services.BuildServiceProvider();
    }

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// An <see cref="IServiceScopeFactory"/> whose scopes resolve a fresh
    /// <see cref="KrakenDbContext"/>. Pass this to services that open a
    /// per-call scope instead of capturing an <see cref="IDbContextFactory{TContext}"/>.
    /// </summary>
    public IServiceScopeFactory ScopeFactory =>
        _scopeProvider.GetRequiredService<IServiceScopeFactory>();

    public KrakenDbContext CreateContext()
    {
        var spaceContext = new DefaultSpaceContext();
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                new AuditableEntityInterceptor(TimeProvider.System),
                new TagApplicationCleanupInterceptor(),
                new SpaceScopingInterceptor(spaceContext))
            // EF Core 9 promotes PendingModelChangesWarning to an error by default.
            // Lambda-based value converters (like our jsonb HasConversion) cannot be
            // perfectly round-tripped through the migration snapshot, so the runtime
            // comparison always flags a diff even though the schema is correct.
            // "dotnet ef migrations has-pending-model-changes" confirms no real drift.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new KrakenDbContext(options, spaceContext);
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _scopeProvider.DisposeAsync();
        await _container.DisposeAsync();
    }

    KrakenDbContext IDbContextFactory<KrakenDbContext>.CreateDbContext() => CreateContext();
}
