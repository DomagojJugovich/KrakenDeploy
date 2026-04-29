namespace KrakenDeploy.Contracts;

/// <summary>
/// The full deployment plan sent from the server to the agent over SignalR.
/// The agent executes all steps autonomously and reports back via
/// <see cref="IAgentHubServer.AppendLogAsync"/> and
/// <see cref="IAgentHubServer.CompleteDeploymentAsync"/>.
/// </summary>
public sealed record DeploymentPlan(
    Guid DeploymentId,
    string EnvironmentName,
    DeploymentStepPlan[] Steps,
    IReadOnlyDictionary<string, string> Variables);

/// <summary>
/// One step within a <see cref="DeploymentPlan"/>.
/// </summary>
public sealed record DeploymentStepPlan(
    int Index,
    string Name,
    string StepType,
    string PackageId,
    string PackageVersion,
    IReadOnlyDictionary<string, string> Config);

/// <summary>
/// Sent by the agent to the server when a deployment is triggered.
/// Contains all the data needed to download the package via gRPC.
/// </summary>
public sealed record PackageDownloadInfo(
    string PackageId,
    string Version);
