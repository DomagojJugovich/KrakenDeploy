using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Decommissions a deployment target as one operator action, spanning the data
/// layer (<see cref="TargetService"/>) and the transport layer
/// (<see cref="IAgentConnectionRegistry"/>). Retire soft-decommission the target
/// (hidden from matching/dispatch, history preserved) and drops the live tunnel
/// immediately; delete hard-removes a history-free target. Lives in the app
/// project because it must reach the in-memory connection registry, which the
/// data layer cannot reference.
/// </summary>
public sealed class TargetDecommissioner(
    TargetService targets,
    IAgentConnectionRegistry registry,
    TargetStatusPublisher statusPublisher,
    IAccountContext accountContext)
{
    /// <summary>
    /// Retires the target (see <see cref="TargetService.RetireAsync"/>) and drops
    /// its live tunnel now — the token-version bump alone would only take effect on
    /// the agent's next (re)connect, and the AgentHub retired-target gate then
    /// refuses it. Publishes the new <see cref="TargetStatus.Disabled"/> status so
    /// other open sessions reflect the retirement without a manual reload. Returns
    /// <c>false</c> if the target does not exist (or is outside the caller's Space).
    /// </summary>
    public async Task<bool> RetireAsync(
        Guid targetId, CallerAuthorization caller, CancellationToken ct = default)
    {
        var retired = await targets.RetireAsync(targetId, caller, ct).ConfigureAwait(false);
        if (!retired)
        {
            return false;
        }

        // Kill the live connection now; the retired gate in AgentHub refuses any
        // subsequent reconnect.
        registry.AbortConnectionFor(targetId);

        // Push the decommissioned status so other circuits / external UI clients
        // see the target flip to Disabled without waiting for a reload. Mirrors the
        // AgentHub retired gate, which publishes on refusal.
        var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
        await statusPublisher
            .PublishAsync(targetId, TargetStatus.Disabled, lastSeenUtc: null, accountId)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Hard-deletes a history-free target (see <see cref="TargetService.DeleteAsync"/>)
    /// and drops its live tunnel if one is up. Throws
    /// <see cref="InvalidOperationException"/> when the target has execution history
    /// (the service refuses). Returns <c>false</c> if the target does not exist.
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid targetId, CallerAuthorization caller, CancellationToken ct = default)
    {
        var deleted = await targets.DeleteAsync(targetId, caller, ct).ConfigureAwait(false);
        if (!deleted)
        {
            return false;
        }

        registry.AbortConnectionFor(targetId);
        return true;
    }
}
