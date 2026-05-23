using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Small helper recurring Hangfire jobs call at the top of their handler:
/// <c>if (await pause.ShouldPauseAsync(ct, logger)) return;</c>.
///
/// <para>
/// Centralised so the maintenance-pause semantics stay consistent across
/// every job — same cache window (10 s), same log message shape, same
/// "treat unreachable maintenance service as 'not in maintenance'"
/// fail-open posture. The fail-open is deliberate: a DB blip during
/// maintenance shouldn't pause backups indefinitely.
/// </para>
/// </summary>
public sealed class MaintenancePause(MaintenanceModeService maintenance)
{
    /// <summary>
    /// Returns true if the recurring job should short-circuit because
    /// the instance is in maintenance mode. Logs a single info-level
    /// message when pausing so an operator scanning the Hangfire
    /// dashboard sees "job X paused by maintenance" instead of silent
    /// no-ops.
    /// </summary>
    public async Task<bool> ShouldPauseAsync(
        CancellationToken ct, ILogger? logger = null, string? jobName = null)
    {
        try
        {
            var state = await maintenance.GetStateAsync(ct).ConfigureAwait(false);
            if (state.Enabled)
            {
                logger?.LogInformation(
                    "Hangfire job {Job} paused by maintenance mode ({Reason}).",
                    jobName ?? "(unknown)", state.Reason ?? "no reason");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Fail-open: a brief outage of the maintenance lookup
            // shouldn't keep critical jobs paused. The middleware fails
            // closed for the same lookup; that's intentional asymmetry
            // — gating HTTP is a security boundary, gating Hangfire is
            // a courtesy.
            logger?.LogWarning(ex,
                "Maintenance-pause check failed for job {Job} — proceeding.",
                jobName ?? "(unknown)");
            return false;
        }
    }
}
