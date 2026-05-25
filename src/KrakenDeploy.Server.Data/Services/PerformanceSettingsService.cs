using KrakenDeploy.Server.Core.Domain.Performance;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Read + write surface for the instance-wide performance / retention knobs
/// (M13.F.3). Singleton-row pattern with an in-memory cache + invalidate-on-write
/// — same shape as <see cref="MaintenanceModeService"/>.
///
/// <para>
/// Read sites: <c>Program.cs</c> (Hangfire worker count at startup),
/// <c>AuditRetentionJob</c> / <c>AiCallLogRetentionJob</c> (retention windows
/// per run), and <c>DeploymentWorker</c> (slow-deployment thresholds per
/// dispatch). All of those happen across distinct DbContext scopes so the
/// service caches the snapshot here rather than every consumer caching
/// their own copy.
/// </para>
///
/// <para>
/// Precedence for retention windows: <c>PerformanceSettings.*RetentionDays</c>
/// (DB) wins when a row exists; on a fresh install with no row yet, the
/// retention jobs fall back to <c>Retention:AuditLogDays</c> /
/// <c>Retention:AiCallLogDays</c> in <c>appsettings.json</c>, then to the
/// hardcoded defaults. That keeps existing operator config working during
/// upgrade — the page Save just materialises the row.
/// </para>
/// </summary>
public sealed class PerformanceSettingsService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    TimeProvider time)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private PerformanceSettings? _cached;
    private DateTimeOffset _refreshedAt;

    /// <summary>
    /// Returns the current snapshot. When no row exists yet (first run),
    /// returns a transient instance carrying default values — useful for
    /// reads that need a value before the operator has visited the page.
    /// </summary>
    public async Task<PerformanceSettings> GetAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } cached && (now - _refreshedAt) < CacheTtl)
            {
                return cached;
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.PerformanceSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == PerformanceSettings.SingletonId, ct)
            .ConfigureAwait(false);

        var fresh = row ?? new PerformanceSettings { Id = PerformanceSettings.SingletonId };

        lock (_gate)
        {
            _cached = fresh;
            _refreshedAt = time.GetUtcNow();
            return _cached;
        }
    }

    /// <summary>
    /// Persists the supplied settings as the singleton row. Caller is
    /// responsible for input validation (the page's edit form enforces
    /// non-negative ints + a sensible upper bound).
    /// </summary>
    public async Task SaveAsync(PerformanceSettings update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.PerformanceSettings
            .FirstOrDefaultAsync(p => p.Id == PerformanceSettings.SingletonId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new PerformanceSettings { Id = PerformanceSettings.SingletonId };
            db.PerformanceSettings.Add(row);
        }

        row.HangfireWorkerCount            = update.HangfireWorkerCount;
        row.SlowDeploymentThresholdMinutes = update.SlowDeploymentThresholdMinutes;
        row.SlowStepThresholdMinutes       = update.SlowStepThresholdMinutes;
        row.AuditLogRetentionDays          = update.AuditLogRetentionDays;
        row.AiCallLogRetentionDays         = update.AiCallLogRetentionDays;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateCache();
    }

    public void InvalidateCache()
    {
        lock (_gate) { _cached = null; }
    }
}
