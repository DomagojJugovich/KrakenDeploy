using FluentAssertions;
using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.ControlPlane.Releases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Blue-green release-registry lifecycle against a real catalog database
/// (docs/blue-green-slot-deployment.md §2/§4/§8): register → flip → drain →
/// retire, plus the invariants that keep routing sane (one live release per
/// slot, never retire the default, drained releases never come back).
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReleaseRegistryTests : IAsyncLifetime, IDbContextFactory<CatalogDbContext>
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("kraken_catalog_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    private ReleaseRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var catalog = CreateDbContext();
        await catalog.Database.MigrateAsync();
        _registry = new ReleaseRegistry(
            this, TimeProvider.System, NullLogger<ReleaseRegistry>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public CatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CatalogDbContext(options);
    }

    [Fact]
    public async Task Full_lifecycle_register_flip_drain_retire()
    {
        // Register + flip the first release: it becomes the Active default.
        await _registry.RegisterAsync("rel-a", "v1", 1);
        await _registry.FlipDefaultAsync("rel-a", TimeSpan.FromHours(24));

        var snapshot = await _registry.GetSnapshotAsync();
        snapshot.DefaultReleaseId.Should().Be("rel-a");
        snapshot.Releases.Single(r => r.Id == "rel-a").Status.Should().Be(AppReleaseStatus.Active);

        // Deploy the next release into a free slot; flipping demotes the previous
        // default to Draining with a deadline.
        await _registry.RegisterAsync("rel-b", "v2", 2);
        await _registry.FlipDefaultAsync("rel-b", TimeSpan.FromHours(24));

        snapshot = await _registry.GetSnapshotAsync();
        snapshot.DefaultReleaseId.Should().Be("rel-b");
        var relA = snapshot.Releases.Single(r => r.Id == "rel-a");
        relA.Status.Should().Be(AppReleaseStatus.Draining);
        relA.DrainDeadlineUtc.Should().NotBeNull();

        // Retire the drained release: slot 1 is free again for the next deploy.
        await _registry.RetireAsync("rel-a");
        snapshot = await _registry.GetSnapshotAsync();
        snapshot.Releases.Single(r => r.Id == "rel-a").Status.Should().Be(AppReleaseStatus.Retired);
        snapshot.Releases.Single(r => r.Id == "rel-a").DrainedAtUtc.Should().NotBeNull();

        await _registry.RegisterAsync("rel-c", "v3", 1);
        (await _registry.GetSnapshotAsync()).Releases
            .Single(r => r.Id == "rel-c").Status.Should().Be(AppReleaseStatus.Deploying);
    }

    [Fact]
    public async Task Register_refuses_duplicate_id_and_occupied_slot()
    {
        await _registry.RegisterAsync("dup", "v1", 3);

        // Ids are immutable history.
        var dup = () => _registry.RegisterAsync("dup", "v1 again", 4);
        await dup.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already registered*");

        // A slot hosts at most one non-Retired release (runbook step 0).
        var occupied = () => _registry.RegisterAsync("other", "v2", 3);
        await occupied.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*occupied*");

        // Retiring the never-flipped (Deploying) release frees the slot — the
        // failed-health-gate rollback path.
        await _registry.RetireAsync("dup");
        await _registry.RegisterAsync("other", "v2", 3);
    }

    [Fact]
    public async Task Retire_refuses_the_active_default_and_flip_refuses_drained()
    {
        await _registry.RegisterAsync("keep", "v1", 5);
        await _registry.FlipDefaultAsync("keep", TimeSpan.FromHours(1));

        // Never retire the release new sessions land on.
        var retireDefault = () => _registry.RetireAsync("keep");
        await retireDefault.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Active default*");

        // Demote it by flipping to a successor, then try to flip BACK — a
        // drained release never becomes the default again (register a new id).
        await _registry.RegisterAsync("next", "v2", 6);
        await _registry.FlipDefaultAsync("next", TimeSpan.FromHours(1));

        var flipBack = () => _registry.FlipDefaultAsync("keep", TimeSpan.FromHours(1));
        await flipBack.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot become the default again*");
    }

    [Fact]
    public async Task Flip_is_idempotent_for_the_current_default()
    {
        await _registry.RegisterAsync("only", "v1", 7);
        await _registry.FlipDefaultAsync("only", TimeSpan.FromHours(1));
        await _registry.FlipDefaultAsync("only", TimeSpan.FromHours(1));

        var snapshot = await _registry.GetSnapshotAsync();
        snapshot.DefaultReleaseId.Should().Be("only");
        snapshot.Releases.Single(r => r.Id == "only").Status.Should().Be(AppReleaseStatus.Active);
    }
}
