using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Orchestrator-side log writer for a single task dispatch. Routes every
/// orchestrator log line through the SHARED DB-atomic sequencer
/// (<see cref="TaskLogService"/>) — the same path the agent and the server-side
/// script runner use — so parallel waves / multi-target fan-out can never take
/// duplicate sequence numbers (the pre-unification in-memory
/// <c>NextLogSequence++</c> race is gone: decision 9).
/// <para>
/// Each append runs in its own short-lived DI scope so concurrent branches never
/// contend on the dispatch's main <see cref="KrakenDbContext"/>. One instance per
/// task dispatch, shared across every orchestrator helper.
/// </para>
/// </summary>
public sealed class LogSequencer(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    Guid taskId)
{
    /// <summary>
    /// Append one orchestrator log line via the shared sequencer.
    /// <paramref name="stepIndex"/> = -1 for a task-level banner (no bound step);
    /// <paramref name="targetId"/> = null when not bound to a specific target.
    /// Returns the allocated sequence.
    /// </summary>
    public async Task<int> AppendAsync(
        int stepIndex, Guid? targetId, string level, string message, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        return await TaskLogService.AppendLiveAsync(
            db, taskId, stepIndex, targetId, level, message, timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }
}
