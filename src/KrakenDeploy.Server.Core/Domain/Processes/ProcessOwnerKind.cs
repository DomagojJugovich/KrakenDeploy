namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// Discriminator for the polymorphic owner of a <see cref="Process"/> — the one
/// <c>processes</c> table serves both a project's deployment process and a
/// runbook's process. <see cref="Process.OwnerId"/> is the project id or the
/// runbook id accordingly. Stored as an int.
/// </summary>
public enum ProcessOwnerKind
{
    /// <summary>The process belongs to a <see cref="Projects.Project"/> (its deployment process).</summary>
    Project = 0,

    /// <summary>The process belongs to a <see cref="Runbooks.Runbook"/>.</summary>
    Runbook = 1,
}
