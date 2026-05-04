using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Tenants;

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
    public DateTimeOffset? RegistrationTokenExpiresUtc { get; set; }

    /// <summary>
    /// Configuration for offline drop delivery. Populated only when
    /// <see cref="TransportMode"/> is <see cref="Targets.TransportMode.OfflineDrop"/>.
    /// Stored as JSONB.
    /// </summary>
    public OfflineDropConfig? OfflineDropConfig { get; set; }

    /// <summary>Tenant tags assigned to this target.</summary>
    public ICollection<TenantTag> TenantTags { get; set; } = [];
}
