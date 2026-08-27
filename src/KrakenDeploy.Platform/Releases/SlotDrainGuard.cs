using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Platform.Releases;

/// <summary>
/// Answers "is the release THIS instance runs Draining/Retired?" — see
/// <see cref="SlotDrainGuard"/>. Interface exists so the deployment worker's
/// drain gate (BG1 item 10) can be unit-tested without a registry database.
/// </summary>
public interface ISlotDrainGuard
{
    Task<bool> IsOwnReleaseDrainingAsync(CancellationToken ct = default);
}

/// <summary>
/// Answers "is the release THIS instance runs Draining/Retired?" from the
/// release registry (cached), keyed by the instance's own <c>Release:Id</c>. Used
/// to take a draining slot out of new background work (docs/blue-green-slot-deployment.md
/// §8 step 6: a Draining release receives no new work) — all slot instances share
/// one Hangfire storage, so without this a draining slot keeps competing for new
/// jobs and can starve its own drain indefinitely. Since BG1 it also gates the
/// deployment worker's claim loop (a draining slot must stop CLAIMING, not just
/// stop taking Hangfire jobs).
/// <para>
/// Fail-open: with no <c>Release:Id</c> configured (not a slotted deployment) or
/// an unreadable registry, the answer is <c>false</c> — running work beats
/// stalling the fleet on a registry blip.
/// </para>
/// </summary>
public sealed class SlotDrainGuard(
    IDbContextFactory<PlatformReleaseDbContext> platformFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<SlotDrainGuard> logger) : ISlotDrainGuard
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
            await using var db = await platformFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var status = await db.AppReleases
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
