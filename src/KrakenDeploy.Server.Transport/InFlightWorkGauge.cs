namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Process-local count of deployment/runbook dispatches this instance is
/// currently orchestrating. This is the "in-flight deployment count" a slot
/// reports for blue-green draining (docs/blue-green-slot-deployment.md §5/§9):
/// a Draining release may only be Retired when its slots report zero in-flight
/// work — and a running deployment is <b>never</b> force-killed.
/// <para>
/// Deliberately in-process (not a DB count): the shared database would count the
/// whole fleet's work, but drain-retire needs to know whether <i>this instance</i>
/// still owns orchestration state (pending sub-plan TCSes, wave loops).
/// </para>
/// </summary>
public sealed class InFlightWorkGauge
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Tracks one dispatch; dispose when the dispatch completes (use in a <c>using</c>).</summary>
    public IDisposable Track()
    {
        Interlocked.Increment(ref _count);
        return new Tracker(this);
    }

    private sealed class Tracker(InFlightWorkGauge gauge) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref gauge._count);
            }
        }
    }
}
