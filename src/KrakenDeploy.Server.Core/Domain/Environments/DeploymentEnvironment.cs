using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Environments;

public class DeploymentEnvironment : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public required string Slug { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }

    /// <summary>
    /// Soft-retire flag. An environment referenced by execution history cannot
    /// be hard-deleted (RESTRICT FK from server_tasks); archiving hides it from
    /// pickers while keeping the id resolvable for historical rows. Archived
    /// environments still appear on the admin page (to unarchive).
    /// </summary>
    public bool Archived { get; set; }
}
