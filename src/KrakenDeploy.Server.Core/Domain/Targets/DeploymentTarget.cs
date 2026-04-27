using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Targets;

public class DeploymentTarget : AuditableEntity
{
    public required string Name { get; set; }
    public TargetStatus Status { get; set; } = TargetStatus.Unknown;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string? MachineName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? AgentVersion { get; set; }
    public List<string> Roles { get; set; } = [];
    public TransportMode TransportMode { get; set; } = TransportMode.Reverse;
    public string? RegistrationKeyHash { get; set; }
}
