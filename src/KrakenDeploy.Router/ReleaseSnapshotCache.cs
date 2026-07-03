using Microsoft.Extensions.Options;
using Npgsql;

namespace KrakenDeploy.Router;

/// <summary>
/// Cached view of the two catalog tables the router reads
/// (<c>platform_settings.current_default_release</c> + live <c>app_releases</c>).
/// Short TTL + explicit invalidation (§5 of the design). <b>Degrades stale, not
/// down:</b> once any snapshot exists, requests NEVER block on the catalog —
/// at most one request at a time attempts a refresh (try-acquire) while all
/// others serve the last-good snapshot immediately, and a failed refresh
/// suppresses re-attempts briefly so a catalog outage costs one connection
/// attempt per back-off window, not one per request. Only a cold start with an
/// unreachable catalog throws (and the router health endpoint goes unhealthy).
/// </summary>
public sealed class ReleaseSnapshotCache(
    NpgsqlDataSource catalog,
    IOptions<RouterOptions> options,
    TimeProvider timeProvider,
    ILogger<ReleaseSnapshotCache> logger) : IDisposable
{
    /// <summary>Re-attempt suppression after a failed refresh (serve-stale window).</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile RouterSnapshot? _snapshot;
    private DateTimeOffset _freshUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _suppressRetryUntil = DateTimeOffset.MinValue;
    private int _generation;

    public void Dispose() => _refreshGate.Dispose();

    /// <summary>
    /// Marks the cached snapshot stale so the next request re-reads the catalog.
    /// The generation bump also voids any refresh already in flight — its result
    /// (read before the flip this invalidation announces) won't be marked fresh.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _freshUntil = DateTimeOffset.MinValue;
        _suppressRetryUntil = DateTimeOffset.MinValue;
    }

    public async ValueTask<RouterSnapshot> GetAsync(CancellationToken ct = default)
    {
        var cached = _snapshot;
        var now = timeProvider.GetUtcNow();
        if (cached is not null && (now < _freshUntil || now < _suppressRetryUntil))
        {
            return cached;
        }

        if (cached is not null)
        {
            // Stale-but-usable: never block behind another request's refresh.
            // Try-acquire — if someone else is already refreshing, serve stale.
            if (!await _refreshGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            {
                return cached;
            }
        }
        else
        {
            // Cold start: nothing to serve, so waiting on the gate is correct.
            await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            // Double-check under the gate — another request may have refreshed.
            cached = _snapshot;
            now = timeProvider.GetUtcNow();
            if (cached is not null && (now < _freshUntil || now < _suppressRetryUntil))
            {
                return cached;
            }

            var generationAtRead = Volatile.Read(ref _generation);
            try
            {
                var fresh = await ReadSnapshotAsync(ct).ConfigureAwait(false);
                _snapshot = fresh;
                if (Volatile.Read(ref _generation) == generationAtRead)
                {
                    _freshUntil = timeProvider.GetUtcNow()
                        + TimeSpan.FromSeconds(Math.Max(1, options.Value.CacheTtlSeconds));
                }

                // else: an Invalidate() raced this read — the data may predate the
                // flip it announced. Keep the snapshot usable but already-stale so
                // the next request re-reads immediately.
                return fresh;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (cached is not null)
                {
                    // Serve stale + back off: routing continuity beats freshness
                    // during a blip, and the back-off keeps a catalog outage from
                    // costing a connection attempt per request.
                    _suppressRetryUntil = timeProvider.GetUtcNow() + FailureBackoff;
                    logger.LogWarning(
                        ex, "Catalog refresh failed; serving the last-good release snapshot " +
                            "(retry suppressed for {Backoff}s).", FailureBackoff.TotalSeconds);
                    return cached;
                }

                throw;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<RouterSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        await using var conn = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);

        string? defaultReleaseId;
        await using (var cmd = new NpgsqlCommand(
            "SELECT value FROM platform_settings WHERE key = 'current_default_release'", conn))
        {
            defaultReleaseId = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        var live = new Dictionary<string, RouterReleaseEntry>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            // 3 = Retired; everything else is routable when explicitly pinned.
            "SELECT id, slot_no, status FROM app_releases WHERE status <> 3", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                live[id] = new RouterReleaseEntry(id, reader.GetInt16(1), reader.GetInt32(2));
            }
        }

        return new RouterSnapshot(defaultReleaseId, live);
    }
}
