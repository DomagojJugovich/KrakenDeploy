using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Machine;

/// <summary>Collects machine and runtime information for registration and heartbeat payloads.</summary>
public sealed class MachineInfoCollector(ILogger<MachineInfoCollector> logger)
{
    // Resolve once at startup — version doesn't change at runtime.
    private static readonly string AgentVersion =
        typeof(MachineInfoCollector).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] // strip git hash suffix
        ?? typeof(MachineInfoCollector).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public AgentMachineInfo Collect(string dataPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataPath);

        var freeDisk = GetFreeDiskBytes(dataPath);

        return new AgentMachineInfo(
            MachineName: Environment.MachineName,
            OperatingSystem: RuntimeInformation.OSDescription,
            AgentVersion: AgentVersion,
            FreeDiskBytes: freeDisk,
            TotalRamBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }

    private long GetFreeDiskBytes(string dataPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dataPath));
            if (string.IsNullOrEmpty(root))
            {
                return 0;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not determine free disk bytes for path {DataPath}.", dataPath);
            return 0;
        }
    }
}
