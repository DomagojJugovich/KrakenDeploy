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
