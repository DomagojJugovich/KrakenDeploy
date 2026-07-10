namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// Discriminator for the unified <see cref="ServerTask"/> spine — the one
/// <c>server_tasks</c> table holds both deployments and runbook runs
/// (table-per-hierarchy). Stored as an int so adding a variant is additive.
/// </summary>
public enum ServerTaskKind
{
    /// <summary>A release deployed into an environment (<see cref="Deployment"/>).</summary>
    Deployment = 0,

    /// <summary>One execution of a runbook (<see cref="Runbooks.RunbookRun"/>).</summary>
    RunbookRun = 1,
}
