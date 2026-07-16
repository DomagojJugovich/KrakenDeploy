namespace KrakenDeploy.Contracts;

/// <summary>
/// The agent wire-contract version this assembly speaks. Sent by the agent in
/// <see cref="AgentRegistrationRequest.ContractVersion"/> and enforced by the
/// server at registration: a mismatch is REFUSED with an explicit
/// <see cref="AgentRegistrationResult"/> instead of the pre-B6 failure mode
/// (silently dropped log/step reports after an unnegotiated signature change).
/// Bump on every breaking change to the SignalR agent surface
/// (<see cref="IAgentHubServer"/> / <see cref="IAgentHubClient"/>) or to
/// <see cref="DeploymentPlan"/>.
/// </summary>
public static class AgentContract
{
    /// <summary>
    /// Version 1 = the B6 freeze surface: DispatchId on plan + completion +
    /// step + log reports, CancelDeploymentAsync push, registration result,
    /// Roles removed from registration.
    /// </summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// Sent by the agent immediately after the SignalR connection is established,
/// providing full machine information so the server can populate the target record.
/// <para>
/// B6 CONTRACT CHANGE: <c>Roles</c> is REMOVED (T1-7 — roles are authorization,
/// operator-assigned server-side, and were already ignored + audited when
/// self-declared; the field no longer exists on the wire).
/// <see cref="ContractVersion"/> is ADDED — a pre-B6 agent deserializes to the
/// default 0 and is refused with a clear upgrade message.
/// </para>
/// </summary>
public sealed record AgentRegistrationRequest(
    Guid TargetId,
    string MachineName,
    string OperatingSystem,
    string AgentVersion,
    long FreeDiskBytes,
    long TotalRamBytes,
    int ContractVersion);

/// <summary>
/// B6 — the server's verdict on a registration. <c>Accepted == false</c> means
/// the agent must NOT expect to receive work (the server has removed the
/// connection from its dispatch registry); the agent logs
/// <see cref="Message"/>, drops the connection and retries on its slow lane so
/// it self-heals after an agent upgrade. Pre-B6 agents invoked
/// <c>RegisterAsync</c> as void and simply ignore this payload — their refusal
/// is enforced server-side.
/// </summary>
public sealed record AgentRegistrationResult(
    bool Accepted,
    int ServerContractVersion,
    string? Message = null);

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
