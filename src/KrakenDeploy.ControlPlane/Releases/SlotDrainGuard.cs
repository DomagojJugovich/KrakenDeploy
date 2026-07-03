using KrakenDeploy.ControlPlane.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.ControlPlane.Releases;

/// <summary>
/// Answers "is the release THIS instance runs Draining/Retired?" from the
/// catalog (cached), keyed by the instance's own <c>Release:Id</c>. Used to take
/// a draining slot out of new background work (docs/blue-green-slot-deployment.md
/// §8 step 6: a Draining release receives no new work) — all slot instances share
/// one Hangfire storage, so without this a draining slot keeps competing for new
/// jobs and can starve its own drain indefinitely.
/// <para>
/// Fail-open: with no <c>Release:Id</c> configured (not a slotted deployment) or
/// an unreadable catalog, the answer is <c>false</c> — running work beats
/// stalling the fleet on a catalog blip.
/// </para>
/// </summary>
public sealed class SlotDrainGuard(
    IDbContextFactory<CatalogDbContext> catalogFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<SlotDrainGuard> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    private bool _cached;
    private DateTimeOffset _freshUntil = DateTimeOffset.MinValue;

    public async Task<bool> IsOwnReleaseDrainingAsync(CancellationToken ct = default)
    {
        var releaseId = configuration["Release:Id"];
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            return false;
        }

        if (timeProvider.GetUtcNow() < _freshUntil)
        {
            return _cached;
        }

        try
        {
            await using var catalog = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var status = await catalog.AppReleases
                .AsNoTracking()
                .Where(r => r.Id == releaseId)
                .Select(r => (AppReleaseStatus?)r.Status)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            _cached = status is AppReleaseStatus.Draining or AppReleaseStatus.Retired;
            _freshUntil = timeProvider.GetUtcNow() + CacheTtl;
            return _cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Could not read own release status ({ReleaseId}); assuming not draining.",
                releaseId);
            return _cached;
        }
    }
}
