using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Subscriptions;

/// <summary>
/// The differentiator transport KrakenDeploy adds over stock Octopus parity:
/// a matching event triggers a runbook run. The operator configures
/// <c>runbookId + environmentId + targetId</c> in the subscription's
/// transport config; the transport calls
/// <c>RunbookService.TriggerAsync</c> with those parameters when an event
/// fires.
///
/// <para>
/// <b>Event data flow</b>: <c>RunbookService.TriggerAsync</c> doesn't take
/// an "input variables" parameter — runbooks resolve their own variables
/// from the project's variable scope at run time. To make event details
/// accessible inside runbook steps, set up project variables that read
/// from the upcoming <c>Octopus.Event.*</c> system-variable namespace
/// (deferred polish — for v1 the transport just fires the runbook; the
/// run's audit log carries the event id so an operator can correlate
/// the event ↔ run after the fact).
/// </para>
///
/// <para>
/// <b>Failure surface</b>: invalid runbook id / environment / target → the
/// underlying service throws; we capture into a failure result so the
/// dispatcher's row write stays uniform. Non-existent target / wrong
/// Space → InvalidOperationException; operator sees "Runbook trigger
/// failed: target X not in Space Y" in the delivery history grid.
/// </para>
/// </summary>
public sealed class RunbookTransport(
    IRunbookTrigger runbookService,
    ILogger<RunbookTransport> logger) : IEventTransport
{
    public SubscriptionTransport Transport => SubscriptionTransport.Runbook;

    private static readonly JsonSerializerOptions ConfigJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<EventTransportResult> DeliverAsync(
        EventSubscription subscription,
        AuditEntry auditEvent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(auditEvent);

        RunbookConfig config;
        try
        {
            config = JsonSerializer.Deserialize<RunbookConfig>(
                subscription.TransportConfigJson, ConfigJsonOpts)
                ?? throw new InvalidOperationException("config deserialised to null");
        }
        catch (Exception ex)
        {
            return EventTransportResult.Failure(
                $"Malformed runbook transport config: {ex.Message}");
        }

        if (!Guid.TryParse(config.RunbookId, out var runbookId))
        {
            return EventTransportResult.Failure(
                "Runbook config missing or malformed 'runbookId' (must be a GUID).");
        }
        if (!Guid.TryParse(config.EnvironmentId, out var environmentId))
        {
            return EventTransportResult.Failure(
                "Runbook config missing or malformed 'environmentId' (must be a GUID).");
        }
        if (!Guid.TryParse(config.TargetId, out var targetId))
        {
            return EventTransportResult.Failure(
                "Runbook config missing or malformed 'targetId' (must be a GUID).");
        }
        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(config.TenantId))
        {
            if (!Guid.TryParse(config.TenantId, out var t))
            {
                return EventTransportResult.Failure(
                    "Runbook config 'tenantId' was supplied but is not a GUID.");
            }
            tenantId = t;
        }

        try
        {
            var run = await runbookService.TriggerAsync(
                runbookId, environmentId, targetId, tenantId, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Runbook trigger ok: sub={SubId} event={EventId} runbook={RunbookId} run={RunId}",
                subscription.Id, auditEvent.Id, runbookId, run.Id);

            return EventTransportResult.Success(
                $"Runbook run started, id={run.Id} " +
                $"(env={environmentId:N}, target={targetId:N}, " +
                $"event={auditEvent.EventType}).");
        }
        catch (InvalidOperationException ex)
        {
            // RunbookService throws InvalidOperationException for the
            // common operator-misconfiguration cases (target not found,
            // runbook in different Space, etc.). Surface verbatim.
            return EventTransportResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            return EventTransportResult.Failure(
                $"Unexpected error triggering runbook: {ex.Message}");
        }
    }

    /// <summary>Schema for the subscription's TransportConfigJson when
    /// Transport=Runbook. Matches the validation in
    /// <c>EventSubscriptionService.ValidateTransportConfig</c>.</summary>
    private sealed record RunbookConfig(
        string RunbookId,
        string EnvironmentId,
        string TargetId,
        string? TenantId = null);
}
