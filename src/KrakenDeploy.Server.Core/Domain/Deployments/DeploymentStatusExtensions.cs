using System.Collections.Immutable;

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
    public static bool IsTerminal(this DeploymentStatus status) => status is
        DeploymentStatus.Succeeded or
        DeploymentStatus.SucceededWithWarnings or
        DeploymentStatus.Failed or
        DeploymentStatus.Cancelled;

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
    /// <para>
    /// DERIVED from <see cref="IsTerminal"/> rather than hand-listed, and
    /// <see cref="ImmutableArray{T}"/> rather than <c>DeploymentStatus[]</c>. Both changes
    /// close the same class of hole: this set backs a FAIL-CLOSED decision — the agent takes
    /// "not in this set" as licence to replace its own install directory and exit — and a
    /// public mutable array let any caller in the process rewrite that decision, while a
    /// hand-written duplicate let it drift from <see cref="IsTerminal"/> silently. Deriving it
    /// makes drift unrepresentable and retires the test that existed only to detect it.
    /// EF translates <c>ImmutableArray.Contains</c> the same way it translated the array.
    /// </para>
    /// </summary>
    public static readonly ImmutableArray<DeploymentStatus> Terminal =
        [.. Enum.GetValues<DeploymentStatus>().Where(s => s.IsTerminal())];
}
