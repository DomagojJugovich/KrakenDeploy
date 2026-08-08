using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Everything the server RECORDS when it refuses an agent's wire contract on the handshake:
/// the target stops reading Online, the UI is told, and an audit row names the target.
/// <para>
/// A separate service rather than inline in <see cref="AgentContractHandshakeGate"/> for two
/// reasons. The gate's job is to answer 426 and it should stay small enough to read in one
/// pass; and this half needs a resolved tenant database, so it is the half that must be
/// best-effort and independently testable against a real one.
/// </para>
/// </summary>
public interface IAgentContractRefusalRecorder
{
    /// <summary>
    /// Records a refusal against <paramref name="targetId"/>.
    /// <paramref name="presentedContract"/> is the gate's already-truncated description of
    /// what the agent sent — this method does not bound it further and must not be handed raw
    /// client input.
    /// </summary>
    Task RecordAsync(Guid targetId, string presentedContract, CancellationToken ct = default);
}

/// <summary>
/// EF-backed <see cref="IAgentContractRefusalRecorder"/>.
/// <para>
/// This exists because moving the contract check onto the handshake silently dropped four side
/// effects the old in-hub refusal had, and the first of them is the one an operator notices:
/// without the Offline mark the WHOLE FLEET reads Online after a contract-bumping server
/// upgrade until <c>AgentLastSeenOfflineJob</c> catches it — a 3-minute threshold on a
/// 5-minute cron, so up to ~8 minutes — and that job does not call the status publisher, so an
/// open dashboard stays green until someone reloads the page. An operator mid-upgrade reading
/// a green fleet concludes the upgrade went fine.
/// </para>
/// <para>
/// The one side effect that CANNOT be restored is <c>target.AgentVersion</c>. A refused
/// connection never sends a registration payload, and the build version is not on the
/// handshake — so after an agent ROLLBACK the targets list keeps advertising the newer
/// version, which is the one field an operator uses to decide what to upgrade. Accepted as a
/// residual and recorded in <c>docs/agent-wire-contract.md</c>; closing it costs a second
/// handshake header.
/// </para>
/// </summary>
public sealed class AgentContractRefusalRecorder(
    IDbContextFactory<KrakenDbContext> dbFactory,
    TargetStatusPublisher statusPublisher,
    IAccountContext accountContext,
    TimeProvider timeProvider,
    ILogger<AgentContractRefusalRecorder> logger) : IAgentContractRefusalRecorder
{
    public async Task RecordAsync(
        Guid targetId, string presentedContract, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Filter-free: the handshake has no ambient Space, and the global filter would hide a
        // target that lives in a non-Default one. The id comes from the agent's own
        // authenticated NameIdentifier, and target ids are globally unique, so this cannot
        // cross accounts — in multi-account the connection's account is already pinned by
        // AccountResolutionMiddleware, so this reads the correct tenant database.
        var target = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == targetId, ct)
            .ConfigureAwait(false);

        // Only Online → Offline. A target that is already Offline needs no write (the refusal
        // repeats for as long as the skew lasts), and one that is Disabled is RETIRED — that
        // state is deliberate and must not be downgraded, exactly as the retired-registration
        // path is careful not to.
        if (target is { Status: TargetStatus.Online })
        {
            target.Status = TargetStatus.Offline;
            target.LastSeenUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var accountId = accountContext.IsResolved
                ? accountContext.CurrentAccountId
                : Guid.Empty;
            await statusPublisher
                .PublishAsync(targetId, TargetStatus.Offline, target.LastSeenUtc, accountId)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Target {TargetId} marked Offline: its wire contract is refused, so it cannot " +
                "be dispatched to. Without this the fleet reads Online until the last-seen " +
                "sweep catches up.", targetId);
        }

        // The audit row is written DIRECTLY rather than through IAuditLog.RecordAsync, and the
        // reason is the Space stamp. RecordAsync takes SpaceId from the ambient ISpaceContext,
        // and on an agent handshake HttpSpaceContext has nothing resolved so it falls back to
        // WellKnown.DefaultSpaceId. For a target in any other Space that filed the refusal in
        // the WRONG Space: AuditExportService.ApplySpaceVisibility cages reads to the row's own
        // Space, so the target's per-entity Events tab showed nothing for the refusal that had
        // just taken it Offline, while the Default Space's audit grid and CSV/JSON export showed
        // that target's name — leaking a foreign Space's target into it.
        //
        // This is the pattern /api/agents/update-status already uses and documents for exactly
        // the same reason, and the house rule "agent-path writes stamp SpaceId from the parent".
        // A target we could not load has no Space to stamp, so the row is filed as a platform
        // event (null SpaceId), visible to AdministerSystem holders — the honest answer for a
        // refusal from a credential whose target no longer exists.
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredUtc = timeProvider.GetUtcNow(),
            SpaceId     = target?.SpaceId,
            EventType   = AuditEventType.AgentContractVersionRejected,
            SubjectType = "DeploymentTarget",
            SubjectId   = targetId.ToString(),
            // Without this the audit grid, the CSV/JSON export and the notification e-mails
            // identify the refused agent by bare GUID.
            SubjectName = target?.Name,
            // NOT the ambient principal: on this path it is the AGENT, whose NameIdentifier is a
            // DeploymentTarget id, so attributing the row to it would claim a user that does not
            // exist and render as "Unknown".
            UserId      = null,
            UserDisplay = "System",
            Details     = $"SentContract={presentedContract}, " +
                          $"RequiredContract={AgentContract.CurrentVersion}",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
