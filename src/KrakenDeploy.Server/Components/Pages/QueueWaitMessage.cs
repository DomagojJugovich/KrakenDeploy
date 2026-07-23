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
}
