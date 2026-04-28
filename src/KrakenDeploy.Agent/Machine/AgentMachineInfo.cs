namespace KrakenDeploy.Agent.Machine;

/// <summary>Snapshot of the local machine state, collected at startup and each heartbeat.</summary>
public sealed record AgentMachineInfo(
    string MachineName,
    string OperatingSystem,
    string AgentVersion,
    long FreeDiskBytes,
    long TotalRamBytes);
