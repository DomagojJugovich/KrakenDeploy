using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Test helper that produces a <see cref="MaintenancePause"/> backed by
/// a real <see cref="MaintenanceModeService"/> pointed at the postgres
/// fixture. The fresh test DB has no maintenance row, so
/// <c>ShouldPauseAsync</c> returns false — every job's "off by default"
/// path stays exercised without mocking.
/// </summary>
internal static class NoopMaintenancePause
{
    public static MaintenancePause For(IDbContextFactory<KrakenDbContext> dbFactory)
        => new(new MaintenanceModeService(dbFactory, TimeProvider.System));
}
