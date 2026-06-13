using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// A single log line emitted by the agent during a <see cref="RunbookRun"/>.
/// Mirrors <c>DeploymentLogEntry</c> in structure.
/// </summary>
public class RunbookRunLogEntry : Entity, ISpaceScoped
{
    /// <summary>Inherited from the parent RunbookRun; set explicitly at the
    /// write site (agent/transport path has no real Space context).</summary>
    public Guid SpaceId { get; set; }

    public Guid RunbookRunId { get; set; }
    public RunbookRun RunbookRun { get; set; } = null!;

    public int Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Level { get; set; }
    public required string Message { get; set; }
}
