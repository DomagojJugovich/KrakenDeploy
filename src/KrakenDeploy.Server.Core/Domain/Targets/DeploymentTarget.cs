using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Environments;
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
    /// A8/T1-12: monotonic version stamped into every agent bearer token this
    /// target is issued (the <c>atv</c> claim). The token is accepted only while
    /// its claim equals this value; bumping it (see
    /// <c>TargetService.RevokeAgentTokenAsync</c>) revokes all outstanding tokens
    /// for the target WITHOUT deleting it or rotating the shared signing key. The
    /// agent must then re-enroll. Defaults to 0.
    /// </summary>
    public int AgentTokenVersion { get; set; }

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

    /// <summary>
    /// Soft-delete / decommission flag. A retired target is hidden from target
    /// matching and dispatch (deploy-dialog pickers, runbook trigger), its agent
    /// is rejected at <c>AgentHub</c> connect, and it no longer counts toward the
    /// fleet health summary — but the row (and therefore all execution history
    /// that references it via the RESTRICT FKs on <c>task_target_assignments</c>
    /// and <c>task_step_outcomes</c>) is preserved. Retire is the ONLY supported
    /// path for a target that has ever been deployed to; a hard
    /// <see cref="TargetService.DeleteAsync"/> is refused while history exists.
    /// Defaults to false.
    /// </summary>
    public bool IsRetired { get; set; }

    /// <summary>
    /// Tenants this target is directly associated with — the PRIMARY
    /// tenant↔target link (Octopus "Associated Tenants"). Tenant-aware
    /// filtering (e.g. variable scoping) reads THIS relation. Tags applied to
    /// the target (extended tag sets, <c>TagApplication</c> rows with
    /// <c>EntityKind = DeploymentTarget</c>) are auxiliary metadata, not
    /// association.
    /// </summary>
    public ICollection<Tenant> Tenants { get; set; } = [];

    /// <summary>Environments this target serves (Octopus "Environments").</summary>
    public ICollection<DeploymentEnvironment> Environments { get; set; } = [];
}
