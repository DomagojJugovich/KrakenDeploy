using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Performance;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M13.F.3 / M13.F.5 — <see cref="AuditRetentionJob"/>
/// gated by both the <c>audit.purge-enabled</c> feature flag (master
/// kill-switch) and the DB-or-config retention day-count.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AuditRetentionJobTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.AuditEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.PerformanceSettings.ExecuteDeleteAsync();
        await db.FeatureFlags.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Purges_rows_older_than_default_window()
    {
        var time = TimeProvider.System;
        await SeedAsync(time, daysAgo: 400, "should-be-deleted");
        await SeedAsync(time, daysAgo: 200, "should-survive");

        await NewJob(time).ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var survivors = await db.AuditEntries.IgnoreQueryFilters().ToListAsync();
        survivors.Should().ContainSingle();
        survivors[0].EventType.Should().Be("should-survive");
    }

    [Fact]
    public async Task Feature_flag_off_short_circuits_purge()
    {
        // M13.F.5 master kill-switch: even with a non-zero day-count, the
        // job MUST NOT delete anything when the feature flag is off. Lets
        // operators pause GDPR retention without losing the day count.
        var time = TimeProvider.System;
        await SeedAsync(time, daysAgo: 9999, "ancient-but-kept");

        await using (var db = postgres.CreateContext())
        {
            db.FeatureFlags.Add(new KrakenDeploy.Server.Core.Domain.Features.FeatureFlag
            {
                Key     = AuditRetentionJob.PurgeEnabledFeatureKey,
                Enabled = false,
            });
            await db.SaveChangesAsync();
        }

        await NewJob(time).ExecuteAsync(CancellationToken.None);

        await using var verify = postgres.CreateContext();
        var survivors = await verify.AuditEntries.IgnoreQueryFilters().CountAsync();
        survivors.Should().Be(1,
            "kill-switch must override the day-count; the ancient row stays");
    }

    [Fact]
    public async Task DB_backed_retention_wins_over_appsettings()
    {
        var time = TimeProvider.System;
        await SeedAsync(time, daysAgo: 10, "older-than-7");
        await SeedAsync(time, daysAgo: 5,  "fresher-than-7");

        await using (var db = postgres.CreateContext())
        {
            db.PerformanceSettings.Add(new PerformanceSettings
            {
                Id                    = PerformanceSettings.SingletonId,
                AuditLogRetentionDays = 7,
            });
            await db.SaveChangesAsync();
        }

        // Appsettings says 999 (would keep everything) — DB-backed 7 must win.
        await NewJob(time, configRetentionDays: "999").ExecuteAsync(CancellationToken.None);

        await using var verify = postgres.CreateContext();
        var survivors = await verify.AuditEntries.IgnoreQueryFilters().ToListAsync();
        survivors.Should().ContainSingle();
        survivors[0].EventType.Should().Be("fresher-than-7");
    }

    [Fact]
    public async Task Appsettings_used_when_no_DB_row()
    {
        // Fresh install — no PerformanceSettings row yet; appsettings is the
        // bootstrap path. Existing operators on the upgrade boundary keep
        // their config working until they visit the page.
        var time = TimeProvider.System;
        await SeedAsync(time, daysAgo: 50, "older-than-30");
        await SeedAsync(time, daysAgo: 15, "fresher-than-30");

        await NewJob(time, configRetentionDays: "30").ExecuteAsync(CancellationToken.None);

        await using var verify = postgres.CreateContext();
        var survivors = await verify.AuditEntries.IgnoreQueryFilters().ToListAsync();
        survivors.Should().ContainSingle();
        survivors[0].EventType.Should().Be("fresher-than-30");
    }

    [Fact]
    public async Task Zero_retention_disables_purging()
    {
        var time = TimeProvider.System;
        await SeedAsync(time, daysAgo: 9999, "ancient");

        await using (var db = postgres.CreateContext())
        {
            db.PerformanceSettings.Add(new PerformanceSettings
            {
                Id                    = PerformanceSettings.SingletonId,
                AuditLogRetentionDays = 0,
            });
            await db.SaveChangesAsync();
        }

        await NewJob(time).ExecuteAsync(CancellationToken.None);

        await using var verify = postgres.CreateContext();
        (await verify.AuditEntries.IgnoreQueryFilters().CountAsync()).Should().Be(1,
            "0 days means 'never purge' — operators opting out for forensic " +
            "reasons must keep the audit table intact");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private AuditRetentionJob NewJob(
        TimeProvider time, string? configRetentionDays = null)
    {
        var configValues = new Dictionary<string, string?>();
        if (configRetentionDays is not null)
        {
            configValues[AuditRetentionJob.RetentionDaysConfigKey] = configRetentionDays;
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var httpAccessor = new HttpContextAccessor();
        var spaceCtx = new KrakenDeploy.Server.Data.Spaces.DefaultSpaceContext();
        var auditLog = new AuditLogService(postgres, httpAccessor, spaceCtx, time);
        var performance = new PerformanceSettingsService(postgres.ScopeFactory, time);
        var featureFlags = new FeatureFlagService(
            postgres.ScopeFactory,
            new KrakenDeploy.Server.Core.Domain.Features.BuiltInFeatureCatalog(),
            time);

        return new AuditRetentionJob(
            auditLog,
            postgres,
            performance,
            featureFlags,
            config,
            NullLogger<AuditRetentionJob>.Instance);
    }

    private async Task SeedAsync(
        TimeProvider time, int daysAgo, string eventType)
    {
        var occurred = time.GetUtcNow().AddDays(-daysAgo);
        await using var db = postgres.CreateContext();
        db.AuditEntries.Add(new AuditEntry
        {
            EventType   = eventType,
            OccurredUtc = occurred,
            UserDisplay = "test",
            SpaceId     = WellKnown.DefaultSpaceId,
        });
        await db.SaveChangesAsync();
    }
}
