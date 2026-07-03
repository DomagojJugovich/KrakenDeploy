namespace KrakenDeploy.ControlPlane.Catalog;

/// <summary>
/// A platform application release deployed to one blue-green <b>slot</b>
/// (docs/blue-green-slot-deployment.md §2/§4). Control-plane scope — one row per
/// known release of the KrakenDeploy monolith itself. Deliberately named
/// <c>AppRelease</c>: the tenant domain already has a <c>Release</c> entity
/// (a project's release), which is entirely unrelated.
/// </summary>
public class AppRelease
{
    /// <summary>
    /// Opaque release id carried in the <c>__Host-kd_ver</c> cookie and the
    /// <c>X-KD-Release</c> agent header. Operator-supplied (e.g. build number +
    /// short SHA). Immutable once registered.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>Human-facing label / build number.</summary>
    public required string Label { get; set; }

    /// <summary>Slot this release occupies (1-based; three slots is the floor, D-bg-4).</summary>
    public short SlotNo { get; set; }

    /// <summary>Lifecycle: Deploying → Active → Draining → Retired (§2).</summary>
    public AppReleaseStatus Status { get; set; } = AppReleaseStatus.Deploying;

    public DateTimeOffset DeployedAtUtc { get; set; }

    /// <summary>Set when the release is marked Retired (fully drained).</summary>
    public DateTimeOffset? DrainedAtUtc { get; set; }

    /// <summary>
    /// While Draining: latest instant to keep the release alive for <i>idle
    /// circuits</i>. Past it, stragglers re-pin to the default. Never applies to
    /// in-flight deployments (§9 — those always finish).
    /// </summary>
    public DateTimeOffset? DrainDeadlineUtc { get; set; }
}

/// <summary>Lifecycle state of an <see cref="AppRelease"/> (§2).</summary>
public enum AppReleaseStatus
{
    /// <summary>Being rolled out to its slot across nodes; not yet the default. Routable only via an explicit pin (health-gate).</summary>
    Deploying = 0,

    /// <summary>The <c>current_default_release</c> — new sessions, agents, and jobs land here.</summary>
    Active = 1,

    /// <summary>No longer the default; existing pinned circuits/deployments finish on it.</summary>
    Draining = 2,

    /// <summary>Fully drained; its slot is free for the next deploy. Pins to it fall back to the default.</summary>
    Retired = 3,
}
