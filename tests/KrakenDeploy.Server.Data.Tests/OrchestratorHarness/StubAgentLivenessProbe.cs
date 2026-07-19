using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

/// <summary>
/// Test stub for <see cref="IAgentLivenessProbe"/>: a target counts as connected
/// iff it was added to <see cref="Connected"/>. Default (empty) = everything
/// disconnected — the reap-eligible state for the E9 disconnect reap tests.
/// </summary>
public sealed class StubAgentLivenessProbe : IAgentLivenessProbe
{
    public HashSet<Guid> Connected { get; } = [];

    public bool IsTargetConnected(Guid targetId) => Connected.Contains(targetId);
}
