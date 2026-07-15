using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// A8/T1-12 — revokes a target's agent bearer token(s) as one operator action:
/// bump the target's token version (so every outstanding token is rejected on
/// its next connect/call), drop the live tunnel immediately if the agent is
/// connected, and write an audit row. Lives in the app project because it spans
/// the data layer (<see cref="TargetService"/>) and the transport layer
/// (<see cref="IAgentConnectionRegistry"/>).
/// </summary>
public sealed class AgentAccessRevoker(
    TargetService targets,
    IAgentConnectionRegistry registry,
    IAuditLog audit)
{
    /// <summary>
    /// Revokes the target's agent tokens. Returns <c>false</c> if the target does
    /// not exist (or is outside the caller's Space).
    /// </summary>
    public async Task<bool> RevokeAsync(Guid targetId, CancellationToken ct = default)
    {
        // Read first for the audit subject name + a NotFound distinction.
        var target = await targets.GetAsync(targetId, ct).ConfigureAwait(false);
        if (target is null)
        {
            return false;
        }

        var newVersion = await targets.RevokeAgentTokenAsync(targetId, ct).ConfigureAwait(false);
        if (newVersion is null)
        {
            return false;
        }

        // Kill the live connection now; the version bump alone would only take
        // effect on the agent's next (re)connect.
        registry.AbortConnectionFor(targetId);

        await audit.RecordAsync(
            AuditEventType.AgentTokenRevoked,
            subjectType: "DeploymentTarget",
            subjectId:   targetId.ToString(),
            subjectName: target.Name,
            details:     $"Agent access revoked; token version is now {newVersion}. Agent must re-enroll.",
            ct:          ct).ConfigureAwait(false);

        return true;
    }
}
