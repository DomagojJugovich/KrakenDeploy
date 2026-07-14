using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Maintenance;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M13.A.3 — <see cref="MaintenanceModeService"/>
/// + <see cref="MaintenancePause"/>. The middleware's path-routing
/// logic is tested separately by exercising
/// <c>MaintenanceMiddleware.IsExemptPath</c> directly in the Server
/// test project.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class MaintenanceModeServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Set<Setting>().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetStateAsync_returns_Off_when_no_row()
    {
        var svc = NewSvc();

        var state = await svc.GetStateAsync();

        state.Should().Be(MaintenanceState.Off,
            "fresh installation must default to 'maintenance off' — " +
            "the gate is opt-in, the singleton row's absence is the safe state");
    }

    [Fact]
    public async Task EnableAsync_persists_and_invalidates_cache()
    {
        var svc = NewSvc();
        // Warm the cache with Off.
        await svc.GetStateAsync();

        await svc.EnableAsync("upgrading to v1.2", userId: Guid.NewGuid());

        // Immediate read must see the new state — without cache
        // invalidation the same-request UI button click would still
        // see Off and the page would look broken.
        var state = await svc.GetStateAsync();
        state.Enabled.Should().BeTrue();
        state.Reason.Should().Be("upgrading to v1.2");
        state.EnabledUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task DisableAsync_clears_reason_and_user()
    {
        var svc = NewSvc();
        await svc.EnableAsync("brief outage", userId: Guid.NewGuid());

        await svc.DisableAsync();

        var state = await svc.GetStateAsync();
        state.Enabled.Should().BeFalse();
        state.Reason.Should().BeNull(
            "disabling fully resets — a stale reason on the next enable " +
            "would mis-describe the new outage");
        state.EnabledByUserId.Should().BeNull();
        state.EnabledUtc.Should().BeNull();
    }

    [Fact]
    public async Task DisableAsync_is_safe_when_no_row_exists()
    {
        var svc = NewSvc();
        var act = async () => await svc.DisableAsync();
        await act.Should().NotThrowAsync(
            "first-disable on a fresh install (no row yet) must be a no-op");
    }

    [Fact]
    public async Task EnableAsync_trims_whitespace_reason()
    {
        var svc = NewSvc();
        await svc.EnableAsync("  trailing spaces  ", userId: null);

        var state = await svc.GetStateAsync();
        state.Reason.Should().Be("trailing spaces");
    }

    [Fact]
    public async Task EnableAsync_treats_blank_reason_as_null()
    {
        var svc = NewSvc();
        await svc.EnableAsync("   ", userId: null);

        var state = await svc.GetStateAsync();
        state.Reason.Should().BeNull(
            "whitespace-only reason is semantically 'no reason supplied'");
    }

    [Fact]
    public async Task EnableAsync_then_re_enable_overwrites_reason()
    {
        // Operator enables for upgrade, finishes, forgets to disable, then
        // re-enables for a different reason — the new reason must replace
        // the old one rather than appending or being ignored.
        var svc = NewSvc();
        await svc.EnableAsync("first window", userId: null);
        await svc.EnableAsync("second window", userId: null);

        var state = await svc.GetStateAsync();
        state.Reason.Should().Be("second window");
    }

    // ── MaintenancePause helper ────────────────────────────────────────────

    [Fact]
    public async Task MaintenancePause_returns_false_when_maintenance_off()
    {
        var svc = NewSvc();
        var pause = new MaintenancePause(svc);

        var should = await pause.ShouldPauseAsync(default);

        should.Should().BeFalse();
    }

    [Fact]
    public async Task MaintenancePause_returns_true_when_maintenance_on()
    {
        var svc = NewSvc();
        await svc.EnableAsync("test", userId: null);
        var pause = new MaintenancePause(svc);

        var should = await pause.ShouldPauseAsync(default);

        should.Should().BeTrue();
    }

    [Fact]
    public async Task MaintenancePause_logs_when_pausing()
    {
        // Pin the operator-visibility contract — a paused job MUST log
        // something so an operator scanning Hangfire's dashboard sees
        // "job X paused" rather than silent no-ops.
        var svc = NewSvc();
        await svc.EnableAsync("Upgrading", userId: null);
        var pause = new MaintenancePause(svc);
        var logger = new CountingLogger();

        await pause.ShouldPauseAsync(default, logger, "kraken.backup");

        logger.InfoCount.Should().BeGreaterThanOrEqualTo(1);
        logger.LastMessage.Should().Contain("paused by maintenance")
            .And.Contain("kraken.backup");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private MaintenanceModeService NewSvc()
        => new(new SettingsService(postgres.ScopeFactory, TimeProvider.System), TimeProvider.System);

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<MaintenancePause>
    {
        public int InfoCount { get; private set; }
        public string? LastMessage { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information)
            {
                InfoCount++;
                LastMessage = formatter(state, exception);
            }
        }
    }
}
