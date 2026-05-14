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
    /// <summary>
    /// Resolved, Octostache-substituted scalar variables (string and sensitive).
    /// StringArray variables appear here as comma-joined strings for Octostache
    /// and backward-compat <c>$OctopusParameters</c> access.
    /// </summary>
    IReadOnlyDictionary<string, string> Variables,
    /// <summary>
    /// StringArray variable values as parsed string arrays, for
    /// <c>$OctopusArrays</c> PowerShell exposure and <c>#{each}</c> Octostache iteration.
    /// </summary>
    IReadOnlyDictionary<string, string[]> ArrayVariables);

/// <summary>
/// One step within a <see cref="DeploymentPlan"/>.
/// Config values have already had Octostache variable substitution applied server-side.
/// </summary>
public sealed record DeploymentStepPlan(
    int Index,
    string Name,
    string StepType,
    string PackageId,
    string PackageVersion,
    IReadOnlyDictionary<string, string> Config,
    IReadOnlyList<string>? TargetRoles = null);

/// <summary>
/// Sent by the agent to the server when a deployment is triggered.
/// Contains all the data needed to download the package via gRPC.
/// </summary>
public sealed record PackageDownloadInfo(
    string PackageId,
    string Version);
