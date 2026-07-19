namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// E9 (INTERIM — superseded by the D1 engine merge, after which B3's wave-level
/// disconnect monitor covers runbook runs too). A read-only probe of an agent
/// target's live connection, implemented in Server.Transport over the agent
/// connection registry and abstracted here so the dispatch reconciler
/// (<c>ScheduledDeploymentDispatchJob</c>, Server.Data) can consult it without a
/// transport dependency — the same seam shape as <see cref="IAgentCancelPusher"/>.
/// <para>
/// Node-local by nature (the in-memory registry only knows connections on THIS
/// node), so callers pair it with a shared-DB signal (<c>DeploymentTarget.LastSeenUtc</c>)
/// for a scale-out-safe "continuously disconnected" decision — the registry check
/// only ever makes the reap MORE conservative (never reap a target this node can
/// still see).
/// </para>
/// </summary>
public interface IAgentLivenessProbe
{
    /// <summary>True if the target has at least one live agent connection on this
    /// node right now.</summary>
    bool IsTargetConnected(Guid targetId);
}
