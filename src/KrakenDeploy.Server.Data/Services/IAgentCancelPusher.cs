namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// B6 — pushes a cooperative-abort signal to the connected agent(s) executing a
/// task (deployment or runbook run). Implemented in Server.Transport over the
/// agent hub; abstracted here so the cancel services (Server.Data) can fire it
/// without a transport dependency.
/// <para>
/// Best-effort BY CONTRACT and never throws: the Cancelled verdict is already
/// recorded in the database before the push, so an offline agent simply misses
/// the signal and the task falls back to the pre-B6 semantics (wave-boundary
/// stop; the agent's late completion is swallowed by the B5 terminal guard).
/// The push is what upgrades that to "the running step's process tree dies
/// within seconds".
/// </para>
/// </summary>
public interface IAgentCancelPusher
{
    Task PushCancelAsync(Guid taskId, string? reason, CancellationToken ct = default);
}
