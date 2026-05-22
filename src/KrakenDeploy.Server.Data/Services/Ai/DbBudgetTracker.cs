using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai;

/// <summary>
/// EF-backed <see cref="IBudgetTracker"/> (Phase M11.A.5). Sums the current
/// Space's <c>AiCallLog.CostUsd</c> over the current UTC calendar month so
/// the wrapper can refuse calls when the monthly cap is reached.
/// </summary>
/// <remarks>
/// <para>
/// The global query filter on <c>AiCallLog</c> already restricts reads to
/// the current Space, so the sum naturally attributes to whoever's budget
/// gets enforced. The filter uses <c>ISpaceContext.CurrentSpaceId</c>;
/// background jobs need <c>WithSpace(spaceId)</c> before the call.
/// </para>
/// <para>
/// Performance: the (<c>SpaceId</c>, <c>CreatedUtc</c>) index added in
/// M11.A.3 makes this a covering index scan in Postgres. For instances
/// with millions of AI calls per Space per month, consider materialising
/// a per-Space monthly rollup in a cron — but that's premature for v1.
/// </para>
/// </remarks>
public sealed class DbBudgetTracker(
    IDbContextFactory<KrakenDbContext> dbFactory,
    ISpaceContext                       spaceContext,
    ILogger<DbBudgetTracker>            logger)
    : IBudgetTracker
{
    public async ValueTask<decimal> GetMonthToDateUsdAsync(CancellationToken ct = default)
    {
        // No ambient Space → no budget context. Return zero so the wrapper's
        // gate is effectively disabled for background-job paths that haven't
        // wired a Space explicitly (Hangfire workers that forgot to
        // WithSpace, MCP tool calls outside any Space). The audit row still
        // gets written; only enforcement is skipped.
        if (spaceContext.CurrentSpaceId == Guid.Empty)
        {
            return 0m;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Current UTC calendar month.
            var now           = DateTimeOffset.UtcNow;
            var startOfMonth  = new DateTimeOffset(
                now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

            // SUM(cost_usd). The global query filter narrows to the current
            // Space; we just add the time-window predicate.
            return await db.AiCallLogs
                .Where(x => x.CreatedUtc >= startOfMonth)
                .SumAsync(x => (decimal?)x.CostUsd, ct)
                .ConfigureAwait(false) ?? 0m;
        }
        catch (Exception ex)
        {
            // The wrapper treats a tracker failure as "no budget data" and
            // allows the call. Better to fail open than to block the
            // user-facing AI feature on a transient DB issue — the audit
            // row still captures the call's actual cost.
            logger.LogError(ex,
                "Budget tracker failed to read MTD spend for Space {SpaceId}; assuming $0.",
                spaceContext.CurrentSpaceId);
            return 0m;
        }
    }
}
