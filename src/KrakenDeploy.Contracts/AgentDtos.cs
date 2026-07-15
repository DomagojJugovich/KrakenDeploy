namespace KrakenDeploy.Contracts;

/// <summary>
/// Sent by the agent immediately after the SignalR connection is established,
/// providing full machine information so the server can populate the target record.
/// </summary>
public sealed record AgentRegistrationRequest(
    Guid TargetId,
    string MachineName,
    string OperatingSystem,
    string AgentVersion,
    /// <summary>
    /// T1-7 CONTRACT CHANGE: informational/IGNORED. Authorization roles drive
    /// secret scoping and are assigned OPERATOR-side (target settings /
    /// registration wizard) — never self-declared by an agent. The current agent
    /// sends an empty list; the server ignores any value and audits a non-empty
    /// one (tampered/old agent). Field kept for wire compatibility; slated for
    /// removal in the B6 contract pass.
    /// </summary>
    IReadOnlyList<string> Roles,
    long FreeDiskBytes,
    long TotalRamBytes);

/// <summary>
/// Sent every 30 s by the agent. Only non-null fields are applied on the server.
/// </summary>
public sealed record HeartbeatRequest(
    string? MachineName,
    string? OperatingSystem,
    string? AgentVersion,
    long? FreeDiskBytes);

/// <summary>
/// Returned by GET /api/agents/update-info. Tells the agent whether a newer
/// version is available and where to download it.
/// </summary>
public sealed record AgentUpdateInfo(
    bool UpdateAvailable,
    string? LatestVersion,
    string? DownloadUrl,
    long? SizeBytes,
    string? Sha256);

/// <summary>Body for POST /api/deployments/{id}/logs.</summary>
public sealed record DeploymentLogLineRequest(string Level, string Message);

/// <summary>Body for POST /api/deployments/{id}/complete.</summary>
public sealed record CompleteDeploymentRequest(bool Success, string? ErrorMessage);
