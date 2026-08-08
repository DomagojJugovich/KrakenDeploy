namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// The single authority for which <see cref="DeploymentStatus"/> values are
/// TERMINAL. Before B1 this classification was duplicated inline (and had
/// already diverged between call sites); every new guard should use this.
/// <c>PendingOfflineResult</c> is deliberately NON-terminal — the task is
/// parked awaiting an out-of-band result bundle. <c>Paused</c> (WP3) is
/// likewise non-terminal — parked awaiting a human approve/reject.
/// </summary>
public static class DeploymentStatusExtensions
{
    /// <summary>
    /// The terminal set as data, for LINQ predicates EF must translate to SQL
    /// (the <see cref="IsTerminal"/> extension method cannot be translated).
    /// Kept as the single definition — <see cref="IsTerminal"/> reads it.
    /// </summary>
    public static readonly DeploymentStatus[] TerminalStatuses =
    [
        DeploymentStatus.Succeeded,
        DeploymentStatus.SucceededWithWarnings,
        DeploymentStatus.Failed,
        DeploymentStatus.Cancelled,
    ];

    public static bool IsTerminal(this DeploymentStatus status)
        => Array.IndexOf(TerminalStatuses, status) >= 0;

    /// <summary>
    /// The non-terminal states a task occupies AFTER it has been claimed — it is
    /// actively holding its (project, environment, tenant) slot: executing
    /// (<c>Running</c>), handed off to the offline-drop workflow awaiting its
    /// result (<c>PendingOfflineResult</c>), or parked at a manual-intervention
    /// gate awaiting a human decision (<c>Paused</c>, WP3). <c>Queued</c> is
    /// pre-claim; the four terminal states are done. Kept in sync with
    /// <see cref="IsTerminal"/> by definition: in-flight == not <c>Queued</c> and
    /// not terminal. Single source for the F1 (project,env,tenant) serialization
    /// predicate, so the claim, the worker's pre-gate skip and the UI queue-reason
    /// all agree on which peers block — and a parked offline-drop or paused
    /// deployment still counts as in-flight.
    /// <para>
    /// <c>Paused</c> is in this set for CORRECTNESS, not just parity: were a paused
    /// deployment to release its key, a newer release could deploy and complete
    /// during the approval window, and the older release — once approved — would
    /// then overwrite the newer code. The intervention timeout bounds the hold, and
    /// an operator can cancel the paused task from the deployment / runbook-run detail
    /// page or its grid row (all of which accept <c>Paused</c>), which also closes the
    /// unanswered gate.
    /// </para>
    /// </summary>
    public static readonly DeploymentStatus[] InFlightAfterClaim =
    [
        DeploymentStatus.Running,
        DeploymentStatus.PendingOfflineResult,
        DeploymentStatus.Paused,
    ];
}
