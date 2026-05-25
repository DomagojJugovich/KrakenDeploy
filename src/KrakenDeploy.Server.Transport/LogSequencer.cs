using KrakenDeploy.Server.Core.Domain.Deployments;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Serialises read-modify-write of <see cref="Deployment.NextLogSequence"/>
/// so the orchestrator can hand out unique log-entry sequence numbers
/// from multiple concurrent code paths inside a single deployment dispatch.
///
/// <para>
/// <strong>Why this exists:</strong> the M14.2/M14.3 helpers each read
/// <c>deployment.NextLogSequence++</c> when appending a log entry. Today
/// the loop is single-threaded per deployment so that's safe. M14.4
/// introduces wave-parallel step execution within a single deployment;
/// two parallel waves writing log entries would race the unguarded
/// post-increment and assign the same sequence to two rows. This carrier
/// closes that gap proactively — one <see cref="LogSequencer"/> per
/// deployment dispatch, shared across every helper that needs a sequence.
/// </para>
///
/// <para>
/// The lock is held only for the duration of the post-increment (a few
/// nanoseconds). It is NOT held across the DB SaveChanges, so concurrent
/// SaveChanges calls writing different rows still parallelise — only the
/// sequence assignment is serialised.
/// </para>
/// </summary>
public sealed class LogSequencer(Deployment deployment)
{
    private readonly object _gate = new();
    private readonly Deployment _deployment = deployment;

    /// <summary>
    /// Atomically reads the current sequence value, increments it, and
    /// returns the pre-increment value. Equivalent to the pre-M14.3.1
    /// <c>deployment.NextLogSequence++</c> idiom under a lock.
    /// </summary>
    public int Next()
    {
        lock (_gate)
        {
            return _deployment.NextLogSequence++;
        }
    }
}
