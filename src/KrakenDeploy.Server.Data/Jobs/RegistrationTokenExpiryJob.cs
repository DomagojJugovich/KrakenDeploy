using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: clears one-time registration tokens that have
/// passed their TTL without being consumed.
/// <para>
/// The token hash is stored on <c>DeploymentTarget.RegistrationKeyHash</c>.
/// Consumed tokens are cleared immediately on use; this job handles the case
/// where an admin generated a token but never installed the agent.
/// Runs daily.
/// </para>
/// </summary>
public sealed class RegistrationTokenExpiryJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    TimeProvider time,
    ILogger<RegistrationTokenExpiryJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var now = time.GetUtcNow();

        // Use ExecuteUpdateAsync to avoid loading entities into memory.
        // IgnoreQueryFilters — token expiry is space-agnostic.
        var count = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .Where(t => t.RegistrationKeyHash != null
                     && t.RegistrationTokenExpiresUtc != null
                     && t.RegistrationTokenExpiresUtc < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RegistrationKeyHash, (string?)null)
                .SetProperty(t => t.RegistrationTokenExpiresUtc, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);

        if (count > 0)
        {
            logger.LogInformation(
                "RegistrationTokenExpiry: cleared {Count} expired token(s).",
                count);
        }
    }
}
