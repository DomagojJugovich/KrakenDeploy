namespace KrakenDeploy.Router;

/// <summary>
/// Point-in-time view of the release registry as the router needs it: the
/// default pointer plus every non-Retired ("live") release. Retired/unknown
/// release ids are simply absent — a pin to one falls back to the default.
/// </summary>
public sealed record RouterSnapshot(
    string? DefaultReleaseId,
    IReadOnlyDictionary<string, RouterReleaseEntry> LiveReleases)
{
    public static RouterSnapshot Empty { get; } =
        new(null, new Dictionary<string, RouterReleaseEntry>(StringComparer.Ordinal));
}

/// <summary>One live (non-Retired) release: which slot it occupies.</summary>
/// <param name="ReleaseId">Opaque release id (cookie/header value).</param>
/// <param name="SlotNo">Slot the release runs in (1-based).</param>
/// <param name="Status">Raw status int: 0 Deploying, 1 Active, 2 Draining.</param>
public sealed record RouterReleaseEntry(string ReleaseId, short SlotNo, int Status);
