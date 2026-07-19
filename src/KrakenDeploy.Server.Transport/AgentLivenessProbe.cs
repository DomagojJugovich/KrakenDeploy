using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// E9 (INTERIM — superseded by the D1 engine merge) — <see cref="IAgentLivenessProbe"/>
/// over the in-memory <see cref="IAgentConnectionRegistry"/>. Singleton, like the
/// registry it reads; a pure lookup with no DB or hub dependency.
/// </summary>
public sealed class AgentLivenessProbe(IAgentConnectionRegistry registry) : IAgentLivenessProbe
{
    public bool IsTargetConnected(Guid targetId) => registry.HasConnectionFor(targetId);
}
