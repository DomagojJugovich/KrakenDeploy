using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Core.Domain.Projects;

public class Project : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Deployment process for this project (one-to-one, created lazily).</summary>
    public DeploymentProcess? Process { get; set; }

    /// <summary>Variable set for this project (one-to-one, created lazily).</summary>
    public VariableSet? VariableSet { get; set; }

    /// <summary>Tenants connected to this project.</summary>
    public ICollection<Tenant> Tenants { get; set; } = [];

    /// <summary>
    /// Default lifecycle applied to all channels that don't specify one.
    /// <c>null</c> = no lifecycle gates enforced by default.
    /// </summary>
    public Guid? LifecycleId { get; set; }
    public Lifecycle? Lifecycle { get; set; }

    /// <summary>Channels defined for this project (at least one default always exists).</summary>
    public ICollection<Channel> Channels { get; set; } = [];
}
