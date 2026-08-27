namespace KrakenDeploy.Platform.Releases;

/// <summary>
/// The pure drain-and-retire rule (docs/blue-green-slot-deployment.md §9),
/// separated from the watcher so it is trivially unit-testable:
/// <list type="bullet">
/// <item>an in-flight deployment is <b>never</b> abandoned — any in-flight work
/// blocks retirement, even past the deadline;</item>
/// <item>zero circuits + zero in-flight → retire;</item>
/// <item>idle circuits past <c>drain_deadline</c> → retire (stragglers re-pin to
/// the default on their next request).</item>
/// </list>
/// </summary>
public static class ReleaseDrainDecision
{
    public static bool ShouldRetire(
        DateTimeOffset now,
        DateTimeOffset? drainDeadline,
        int activeCircuits,
        int inFlightDeployments)
    {
        if (inFlightDeployments > 0)
        {
            return false;
        }

        if (activeCircuits == 0)
        {
            return true;
        }

        return drainDeadline is not null && now > drainDeadline;
    }
}
