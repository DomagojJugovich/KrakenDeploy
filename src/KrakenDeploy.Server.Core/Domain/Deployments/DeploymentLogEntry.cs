using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// A single line of output from a running or completed deployment.
/// Written by <c>AgentHub.AppendLogAsync</c> and streamed in real time
/// to the UI via <c>UiHub</c>.
/// </summary>
public class DeploymentLogEntry : Entity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;

    /// <summary>Monotonically increasing per-deployment sequence number.</summary>
    public int Sequence { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public required string Message { get; set; }

    /// <summary>"info" | "warning" | "error" — used for UI colouring.</summary>
    public string Level { get; set; } = "info";
}
