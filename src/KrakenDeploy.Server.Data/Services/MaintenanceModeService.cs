using KrakenDeploy.Server.Core.Domain.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Read + write surface for the instance-wide maintenance flag.
///
/// <para>
/// The middleware hits <see cref="GetStateAsync"/> once per incoming
/// request, so the state is cached in-memory with a short TTL.
/// Writes (<see cref="EnableAsync"/> / <see cref="DisableAsync"/>)
/// invalidate the cache so the gate takes effect within the same
/// request, not after a TTL window.
/// </para>
/// </summary>
public sealed class MaintenanceModeService(
    IServiceScopeFactory scopeFactory,
    TimeProvider time)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private MaintenanceState? _cached;
    private DateTimeOffset _refreshedAt;

    /// <summary>Cached snapshot of the singleton row. Returns the "off"
    /// default when no row exists yet (first run).</summary>
    public async Task<MaintenanceState> GetStateAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } cached && (now - _refreshedAt) < CacheTtl)
            {
                return cached;
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var row = await db.MaintenanceSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == MaintenanceSettings.SingletonId, ct)
            .ConfigureAwait(false);

        var fresh = row is null
            ? MaintenanceState.Off
            : new MaintenanceState(row.Enabled, row.Reason, row.EnabledByUserId, row.EnabledUtc);

        lock (_gate)
        {
            _cached = fresh;
            _refreshedAt = time.GetUtcNow();
            return _cached;
        }
    }

    public async Task EnableAsync(string? reason, Guid? userId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var row = await db.MaintenanceSettings
            .FirstOrDefaultAsync(m => m.Id == MaintenanceSettings.SingletonId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new MaintenanceSettings { Id = MaintenanceSettings.SingletonId };
            db.MaintenanceSettings.Add(row);
        }
        row.Enabled         = true;
        row.Reason          = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        row.EnabledByUserId = userId;
        row.EnabledUtc      = time.GetUtcNow();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateCache();
    }

    public async Task DisableAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var row = await db.MaintenanceSettings
            .FirstOrDefaultAsync(m => m.Id == MaintenanceSettings.SingletonId, ct)
            .ConfigureAwait(false);
        if (row is null) { return; }

        row.Enabled         = false;
        row.Reason          = null;
        row.EnabledByUserId = null;
        row.EnabledUtc      = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateCache();
    }

    public void InvalidateCache()
    {
        lock (_gate) { _cached = null; }
    }
}

/// <summary>Immutable snapshot of the maintenance flag — what the
/// middleware and the page bind against.</summary>
public sealed record MaintenanceState(
    bool Enabled,
    string? Reason,
    Guid? EnabledByUserId,
    DateTimeOffset? EnabledUtc)
{
    public static readonly MaintenanceState Off = new(false, null, null, null);
}
