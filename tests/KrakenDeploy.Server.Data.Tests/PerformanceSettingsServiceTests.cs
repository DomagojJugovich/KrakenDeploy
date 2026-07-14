using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Performance;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for <see cref="PerformanceSettingsService"/> — M13.F.3
/// singleton entity that backs the <c>/configuration/performance</c> page
/// + the retention jobs + the Hangfire worker-count read at startup.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class PerformanceSettingsServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Set<Setting>().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetAsync_returns_defaults_when_no_row()
    {
        // Fresh install: no row yet, the service must still return a
        // usable object so consumers (Hangfire worker count, retention
        // jobs) can read sensible defaults before the operator visits
        // the page.
        var svc = NewSvc();

        var settings = await svc.GetAsync();

        settings.HangfireWorkerCount.Should().Be(
            PerformanceSettings.DefaultHangfireWorkerCount);
        settings.SlowDeploymentThresholdMinutes.Should().Be(
            PerformanceSettings.DefaultSlowDeploymentThresholdMinutes);
        settings.SlowStepThresholdMinutes.Should().Be(
            PerformanceSettings.DefaultSlowStepThresholdMinutes);
        settings.AuditLogRetentionDays.Should().Be(
            PerformanceSettings.DefaultAuditLogRetentionDays);
        settings.AiCallLogRetentionDays.Should().Be(
            PerformanceSettings.DefaultAiCallLogRetentionDays);
    }

    [Fact]
    public async Task SaveAsync_persists_and_invalidates_cache()
    {
        var svc = NewSvc();
        // Warm the cache with defaults.
        await svc.GetAsync();

        await svc.SaveAsync(new PerformanceSettings
        {
            HangfireWorkerCount            = 12,
            SlowDeploymentThresholdMinutes = 45,
            SlowStepThresholdMinutes       = 7,
            AuditLogRetentionDays          = 730,
            AiCallLogRetentionDays         = 30,
        });

        // Immediate read must see the new state — without cache
        // invalidation a same-request UI Save+reload would still show
        // stale defaults and the page would look broken.
        var fresh = await svc.GetAsync();
        fresh.HangfireWorkerCount.Should().Be(12);
        fresh.SlowDeploymentThresholdMinutes.Should().Be(45);
        fresh.SlowStepThresholdMinutes.Should().Be(7);
        fresh.AuditLogRetentionDays.Should().Be(730);
        fresh.AiCallLogRetentionDays.Should().Be(30);
    }

    [Fact]
    public async Task SaveAsync_persists_EmbedOfflineRunner_toggle()
    {
        // Regression guard: SaveAsync previously copied every field EXCEPT
        // EmbedOfflineRunner, so the GUI toggle silently never persisted.
        var svc = NewSvc();
        (await svc.GetAsync()).EmbedOfflineRunner.Should().BeTrue("default is true");

        await svc.SaveAsync(new PerformanceSettings
        {
            EmbedOfflineRunner = false,
        });
        (await svc.GetAsync()).EmbedOfflineRunner.Should().BeFalse("the OFF toggle must persist");

        await svc.SaveAsync(new PerformanceSettings
        {
            EmbedOfflineRunner = true,
        });
        (await svc.GetAsync()).EmbedOfflineRunner.Should().BeTrue("the ON toggle must persist");
    }

    [Fact]
    public async Task SaveAsync_overwrites_existing_row()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new PerformanceSettings
        {
            HangfireWorkerCount = 8,
        });
        await svc.SaveAsync(new PerformanceSettings
        {
            HangfireWorkerCount = 16,
        });

        var fresh = await svc.GetAsync();
        fresh.HangfireWorkerCount.Should().Be(16,
            "second save replaces the first; we must not duplicate-row " +
            "the singleton");

        // Only one row exists in the DB after two saves.
        await using var db = postgres.CreateContext();
        var count = await db.Set<Setting>().CountAsync(s => s.Key == PerformanceSettings.Key);
        count.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_returns_row_when_present()
    {
        // Seed the document via a separate SettingsService instance (bypassing
        // the service-under-test) so we pin the GetAsync read path independently
        // of the SaveAsync write. NewSvc() below has a cold cache.
        await new SettingsService(postgres.ScopeFactory, TimeProvider.System)
            .SaveAsync(new PerformanceSettings
            {
                HangfireWorkerCount            = 24,
                SlowDeploymentThresholdMinutes = 60,
                SlowStepThresholdMinutes       = 15,
                AuditLogRetentionDays          = 1825, // 5 years
                AiCallLogRetentionDays         = 14,
            });

        var svc = NewSvc();
        var settings = await svc.GetAsync();

        settings.HangfireWorkerCount.Should().Be(24);
        settings.AuditLogRetentionDays.Should().Be(1825,
            "operators in regulated jurisdictions need a multi-year " +
            "retention window without code changes");
    }

    [Fact]
    public async Task Defaults_are_sensible()
    {
        // Pin the documented defaults so a future contributor doesn't
        // accidentally change the bootstrap behaviour by tweaking a
        // const. These values are referenced in docs + plan.
        PerformanceSettings.DefaultHangfireWorkerCount.Should().Be(4);
        PerformanceSettings.DefaultSlowDeploymentThresholdMinutes.Should().Be(30);
        PerformanceSettings.DefaultSlowStepThresholdMinutes.Should().Be(10);
        PerformanceSettings.DefaultAuditLogRetentionDays.Should().Be(365);
        PerformanceSettings.DefaultAiCallLogRetentionDays.Should().Be(90);
        PerformanceSettings.DefaultEmbedOfflineRunner.Should().BeTrue();

        await Task.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private PerformanceSettingsService NewSvc()
        => new(new SettingsService(postgres.ScopeFactory, TimeProvider.System));
}
