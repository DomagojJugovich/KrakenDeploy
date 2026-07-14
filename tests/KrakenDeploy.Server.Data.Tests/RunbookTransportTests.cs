using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Subscriptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for <see cref="RunbookTransport"/>. The transport is a thin
/// adapter over <c>RunbookService.TriggerAsync</c> — tests focus on the
/// config-validation + error-translation surface, with a stub
/// <c>RunbookService</c> so we don't need to spin up the full runbook
/// execution pipeline.
/// </summary>
public sealed class RunbookTransportTests
{
    [Fact]
    public async Task DeliverAsync_invokes_RunbookService_with_config_ids()
    {
        var runbookId    = Guid.NewGuid();
        var envId        = Guid.NewGuid();
        var targetId     = Guid.NewGuid();

        var captured = new CapturingRunbookService();
        var transport = new RunbookTransport(
            captured, NullLogger<RunbookTransport>.Instance);

        var sub = new EventSubscription
        {
            Name                = "trigger-runbook",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Runbook,
            TransportConfigJson = $$$"""
                {
                    "runbookId":     "{{{runbookId}}}",
                    "environmentId": "{{{envId}}}",
                    "targetId":      "{{{targetId}}}"
                }
                """,
        };
        var evt = NewEvent();

        var result = await transport.DeliverAsync(sub, evt, default);

        result.Succeeded.Should().BeTrue();
        captured.RunbookId.Should().Be(runbookId);
        captured.EnvironmentId.Should().Be(envId);
        captured.TargetId.Should().Be(targetId);
        captured.TenantId.Should().BeNull(
            "tenant id is optional; absent in config means untenanted run");
    }

    [Fact]
    public async Task DeliverAsync_passes_tenant_id_when_supplied()
    {
        var tenantId = Guid.NewGuid();
        var captured = new CapturingRunbookService();
        var transport = new RunbookTransport(
            captured, NullLogger<RunbookTransport>.Instance);

        var sub = new EventSubscription
        {
            Name                = "t",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Runbook,
            TransportConfigJson = $$$"""
                {
                    "runbookId":     "{{{Guid.NewGuid()}}}",
                    "environmentId": "{{{Guid.NewGuid()}}}",
                    "targetId":      "{{{Guid.NewGuid()}}}",
                    "tenantId":      "{{{tenantId}}}"
                }
                """,
        };

        await transport.DeliverAsync(sub, NewEvent(), default);

        captured.TenantId.Should().Be(tenantId);
    }

    [Theory]
    [InlineData("""{"runbookId":"not-a-guid","environmentId":"00000000-0000-0000-0000-000000000001","targetId":"00000000-0000-0000-0000-000000000002"}""")]
    [InlineData("""{"runbookId":"00000000-0000-0000-0000-000000000001","environmentId":"not-a-guid","targetId":"00000000-0000-0000-0000-000000000002"}""")]
    [InlineData("""{"runbookId":"00000000-0000-0000-0000-000000000001","environmentId":"00000000-0000-0000-0000-000000000002","targetId":"not-a-guid"}""")]
    public async Task Malformed_guid_yields_failure_result(string configJson)
    {
        var transport = new RunbookTransport(
            new CapturingRunbookService(), NullLogger<RunbookTransport>.Instance);

        var sub = new EventSubscription
        {
            Name                = "t",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Runbook,
            TransportConfigJson = configJson,
        };

        var result = await transport.DeliverAsync(sub, NewEvent(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("GUID");
    }

    [Fact]
    public async Task Service_throws_InvalidOperationException_yields_failure()
    {
        // RunbookService.TriggerAsync throws InvalidOperationException for
        // operator-misconfiguration cases (runbook not found, target wrong
        // Space, ...). The transport must NOT propagate — the dispatcher
        // relies on the result shape, not exception handling.
        var throwing = new ThrowingRunbookService(
            new InvalidOperationException("Runbook 'foo' not found in Space X"));
        var transport = new RunbookTransport(
            throwing, NullLogger<RunbookTransport>.Instance);

        var sub = ValidSub();

        var result = await transport.DeliverAsync(sub, NewEvent(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("not found",
            "InvalidOperationException message surfaces verbatim so the " +
            "operator sees what went wrong in the delivery-history grid");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static EventSubscription ValidSub() => new()
    {
        Name                = "valid",
        SpaceId             = WellKnown.DefaultSpaceId,
        Transport           = SubscriptionTransport.Runbook,
        TransportConfigJson = $$$"""
            {
                "runbookId":     "{{{Guid.NewGuid()}}}",
                "environmentId": "{{{Guid.NewGuid()}}}",
                "targetId":      "{{{Guid.NewGuid()}}}"
            }
            """,
    };

    private static AuditEntry NewEvent() => new()
    {
        EventType   = "Deployment.Failed",
        OccurredUtc = DateTimeOffset.UtcNow,
        UserDisplay = "test",
        SpaceId     = WellKnown.DefaultSpaceId,
    };

    /// <summary>Stub that captures the arguments handed to TriggerAsync
    /// and returns a synthetic RunbookRun. The transport's job ends at
    /// "call the trigger" — the service's own correctness is its own
    /// test.</summary>
    private sealed class CapturingRunbookService : IRunbookTrigger
    {
        public Guid?  RunbookId      { get; private set; }
        public Guid?  EnvironmentId  { get; private set; }
        public Guid?  TargetId       { get; private set; }
        public Guid?  TenantId       { get; private set; }
        public TaskInitiator? Initiator { get; private set; }

        public Task<RunbookRun> TriggerAsync(
            Guid runbookId, Guid environmentId, Guid targetId,
            TaskInitiator initiator,
            Guid? tenantId = null, CancellationToken ct = default)
        {
            RunbookId     = runbookId;
            EnvironmentId = environmentId;
            TargetId      = targetId;
            TenantId      = tenantId;
            Initiator     = initiator;
            return Task.FromResult(new RunbookRun
            {
                Id            = Guid.NewGuid(),
                RunbookId     = runbookId,
                EnvironmentId = environmentId,
                Targets       = [new TaskTargetAssignment { TargetId = targetId, AddedUtc = DateTimeOffset.UtcNow }],
                TenantId      = tenantId,
            });
        }
    }

    private sealed class ThrowingRunbookService(Exception toThrow) : IRunbookTrigger
    {
        public Task<RunbookRun> TriggerAsync(
            Guid runbookId, Guid environmentId, Guid targetId,
            TaskInitiator initiator,
            Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromException<RunbookRun>(toThrow);
    }
}
