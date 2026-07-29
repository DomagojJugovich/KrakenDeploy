using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// F5 — test helper for the registry's two-phase connection lifecycle.
/// <para>
/// A connection becomes DISPATCHABLE in two steps: <c>Add</c> when the hub's
/// <c>OnConnectedAsync</c> accepts the socket, then <c>MarkRegistered</c> once
/// <c>RegisterAsync</c> has passed — including the wire-contract version check. Only the
/// second makes <c>GetConnectionId</c> / <c>HasConnectionFor</c> return it, because a
/// version-skewed agent must never be handed work (a v2 agent reads v3's
/// <c>AllowParallelTaskExecution = true</c> as "no machine gate at all").
/// </para>
/// <para>
/// Most tests only care that a target HAS a usable connection, so they say
/// <see cref="AddRegistered"/> and get both steps. Tests that specifically exercise the
/// window between them call <c>Add</c> and <c>MarkRegistered</c> separately.
/// </para>
/// </summary>
internal static class AgentConnectionRegistryTestExtensions
{
    internal static void AddRegistered(
        this IAgentConnectionRegistry registry,
        string connectionId,
        Guid targetId,
        Guid accountId = default,
        Action? abort = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Add(connectionId, targetId, accountId, abort);
        registry.MarkRegistered(connectionId);
    }
}
