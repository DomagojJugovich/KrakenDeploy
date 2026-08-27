using KrakenDeploy.Server.Core.Domain.Maintenance;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// BG1/T13 — the maintenance CREATION refusal shared by
/// <c>DeploymentService.CreateAsync</c> and <c>RunbookService.TriggerAsync</c>,
/// the authoritative gate for every surface in one place (UI rides the
/// <c>/_blazor</c> middleware exemption; REST/MCP callers holding
/// BypassMaintenance pass the middleware). UNCONDITIONAL — deliberately ignores
/// <c>Permission.BypassMaintenance</c>: an admin-created task would only sit
/// Queued-stuck behind the claim gate (e27c89a), so a refusal naming the switch
/// beats a dead bypass. Cache-free read on the caller's own context, mirroring
/// the claim gate.
/// </summary>
public static class MaintenanceCreationGate
{
    /// <summary>
    /// Throws the caller-worded <see cref="InvalidOperationException"/> (with
    /// the operator's maintenance reason appended) when maintenance is on. A
    /// child creation — <paramref name="parentTaskId"/> set and claimed
    /// (<see cref="ServerTaskLease.IsContinuationOfClaimedParent"/>) — is exempt
    /// so an in-flight parent can never strand; runbook runs pass
    /// <c>parentTaskId: null</c> (they are never children) and are therefore
    /// always gated — zero escape hatch (T-B2).
    /// </summary>
    public static async Task EnsureAllowedAsync(
        KrakenDbContext db, Guid? parentTaskId, string refusalText, CancellationToken ct)
    {
        if (ServerTaskLease.IsContinuationOfClaimedParent(parentTaskId))
        {
            return;
        }

        var maintenance = await SettingsService
            .ReadOrDefaultAsync<MaintenanceSettings>(db, ct: ct)
            .ConfigureAwait(false);
        if (maintenance.Enabled)
        {
            throw new InvalidOperationException(
                refusalText
                + (string.IsNullOrWhiteSpace(maintenance.Reason)
                    ? ""
                    : $" Reason: {maintenance.Reason}"));
        }
    }
}
