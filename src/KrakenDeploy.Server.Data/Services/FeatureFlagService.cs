using KrakenDeploy.Server.Core.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Read/write surface for per-instance feature toggles (M13.F.1).
/// Caches override state in-memory with a short TTL so hot paths
/// (page renders, Hangfire job pre-checks) don't pay the DB hit on
/// every call. Cache invalidates immediately on <see cref="SetAsync"/>
/// so a UI toggle takes effect at once for the saving process; other
/// processes pick up the change within the TTL window.
/// </summary>
public sealed class FeatureFlagService(
    IServiceScopeFactory scopeFactory,
    IFeatureCatalog catalog,
    TimeProvider time)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private Dictionary<string, bool>? _overrides;
    private DateTimeOffset _refreshedAt;

    /// <summary>
    /// Returns the effective state for <paramref name="key"/> — DB override
    /// when present, catalogue default otherwise. Unknown keys throw so
    /// typos at call sites fail loudly instead of silently returning false.
    /// </summary>
    public async Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var descriptor = catalog.Find(key)
            ?? throw new InvalidOperationException(
                $"Feature '{key}' is not registered in IFeatureCatalog. " +
                "Check the spelling or add an entry to BuiltInFeatureCatalog.");

        var overrides = await GetOverridesAsync(ct).ConfigureAwait(false);
        return overrides.TryGetValue(key, out var explicitState)
            ? explicitState
            : descriptor.DefaultEnabled;
    }

    /// <summary>
    /// Returns one <see cref="FeatureState"/> per catalogue entry, with the
    /// effective enabled state + a flag indicating whether the value comes
    /// from a DB override or the catalogue default. The page uses this to
    /// render a subtle "(default)" hint next to unchanged toggles.
    /// </summary>
    public async Task<List<FeatureState>> GetAllAsync(CancellationToken ct = default)
    {
        var overrides = await GetOverridesAsync(ct).ConfigureAwait(false);
        return [.. catalog.All.Select(d =>
            new FeatureState(
                Descriptor: d,
                Enabled:    overrides.TryGetValue(d.Key, out var v) ? v : d.DefaultEnabled,
                IsOverride: overrides.ContainsKey(d.Key)))];
    }

    /// <summary>
    /// Persists an explicit override. When the requested state matches the
    /// catalogue default, deletes the override row instead — keeps the
    /// table clean so an operator who toggles "off then back on" doesn't
    /// leave a redundant row behind.
    /// </summary>
    public async Task SetAsync(string key, bool enabled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var descriptor = catalog.Find(key)
            ?? throw new InvalidOperationException(
                $"Feature '{key}' is not registered in IFeatureCatalog.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var row = await db.FeatureFlags
            .FirstOrDefaultAsync(f => f.Key == key, ct)
            .ConfigureAwait(false);

        if (enabled == descriptor.DefaultEnabled)
        {
            // Back to default — remove the override row entirely. Keeps
            // the table free of "explicit-but-redundant" entries; useful
            // when scanning for "what's been changed from defaults".
            if (row is not null)
            {
                db.FeatureFlags.Remove(row);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            if (row is null)
            {
                row = new FeatureFlag { Key = key, Enabled = enabled };
                db.FeatureFlags.Add(row);
            }
            else
            {
                row.Enabled = enabled;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Invalidate the cache so the next read (likely from the same
        // request) sees the new value.
        Invalidate();
    }

    /// <summary>
    /// Drop the cache. Mostly used by tests + the SetAsync write path.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate) { _overrides = null; }
    }

    private async Task<Dictionary<string, bool>> GetOverridesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_overrides is not null && (now - _refreshedAt) < CacheTtl)
            {
                return _overrides;
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var rows = await db.FeatureFlags
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var fresh = rows.ToDictionary(r => r.Key, r => r.Enabled);

        lock (_gate)
        {
            _overrides = fresh;
            _refreshedAt = time.GetUtcNow();
            return _overrides;
        }
    }
}

/// <summary>One row in the page's grid — effective state + provenance.</summary>
public sealed record FeatureState(
    FeatureDescriptor Descriptor,
    bool Enabled,
    bool IsOverride);
