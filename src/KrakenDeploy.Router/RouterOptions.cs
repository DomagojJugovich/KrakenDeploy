namespace KrakenDeploy.Router;

/// <summary>
/// Configuration for the per-node blue-green slot router, bound from the
/// <c>Router</c> section. The slot map is static infrastructure (slots never
/// change — releases rotate through them, §2 of the design), so it lives in
/// plain config; only the release→slot assignment is dynamic (catalog).
/// </summary>
public sealed class RouterOptions
{
    public const string SectionName = "Router";

    /// <summary>
    /// Slot number → base URL of the local slot instance
    /// (e.g. <c>"1": "http://localhost:5081"</c>). Localhost in production —
    /// the router is co-located with the slots on the app node (D-bg-7).
    /// </summary>
    public Dictionary<short, string> Slots { get; } = [];

    /// <summary>
    /// Seconds a catalog snapshot is served before a refresh is attempted.
    /// A default flip therefore propagates within this window (plus the
    /// explicit <c>/kd-router/invalidate</c> push, when wired). Default 5.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 5;

    /// <summary>
    /// Shared secret required (as the <c>X-KD-Ops-Token</c> header) by
    /// <c>POST /kd-router/invalidate</c>. The router sits behind a pass-everything
    /// edge, so without a token an anonymous internet client could bust the
    /// snapshot cache at will. Null/empty (default) DISABLES the endpoint —
    /// routers then converge purely via the cache TTL.
    /// </summary>
    public string? OpsToken { get; set; }
}
