namespace KrakenDeploy.Router;

/// <summary>
/// The pure release→slot routing decision (§3/§6 of the design):
/// <list type="bullet">
/// <item>a pin naming a <b>live</b> release routes to that release's slot.
/// <c>Deploying</c> (pre-health-gate) releases are reachable ONLY via the
/// explicit <c>X-KD-Release</c> header — the operator health-gate path — never
/// via a browser cookie, so anonymous cookie tampering cannot reach a build
/// that has not passed its gate;</item>
/// <item>no pin, or a pin naming a Retired/unknown release, routes to the
/// <c>current_default_release</c> and (re)issues the pin;</item>
/// <item>no routable default → <c>null</c> (the caller answers 503).</item>
/// </list>
/// </summary>
public static class SlotRouteDecider
{
    private const int DeployingStatus = 0;

    public static RouteDecision? Decide(RouterSnapshot snapshot, PinExtraction pin)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pin);

        if (pin.Value is not null
            && snapshot.LiveReleases.TryGetValue(pin.Value, out var pinned)
            && (pinned.Status != DeployingStatus || pin.FromHeader))
        {
            return new RouteDecision(pinned.ReleaseId, pinned.SlotNo, IssuePin: false);
        }

        if (snapshot.DefaultReleaseId is not null
            && snapshot.LiveReleases.TryGetValue(snapshot.DefaultReleaseId, out var def))
        {
            // IssuePin: the request either carried no pin or a dead one — (re)pin
            // it to the default so the session stays on this release from now on.
            return new RouteDecision(def.ReleaseId, def.SlotNo, IssuePin: true);
        }

        // No default, or the default points at a Retired/unknown release
        // (operator error the registry service guards against). Fail loudly.
        return null;
    }
}

/// <summary>Outcome of a routing decision.</summary>
/// <param name="ReleaseId">Release the request is routed to.</param>
/// <param name="SlotNo">Slot hosting that release.</param>
/// <param name="IssuePin">Whether to (re)issue the version cookie on the response.</param>
public sealed record RouteDecision(string ReleaseId, short SlotNo, bool IssuePin);
