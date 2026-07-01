using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Backup;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M13.G — <see cref="BackupService"/>'s settings
/// CRUD + the run-once persistence contract. The actual pg_dump call path
/// can't be exercised in unit tests (no separate Postgres install for
/// pg_dump to point at) — it's covered by <see cref="BackupEngineTests"/>'s
/// "engine returns failure result when pg_dump unavailable / connection
/// invalid" cases.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class BackupServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.BackupRuns.ExecuteDeleteAsync();
        await db.BackupSettings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Settings ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSettingsAsync_returns_defaults_when_no_row()
    {
        var svc = NewSvc();
        var settings = await svc.GetSettingsAsync();

        settings.TargetDirectory.Should().Be("backups",
            "the default keeps first-run convenience — operator sees a " +
            "pre-populated form instead of empty fields");
        settings.ScheduleEnabled.Should().BeFalse();
        settings.RetainLastN.Should().Be(14);
    }

    [Fact]
    public async Task UpsertSettingsAsync_creates_row_on_first_save_with_singleton_id()
    {
        var svc = NewSvc();
        var input = new BackupSettings
        {
            TargetDirectory = "/var/lib/kraken/backups",
            ScheduleEnabled = true,
            ScheduleCron    = "0 3 * * *",
            RetainLastN     = 7,
        };

        var saved = await svc.UpsertSettingsAsync(input);

        saved.Id.Should().Be(BackupSettings.SingletonId);
        saved.TargetDirectory.Should().Be("/var/lib/kraken/backups");
        saved.ScheduleEnabled.Should().BeTrue();
        saved.ScheduleCron.Should().Be("0 3 * * *");
        saved.RetainLastN.Should().Be(7);
    }

    [Fact]
    public async Task UpsertSettingsAsync_trims_strings_and_nulls_empty_cron()
    {
        var svc = NewSvc();
        var input = new BackupSettings
        {
            TargetDirectory = "  backups  ",
            ScheduleEnabled = false,
            ScheduleCron    = "   ",
            RetainLastN     = 10,
        };

        var saved = await svc.UpsertSettingsAsync(input);

        saved.TargetDirectory.Should().Be("backups");
        saved.ScheduleCron.Should().BeNull(
            "whitespace cron is semantically 'unset'; storing it as null " +
            "keeps the scheduler from registering a nonsense recurring job");
    }

    [Fact]
    public async Task UpsertSettingsAsync_rejects_empty_target_directory()
    {
        var svc = NewSvc();
        var act = async () => await svc.UpsertSettingsAsync(new BackupSettings
        {
            TargetDirectory = "",
        });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Target directory is required*");
    }

    [Fact]
    public async Task UpsertSettingsAsync_rejects_negative_retention()
    {
        var svc = NewSvc();
        var act = async () => await svc.UpsertSettingsAsync(new BackupSettings
        {
            TargetDirectory = "backups",
            RetainLastN     = -1,
        });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*RetainLastN must be 0*");
    }

    // ── RunOnceAsync persistence ──────────────────────────────────────────

    [Fact]
    public async Task RunOnceAsync_writes_a_failed_BackupRun_when_engine_fails()
    {
        // Force a deterministic failure regardless of whether pg_dump is
        // installed: hand the engine a malformed connection string so the
        // NpgsqlConnectionStringBuilder ctor in BackupEngine fails on the
        // very first line. Service must record this as a failed run.
        var svc = NewSvcWithBadConnection();

        await svc.UpsertSettingsAsync(new BackupSettings
        {
            TargetDirectory = Path.Combine(Path.GetTempPath(),
                $"kraken-backup-test-{Guid.NewGuid():N}"),
        });

        var run = await svc.RunOnceAsync("UnitTest");

        run.Outcome.Should().Be(BackupOutcome.Failed,
            "engine returns failure for a bad connection string; service " +
            "must persist that as a Failed run, not bubble an exception");
        run.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        run.StartedUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        run.CompletedUtc.Should().NotBeNull();
        run.Duration.Should().NotBeNull();
        run.TriggeredBy.Should().Be("UnitTest");
    }

    [Fact]
    public async Task RunOnceAsync_creates_one_row_per_call()
    {
        // Pin the "exactly one row per run" contract — the service inserts
        // an in-flight row before the engine call (so an operator who hits
        // the history page DURING a long backup sees something) and then
        // UPDATES that same row when the engine returns. A buggy refactor
        // that adds a second insert at the end would double-count runs.
        var svc = NewSvcWithBadConnection();
        await svc.UpsertSettingsAsync(new BackupSettings
        {
            TargetDirectory = Path.Combine(Path.GetTempPath(),
                $"kraken-backup-test-{Guid.NewGuid():N}"),
        });

        await svc.RunOnceAsync("UnitTest");

        await using var db = postgres.CreateContext();
        var rowCount = await db.BackupRuns.CountAsync();
        rowCount.Should().Be(1,
            "one insert-then-update path; two rows would mean a second " +
            "Add() slipped into the post-engine finalisation block");
    }

    // ── GetRecentRunsAsync / GetLastSuccessfulRunAsync ────────────────────

    [Fact]
    public async Task GetRecentRunsAsync_returns_newest_first()
    {
        var svc = NewSvc();
        await using (var db = postgres.CreateContext())
        {
            db.BackupRuns.AddRange(
                new BackupRun { StartedUtc = DateTimeOffset.UtcNow.AddDays(-3), TriggeredBy = "Schedule", Outcome = BackupOutcome.Success },
                new BackupRun { StartedUtc = DateTimeOffset.UtcNow.AddDays(-1), TriggeredBy = "User",     Outcome = BackupOutcome.Failed },
                new BackupRun { StartedUtc = DateTimeOffset.UtcNow,             TriggeredBy = "Schedule", Outcome = BackupOutcome.Success });
            await db.SaveChangesAsync();
        }

        var runs = await svc.GetRecentRunsAsync(take: 10);

        runs.Should().HaveCount(3);
        runs.Select(r => r.TriggeredBy).Should().Equal("Schedule", "User", "Schedule");
    }

    [Fact]
    public async Task GetRecentRunsAsync_caps_to_take()
    {
        var svc = NewSvc();
        await using (var db = postgres.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.BackupRuns.Add(new BackupRun
                {
                    StartedUtc  = DateTimeOffset.UtcNow.AddMinutes(-i),
                    TriggeredBy = "Test",
                    Outcome     = BackupOutcome.Success,
                });
            }
            await db.SaveChangesAsync();
        }

        var runs = await svc.GetRecentRunsAsync(take: 2);

        runs.Should().HaveCount(2,
            "the page shows the recent-N list; the query must honour the " +
            "take parameter to keep the row read bounded on long history");
    }

    [Fact]
    public async Task GetLastSuccessfulRunAsync_ignores_failed_rows()
    {
        var svc = NewSvc();
        await using (var db = postgres.CreateContext())
        {
            db.BackupRuns.AddRange(
                new BackupRun { StartedUtc = DateTimeOffset.UtcNow.AddHours(-2), TriggeredBy = "T", Outcome = BackupOutcome.Success, BundlePath = "/a" },
                new BackupRun { StartedUtc = DateTimeOffset.UtcNow.AddHours(-1), TriggeredBy = "T", Outcome = BackupOutcome.Failed,  ErrorMessage = "boom" });
            await db.SaveChangesAsync();
        }

        var last = await svc.GetLastSuccessfulRunAsync();

        last.Should().NotBeNull();
        last!.BundlePath.Should().Be("/a",
            "the newer FAILED row must not count as 'last successful' — " +
            "operators rely on this label to verify they have a working " +
            "restore point");
    }

    [Fact]
    public async Task GetLastSuccessfulRunAsync_returns_null_when_no_successes()
    {
        var svc = NewSvc();
        await using (var db = postgres.CreateContext())
        {
            db.BackupRuns.Add(new BackupRun
            {
                StartedUtc   = DateTimeOffset.UtcNow,
                TriggeredBy  = "T",
                Outcome      = BackupOutcome.Failed,
                ErrorMessage = "boom",
            });
            await db.SaveChangesAsync();
        }

        (await svc.GetLastSuccessfulRunAsync()).Should().BeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private BackupService NewSvc()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:KrakenDb"] = postgres.ConnectionString,
                ["Server:DataPath"]            = Path.Combine(Path.GetTempPath(), "kraken-data-empty"),
            })
            .Build();

        var engine = new BackupEngine(config,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            NullLogger<BackupEngine>.Instance, TimeProvider.System);
        return new BackupService(postgres, engine,
            NullLogger<BackupService>.Instance, TimeProvider.System);
    }

    /// <summary>
    /// Service wired with a deliberately bad connection string so
    /// <see cref="BackupEngine.RunAsync"/> fails fast — independent of
    /// whether pg_dump happens to be on the test host's PATH.
    /// </summary>
    private BackupService NewSvcWithBadConnection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Empty connection string = engine's "ConnectionStrings:KrakenDb
                // is not configured" early-out.
                ["ConnectionStrings:KrakenDb"] = "",
            })
            .Build();

        var engine = new BackupEngine(config,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            NullLogger<BackupEngine>.Instance, TimeProvider.System);
        return new BackupService(postgres, engine,
            NullLogger<BackupService>.Instance, TimeProvider.System);
    }
}
