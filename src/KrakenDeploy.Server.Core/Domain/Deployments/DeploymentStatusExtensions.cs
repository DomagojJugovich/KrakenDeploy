namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// The single authority for which <see cref="DeploymentStatus"/> values are
/// TERMINAL. Before B1 this classification was duplicated inline (and had
/// already diverged between call sites); every new guard should use this.
/// <c>PendingOfflineResult</c> is deliberately NON-terminal — the task is
/// parked awaiting an out-of-band result bundle.
/// </summary>
public static class DeploymentStatusExtensions
{
    public static bool IsTerminal(this DeploymentStatus status) => status is
        DeploymentStatus.Succeeded or
        DeploymentStatus.SucceededWithWarnings or
        DeploymentStatus.Failed or
        DeploymentStatus.Cancelled;

    /// <summary>
    /// The non-terminal states a task occupies AFTER it has been claimed — it is
    /// actively holding its (project, environment, tenant) slot: executing
    /// (<c>Running</c>) or handed off to the offline-drop workflow awaiting its
    /// result (<c>PendingOfflineResult</c>). <c>Queued</c> is pre-claim; the four
    /// terminal states are done. Kept in sync with <see cref="IsTerminal"/> by
    /// definition: in-flight == not <c>Queued</c> and not terminal. Single source
    /// for the F1 (project,env,tenant) serialization predicate, so the claim, the
    /// worker's pre-gate skip and the UI queue-reason all agree on which peers
    /// block — and a parked offline-drop deployment still counts as in-flight.
    /// </summary>
    public static readonly DeploymentStatus[] InFlightAfterClaim =
    [
        DeploymentStatus.Running,
        DeploymentStatus.PendingOfflineResult,
    ];

    /// <summary>
    /// <see cref="IsTerminal"/> as data, for the query provider. EF cannot translate the
    /// method, so a fail-CLOSED predicate ("anything not finished counts") must be
    /// expressed as <c>!Terminal.Contains(status)</c> rather than by enumerating the
    /// non-terminal states — the two are equivalent today and diverge the moment a
    /// non-terminal status is added, in the dangerous direction. F5's swap gate is the
    /// motivating case: an enumeration answered "idle" for a status it had never heard
    /// of, and the agent takes "idle" as licence to replace its own binary and exit.
    /// <para>
    /// This is NOT the complement of <see cref="InFlightAfterClaim"/>: that set is
    /// deliberately narrower (post-claim, slot-holding) and excludes <c>Queued</c>.
    /// </para>
    /// </summary>
    public static readonly DeploymentStatus[] Terminal =
    [
        DeploymentStatus.Succeeded,
        DeploymentStatus.SucceededWithWarnings,
        DeploymentStatus.Failed,
        DeploymentStatus.Cancelled,
    ];
}
