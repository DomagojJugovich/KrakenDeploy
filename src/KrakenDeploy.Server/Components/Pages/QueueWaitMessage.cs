namespace KrakenDeploy.Server.Components.Pages;

/// <summary>
/// F1 — the single formatter for the "another deployment of X to Y is running"
/// queue-wait reason, shared by <c>Deployments</c> (list) and
/// <c>DeploymentDetail</c> so the operator-facing wording stays identical (it was
/// duplicated in both pages). The blocked-peer DETECTION lives in
/// <c>ServerTaskLease.InFlightDeploymentPeerPredicate</c> /
/// <c>DeploymentStatusExtensions.InFlightAfterClaim</c>; this only builds the
/// sentence.
/// </summary>
public static class QueueWaitMessage
{
    /// <summary>The reason shown for a Queued deployment held by the
    /// (project, environment, tenant) serialization rule because a same-key
    /// deployment is in-flight. Falls back to generic wording when a name is
    /// unavailable.</summary>
    public static string RunningPeer(string? projectName, string? environmentName)
        => $"Waiting: another deployment of {projectName ?? "this project"} to " +
           $"{environmentName ?? "this environment"} is running.";

    /// <summary>The reason shown for a Queued task held by the maintenance gate in
    /// <c>ServerTaskLease.TryClaimAsync</c>. Takes precedence over
    /// <see cref="RunningPeer"/> in the pages: while maintenance is on, the gate is
    /// the binding constraint regardless of what else is in flight. Without this the
    /// task would sit at a bare "Waiting to start." and read as a hung queue.</summary>
    public static string MaintenanceHold(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "Waiting: the instance is in maintenance mode. This task starts "
              + "automatically once maintenance is disabled."
            : $"Waiting: the instance is in maintenance mode ({reason.Trim()}). "
              + "This task starts automatically once maintenance is disabled.";

    /// <summary>F6 — the reason shown for a Queued task held by the per-plan
    /// target exclusion ("Waiting for target X — busy with #N (title); M ahead").
    /// Delegates to the Data-side formatter so the banner and the one-time
    /// first-deferral task-log line render the SAME sentence.</summary>
    public static string TargetWait(
        KrakenDeploy.Server.Data.ServerTaskTargetExclusion.TargetConflict conflict)
        => KrakenDeploy.Server.Data.ServerTaskTargetExclusion.Format(conflict);
}
