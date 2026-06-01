using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Features;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for the M13.F.1 feature-flag store. The cache layer
/// is exercised end-to-end via the real Postgres fixture so a refactor
/// that breaks the "set + immediately read" invariant (the UI relies on
/// it) gets caught here.
/// </summary>
[Collection("Postgres")]
public sealed class FeatureFlagServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.FeatureFlags.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── IsEnabledAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task IsEnabledAsync_returns_catalog_default_when_no_override()
    {
        var svc = NewSvc();

        // feeds.step-template-catalog default is true in the BuiltInFeatureCatalog.
        (await svc.IsEnabledAsync("feeds.step-template-catalog")).Should().BeTrue();

        // steps.allow-unsigned-packages default is false.
        (await svc.IsEnabledAsync("steps.allow-unsigned-packages")).Should().BeFalse();
    }

    [Fact]
    public async Task IsEnabledAsync_returns_override_when_present()
    {
        var svc = NewSvc();

        await svc.SetAsync("feeds.step-template-catalog", enabled: false);

        (await svc.IsEnabledAsync("feeds.step-template-catalog")).Should().BeFalse();
    }

    [Fact]
    public async Task IsEnabledAsync_throws_for_unknown_key()
    {
        // Typos at call sites must fail loudly rather than silently
        // returning false — a missing audit row is worse than a
        // runtime exception that points at the bad key.
        var svc = NewSvc();

        var act = async () => await svc.IsEnabledAsync("does.not.exist");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not registered*");
    }

    // ── SetAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_creates_row_when_value_differs_from_default()
    {
        var svc = NewSvc();

        await svc.SetAsync("feeds.step-template-catalog", enabled: false);

        await using var db = postgres.CreateContext();
        var rows = await db.FeatureFlags.ToListAsync();
        rows.Should().ContainSingle()
            .Which.Key.Should().Be("feeds.step-template-catalog");
    }

    [Fact]
    public async Task SetAsync_removes_row_when_value_returns_to_default()
    {
        // The clean-on-default contract: toggling off-then-on must NOT
        // leave a redundant "on (=default)" override row behind. Without
        // this, an operator who experiments with a toggle leaves a trail
        // of orphan rows in the table.
        var svc = NewSvc();

        await svc.SetAsync("feeds.step-template-catalog", enabled: false);
        await svc.SetAsync("feeds.step-template-catalog", enabled: true); // back to default

        await using var db = postgres.CreateContext();
        (await db.FeatureFlags.CountAsync()).Should().Be(0,
            "toggling back to the catalogue default must delete the override row");
    }

    [Fact]
    public async Task SetAsync_does_not_create_row_when_value_matches_default()
    {
        // No-op write: setting a fresh flag to its already-default value
        // shouldn't create a redundant row.
        var svc = NewSvc();

        await svc.SetAsync("feeds.step-template-catalog", enabled: true); // default is true

        await using var db = postgres.CreateContext();
        (await db.FeatureFlags.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SetAsync_throws_for_unknown_key()
    {
        var svc = NewSvc();
        var act = async () => await svc.SetAsync("does.not.exist", true);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SetAsync_invalidates_cache_for_immediate_read()
    {
        // The UI does Set → fetch GetAllAsync in the same code path; the
        // fetch MUST see the new value, not the cached old one.
        var svc = NewSvc();

        // Warm the cache.
        await svc.IsEnabledAsync("feeds.step-template-catalog");

        await svc.SetAsync("feeds.step-template-catalog", enabled: false);

        (await svc.IsEnabledAsync("feeds.step-template-catalog"))
            .Should().BeFalse("the Set+read sequence must skip the cache");
    }

    // ── GetAllAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllAsync_returns_one_state_per_catalog_entry()
    {
        var svc = NewSvc();
        var catalog = new BuiltInFeatureCatalog();

        var states = await svc.GetAllAsync();

        states.Should().HaveCount(catalog.All.Count,
            "the page renders one row per catalogue entry; missing rows " +
            "would silently hide a feature toggle from the UI");
    }

    [Fact]
    public async Task GetAllAsync_marks_override_state_correctly()
    {
        var svc = NewSvc();
        await svc.SetAsync("feeds.step-template-catalog", enabled: false);

        var states = await svc.GetAllAsync();

        var overridden = states.Single(s => s.Descriptor.Key == "feeds.step-template-catalog");
        overridden.IsOverride.Should().BeTrue();
        overridden.Enabled.Should().BeFalse();

        var defaulted = states.Single(s => s.Descriptor.Key == "feeds.step-package-catalog");
        defaulted.IsOverride.Should().BeFalse();
        defaulted.Enabled.Should().Be(defaulted.Descriptor.DefaultEnabled);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private FeatureFlagService NewSvc() =>
        new(postgres.ScopeFactory, new BuiltInFeatureCatalog(), TimeProvider.System);
}
