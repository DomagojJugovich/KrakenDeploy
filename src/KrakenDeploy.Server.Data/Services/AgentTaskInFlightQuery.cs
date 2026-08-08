using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// The FAIL-CLOSED predicate behind <c>GET /api/agents/task-in-flight</c>: does the server
/// still have work for this target?
/// <para>
/// Extracted from the endpoint lambda because it is the single most consequential boolean the
/// agent consumes — a "no" is licence to replace its whole install directory and exit — and
/// while it lived inline it had no test at all. Deleting the <c>!</c> from the terminal check
/// left the entire suite green, and the agent then takes a false "idle" as permission to swap
/// mid-plan.
/// </para>
/// </summary>
public static class AgentTaskInFlightQuery
{
    /// <summary>
    /// True when any task assigned to <paramref name="targetId"/> is NOT in a terminal state.
    /// <para>
    /// Three properties are load-bearing, and each is the fail-CLOSED direction:
    /// </para>
    /// <list type="bullet">
    ///   <item>NEGATION of the terminal set, never an enumeration of the in-flight ones. The
    ///     two are equivalent for today's statuses and diverge the moment a non-terminal
    ///     status is added — in the dangerous direction, because an enumeration answers a
    ///     confident "idle" for a status it has never heard of.</item>
    ///   <item><c>IgnoreQueryFilters</c>. This runs on an agent-authenticated request with no
    ///     user and no Space context; a Space filter would answer "idle" for work in a Space
    ///     the request cannot see, which is the fail-OPEN this exists to prevent.</item>
    ///   <item><c>Queued</c> counts as in flight. <c>InFlightAfterClaim</c> is deliberately
    ///     NOT usable here — it is the narrower F1 slot-holding set and excludes
    ///     <c>Queued</c> — but an unclaimed task can be dispatched at any moment, so a swap
    ///     started now would race its first wave.</item>
    /// </list>
    /// <para>
    /// <c>AnyAsync</c> rather than a count: only the boolean is consumed, and a target
    /// accumulates one assignment row per task that ever touched it, so counting scans an
    /// unbounded history to learn one bit.
    /// </para>
    /// </summary>
    public static Task<bool> AnyNonTerminalForTargetAsync(
        this KrakenDbContext db, Guid targetId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Set<TaskTargetAssignment>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(
                a => a.TargetId == targetId
                     && !DeploymentStatusExtensions.Terminal.Contains(a.Task.Status),
                ct);
    }
}
