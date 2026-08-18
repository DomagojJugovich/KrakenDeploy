using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Core.Domain.Projects;

public class Project : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>
    /// FK to the owning <see cref="ProjectGroup"/> (the Project's folder).
    /// Required — every Project belongs to exactly one Group (the Space's
    /// Default Project Group unless moved). The M10 transition that left this
    /// nullable is complete (backfilled + NOT NULL).
    /// </summary>
    public Guid ProjectGroupId { get; set; }

    /// <summary>Navigation to the owning group; null unless explicitly loaded.</summary>
    public ProjectGroup? ProjectGroup { get; set; }

    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    // The deployment process is a polymorphic Process row (owner_kind=Project,
    // owner_id=this.Id) with no owner FK — resolve it via ProcessService, not a
    // navigation property.

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

    /// <summary>
    /// F6 — author consent that this project's deployment process is
    /// self-contained (own folders, services, sites) and may run alongside other
    /// CONSENTING work on a shared machine. Feeds two composition points:
    /// <list type="bullet">
    ///   <item>Claim time (server): a deployment's per-target mode is
    ///   <c>Shared</c> when the target's own flag OR this flag is set; the
    ///   claim-time exclusion in <c>ServerTaskLease</c> only defers on a shared
    ///   target where at least one side is Exclusive.</item>
    ///   <item>Plan build (agent gate): OR-composed into
    ///   <c>DeploymentPlan.AllowParallelTaskExecution</c>, selecting the SHARED
    ///   side of the agent's reader-writer machine gate (F5).</item>
    /// </list>
    /// Default <c>false</c> — safe without author effort: deployments hold each
    /// target exclusively for the whole plan. Consent is mutual: setting this
    /// never lets the project's work interleave with a task that did not opt in.
    /// </summary>
    public bool AllowParallelTaskExecution { get; set; }
}
