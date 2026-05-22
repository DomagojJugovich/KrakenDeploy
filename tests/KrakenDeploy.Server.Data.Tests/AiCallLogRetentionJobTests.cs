using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Data.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M13.F.4 — <see cref="AiCallLogRetentionJob"/>.
/// Runs against the shared Postgres fixture so the global query filter,
/// the IgnoreQueryFilters override, and the actual ExecuteDeleteAsync
/// generation all exercise.
/// </summary>
[Collection("Postgres")]
public sealed class AiCallLogRetentionJobTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.AiCallLogs.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Deletes_rows_older_than_default_retention_window()
    {
        var time = TimeProvider.System;
        // Seed: one row 91 days old (past 90-day default), one row 30 days old.
        await SeedAsync(time, daysAgo: 91, "should-be-deleted");
        await SeedAsync(time, daysAgo: 30, "should-survive");

        await NewJob(time).ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var survivors = await db.AiCallLogs.IgnoreQueryFilters().ToListAsync();
        survivors.Should().ContainSingle();
        survivors[0].Feature.Should().Be("should-survive");
    }

    [Fact]
    public async Task Honours_configured_retention_window()
    {
        var time = TimeProvider.System;
        await SeedAsync(time, daysAgo: 8,  "older-than-7");
        await SeedAsync(time, daysAgo: 5,  "fresher-than-7");

        // Operator override: keep only the last 7 days.
        await NewJob(time, retentionDays: "7").ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var survivors = await db.AiCallLogs.IgnoreQueryFilters().ToListAsync();
        survivors.Should().ContainSingle();
        survivors[0].Feature.Should().Be("fresher-than-7");
    }

    [Fact]
    public async Task Zero_retention_disables_purging()
    {
        var time = TimeProvider.System;
        await SeedAsync(time, daysAgo: 999, "ancient-but-kept");

        // 0 disables — operator opts into "keep all rows forever" for
        // forensic / regulatory reasons. The default 90 is overridable.
        await NewJob(time, retentionDays: "0").ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var survivors = await db.AiCallLogs.IgnoreQueryFilters().ToListAsync();
        survivors.Should().HaveCount(1,
            "retention 0 means 'never purge' — the ancient row stays");
    }

    [Fact]
    public async Task Negative_retention_disables_purging()
    {
        var time = TimeProvider.System;
        await SeedAsync(time, daysAgo: 365, "old-but-kept");

        await NewJob(time, retentionDays: "-1").ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.AiCallLogs.IgnoreQueryFilters().CountAsync()).Should().Be(1,
            "negative retention is treated the same as zero — disable, don't crash");
    }

    [Fact]
    public async Task Returns_silently_when_no_rows_match()
    {
        // Empty table, no error.
        var act = async () => await NewJob(TimeProvider.System).ExecuteAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Cross_space_purge_runs_through_IgnoreQueryFilters()
    {
        // Pins the contract that retention purges across ALL Spaces, not
        // just the ambient one. The job uses IgnoreQueryFilters() to bypass
        // the global ISpaceScoped restriction.
        var time = TimeProvider.System;
        var spaceA = WellKnown.DefaultSpaceId;
        var spaceB = Guid.NewGuid();

        await SeedAsync(time, daysAgo: 100, "space-a-old",  spaceId: spaceA);
        await SeedAsync(time, daysAgo: 100, "space-b-old",  spaceId: spaceB);

        await NewJob(time).ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var survivors = await db.AiCallLogs.IgnoreQueryFilters().CountAsync();
        survivors.Should().Be(0,
            "retention purges across every Space — the foreign-Space row " +
            "would survive if the job forgot to IgnoreQueryFilters");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private AiCallLogRetentionJob NewJob(
        TimeProvider time, string? retentionDays = null)
    {
        var configValues = new Dictionary<string, string?>();
        if (retentionDays is not null)
        {
            configValues[AiCallLogRetentionJob.RetentionDaysConfigKey] = retentionDays;
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
        return new AiCallLogRetentionJob(
            postgres,
            config,
            time,
            NullLogger<AiCallLogRetentionJob>.Instance);
    }

    private async Task SeedAsync(
        TimeProvider time, int daysAgo, string feature, Guid? spaceId = null)
    {
        // Insert first; the AuditableEntityInterceptor stamps CreatedUtc to
        // "now" on every insert (unconditionally — see Interceptors/
        // AuditableEntityInterceptor.cs). Then UPDATE the column to the past
        // via ExecuteUpdateAsync, which bypasses the interceptor (it only
        // fires for Added entries).
        var pastTimestamp = time.GetUtcNow().AddDays(-daysAgo);
        var row = new AiCallLog
        {
            SpaceId          = spaceId ?? WellKnown.DefaultSpaceId,
            Provider         = "Anthropic",
            Model            = "claude-sonnet-4.6",
            Feature          = feature,
            PromptTokens     = 100,
            CompletionTokens = 50,
            LatencyMs        = 200,
            CostUsd          = 0.001m,
            Success          = true,
        };

        await using var db = postgres.CreateContext();
        db.AiCallLogs.Add(row);
        await db.SaveChangesAsync();

        // Push the timestamp into the past for retention testing.
        await db.AiCallLogs
            .IgnoreQueryFilters()
            .Where(x => x.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, pastTimestamp));
    }
}
