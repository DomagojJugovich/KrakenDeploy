using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Tenants;

namespace KrakenDeploy.Server.Core.Domain.Targets;

public class DeploymentTarget : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public required string Name { get; set; }
    public TargetStatus Status { get; set; } = TargetStatus.Unknown;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string? MachineName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? AgentVersion { get; set; }
    public List<string> Roles { get; set; } = [];

    /// <summary>
    /// Operator-assigned risk classification (M11.E.11). Cannot be inferred —
    /// see <see cref="TargetRiskLevel"/>. Defaults to
    /// <see cref="TargetRiskLevel.Production"/> (fail-safe). Consumed by the
    /// ad-hoc approval policy, which takes the MAX risk across a session's
    /// frozen target set.
    /// </summary>
    public TargetRiskLevel RiskLevel { get; set; } = TargetRiskLevel.Production;

    public TransportMode TransportMode { get; set; } = TransportMode.Reverse;
    public string? RegistrationKeyHash { get; set; }
    public DateTimeOffset? RegistrationTokenExpiresUtc { get; set; }

    /// <summary>
    /// Configuration for offline drop delivery. Populated only when
    /// <see cref="TransportMode"/> is <see cref="Targets.TransportMode.OfflineDrop"/>.
    /// Stored as JSONB.
    /// </summary>
    public OfflineDropConfig? OfflineDropConfig { get; set; }

    /// <summary>
    /// When false, the agent will not auto-update even when the server publishes
    /// a newer version. Defaults to true.
    /// </summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>Tenant tags assigned to this target.</summary>
    public ICollection<TenantTag> TenantTags { get; set; } = [];
}
