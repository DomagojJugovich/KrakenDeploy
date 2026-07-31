using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// FAST (non-Docker) contract tests for WP3's manual-intervention pieces. These matter
/// disproportionately because the CI Windows leg filters <c>Category!=Docker</c>: without
/// them a green Windows run says nothing at all about the approval gate.
/// </summary>
public sealed class ManualInterventionContractTests
{
    // SC4-b: the registry-driven server-side set, exactly as the orchestrator
    // loads it (Octopus.Manual declares executionLocus=server in its manifest;
    // Octopus.DeployRelease is a System registry row).
    private static readonly HashSet<string> ServerSideTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            DeployReleaseStepRunner.StepType,
            ManualInterventionConfigKeys.StepType,
        };

    // ── The step type is server-only ────────────────────────────────────────

    [Fact]
    public void Octopus_Manual_is_classified_server_side()
    {
        // A task-global pause cannot be a per-target agent step: the gate has to stop
        // the ORCHESTRATION, and a per-target step could not (it would also need N
        // approvals for N targets).
        var gate = new DeploymentStepPlan(
            Index: 0, Name: "approve", StepType: ManualInterventionConfigKeys.StepType,
            PackageId: "", PackageVersion: "",
            Config: new Dictionary<string, string>());

        // Asserted through the public partitioner rather than the internal classifier:
        // a lone gate must land in a SERVER wave, which is what actually makes the
        // task-global pause possible.
        var waves = WavePartitioner.Partition(
            [gate],
            triggerByIndex: _ =>
                KrakenDeploy.Server.Core.Domain.Processes.StepStartTrigger.StartAfterPrevious,
            serverSideTypes: ServerSideTypes);

        waves.Should().ContainSingle()
            .Which.Kind.Should().Be(WavePartitioner.WaveKind.Server);
    }

    [Fact]
    public void A_gate_beside_a_target_step_in_one_wave_is_refused_as_mixed()
    {
        // Documented consequence of making the step server-only (design doc D2): a
        // Manual step marked StartWithPrevious next to a target-side step is now a
        // MIXED wave and fails loudly at partition time rather than silently
        // reordering the operator's process. Pinned so the behaviour change is not
        // discovered in production.
        var gate = new DeploymentStepPlan(
            Index: 0, Name: "approve", StepType: ManualInterventionConfigKeys.StepType,
            PackageId: "", PackageVersion: "", Config: new Dictionary<string, string>());
        var agentStep = new DeploymentStepPlan(
            Index: 1, Name: "deploy", StepType: "Octopus.Script",
            PackageId: "", PackageVersion: "", Config: new Dictionary<string, string>());

        var act = () => WavePartitioner.Partition(
            [gate, agentStep],
            triggerByIndex: i => i == 1
                ? KrakenDeploy.Server.Core.Domain.Processes.StepStartTrigger.StartWithPrevious
                : KrakenDeploy.Server.Core.Domain.Processes.StepStartTrigger.StartAfterPrevious,
            serverSideTypes: ServerSideTypes);

        act.Should().Throw<WavePartitioner.InvalidWaveException>()
            .Which.ServerStepNames.Should().Contain("approve");
    }

    // ── Config-key parsing ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Octopus.Action.Manual.ResponsibleTeamIds")]
    [InlineData("octopus.action.manual.responsibleteamids")]
    [InlineData("OCTOPUS.ACTION.MANUAL.RESPONSIBLETEAMIDS")]
    public void The_approver_key_is_read_case_insensitively(string key)
    {
        // SECURITY-CRITICAL. step.Config is a jsonb-deserialised dictionary with the
        // DEFAULT ordinal comparer, seeded from a caller-supplied AddStepRequest.Config.
        // A casing miss used to return null -> zero tokens -> EMPTY responsible-team
        // list -> "anyone holding the respond permission", silently widening the
        // approver set while the step editor still displayed the restriction. This key
        // is the one that fails OPEN, so it must never depend on casing.
        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key] = "11111111-1111-1111-1111-111111111111",
        };

        ManualInterventionConfigKeys
            .Read(config, ManualInterventionConfigKeys.ResponsibleTeamIds)
            .Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("0.5", 0.5)]
    [InlineData("8760", 8760)]
    public void A_valid_timeout_parses(string raw, double expectedHours)
        => ManualInterventionConfigKeys.ParseTimeout(raw)
            .Should().Be(TimeSpan.FromHours(expectedHours));

    [Fact]
    public void Zero_is_not_a_usable_timeout()
        // WP3-b reversal. "0" used to parse to TimeSpan.Zero and mean "wait forever",
        // which the gate turned into a NULL ExpiresUtc. InterruptionTimeoutJob filters on
        // `ExpiresUtc != null`, so such a gate was never reaped — and because Paused is in
        // InFlightAfterClaim, its task held the F1 (project, environment, tenant) key for
        // as long as it waited. A step author with only ProcessEdit could therefore block
        // every later release of a project+environment until someone with TaskCancel
        // intervened. Every gate must be bounded.
        => ManualInterventionConfigKeys.ParseTimeout("0").Should().BeNull(
            because: "an unexpiring gate parks its task on the F1 slot indefinitely");

    [Theory]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("NaN")]
    [InlineData("1e30")]
    [InlineData("99999999")]
    [InlineData("-4")]
    [InlineData("not-a-number")]
    [InlineData("0,5")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unusable_timeout_falls_back_rather_than_throwing(string? raw)
    {
        // NumberStyles.Float + invariant culture accepts "Infinity" and "1e30"; both
        // passed a bare `>= 0` test and then threw OverflowException /
        // ArgumentOutOfRangeException out of the gate, failing the deployment with a raw
        // framework message instead of pausing. "0,5" is the Croatian decimal comma — it
        // does not parse invariantly, and since WP3-a that no longer degrades silently:
        // ResponsibleTeamResolver.ValidateStepConfigAsync refuses the save. Parsing stays
        // invariant because the value lives in a process document that must mean the same
        // thing on every machine.
        var act = () => ManualInterventionConfigKeys.ParseTimeout(raw);
        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void The_timeout_ceiling_is_inside_the_arithmetic_that_consumes_it()
    {
        // The guard is only worth having if the accepted maximum cannot itself overflow
        // `now + timeout`.
        var max = TimeSpan.FromHours(ManualInterventionConfigKeys.MaxTimeoutHours);
        var act = () => DateTimeOffset.UtcNow + max;
        act.Should().NotThrow();
    }

    // ── Interruption status semantics ───────────────────────────────────────

    [Theory]
    [InlineData(InterruptionStatus.Approved, true)]
    [InlineData(InterruptionStatus.Rejected, true)]
    [InlineData(InterruptionStatus.TimedOut, true)]
    [InlineData(InterruptionStatus.Cancelled, false)]
    [InlineData(InterruptionStatus.Pending, false)]
    public void Only_real_answers_count_as_a_decision(InterruptionStatus status, bool expected)
        // Cancelled is RESOLVED but is not a DECISION: the task went terminal underneath
        // the gate, so nothing resumes and nothing may be recorded as an approval or a
        // refusal. Conflating the two let a cancelled deployment be "approved".
        => status.IsDecision().Should().Be(expected);

    [Theory]
    [InlineData(InterruptionStatus.Pending, false)]
    [InlineData(InterruptionStatus.Approved, true)]
    [InlineData(InterruptionStatus.Rejected, true)]
    [InlineData(InterruptionStatus.TimedOut, true)]
    [InlineData(InterruptionStatus.Cancelled, true)]
    public void Everything_but_Pending_is_resolved(InterruptionStatus status, bool expected)
        => status.IsResolved().Should().Be(expected);

    [Fact]
    public void Cancelled_has_no_resolution_audit_event()
    {
        // The cancel is already audited (Deployment.Cancelled); emitting an intervention
        // event too would put a second, misleading change-control row in front of a
        // reviewer.
        var act = () => InterruptionAuditEvents.For(
            ServerTaskKind.Deployment, InterruptionStatus.Cancelled);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(ServerTaskKind.Deployment, InterruptionStatus.Approved, "Deployment.InterventionApproved")]
    [InlineData(ServerTaskKind.Deployment, InterruptionStatus.Rejected, "Deployment.InterventionRejected")]
    [InlineData(ServerTaskKind.Deployment, InterruptionStatus.TimedOut, "Deployment.InterventionTimedOut")]
    [InlineData(ServerTaskKind.RunbookRun, InterruptionStatus.Approved, "RunbookRun.InterventionApproved")]
    [InlineData(ServerTaskKind.RunbookRun, InterruptionStatus.Rejected, "RunbookRun.InterventionRejected")]
    [InlineData(ServerTaskKind.RunbookRun, InterruptionStatus.TimedOut, "RunbookRun.InterventionTimedOut")]
    public void Resolution_events_are_kind_correct_and_not_transposed(
        ServerTaskKind kind, InterruptionStatus status, string expected)
        // All six branches, because SubscriptionMatcher matches on the event-type PREFIX:
        // a transposed Approved/Rejected pair would notify reviewers that a REFUSED
        // change was approved, and a reused Deployment.* name would leak runbook events
        // into every Deployment.* subscription.
        => InterruptionAuditEvents.For(kind, status).Should().Be(expected);

    // ── Paused is non-terminal but holds the F1 key ──────────────────────────

    [Fact]
    public void Paused_is_non_terminal_and_in_flight()
    {
        DeploymentStatus.Paused.IsTerminal().Should().BeFalse(
            because: "a paused task is parked, not finished");
        DeploymentStatusExtensions.InFlightAfterClaim.Should().Contain(DeploymentStatus.Paused,
            because: "releasing the (project, environment, tenant) key would let a newer " +
                     "release deploy and complete during the approval window, after which " +
                     "the approved older release would overwrite newer code");
    }

    // ── WP3-b ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Octopus.Manual")]
    [InlineData("octopus.manual")]
    [InlineData("OCTOPUS.MANUAL")]
    public void The_offline_drop_refusal_is_case_insensitive(string stepType)
    {
        // GATE BYPASS. The refusal used a C# `is` constant pattern, which is ORDINAL and
        // case-sensitive, while every other WP3 comparison — WavePartitioner's
        // ServerOnlyStepTypes, GateStepsIn, the server-step guard, the step package's
        // CanHandle — is OrdinalIgnoreCase. ProcessService stores StepType verbatim with no
        // allow-list, so a step added as "octopus.manual" via REST, MCP or an import still
        // gated online (nothing looked wrong) yet slipped past this refusal into an offline
        // bundle, where the air-gapped handler logs "APPROVAL NOT ENFORCED" and returns
        // true: deployment complete, no Interruption row, no audit event, no step outcome.
        OfflineDropBundleBuilder.IsOnlineOnlyStepType(stepType).Should().BeTrue(
            because: "casing must never decide whether a change-control gate is enforced");
    }

    [Fact]
    public void The_offline_drop_refusal_also_covers_DeployRelease()
        => OfflineDropBundleBuilder.IsOnlineOnlyStepType("octopus.deployrelease")
            .Should().BeTrue();

    [Fact]
    public void A_normal_step_is_not_refused_offline()
        => OfflineDropBundleBuilder.IsOnlineOnlyStepType("Octopus.Script").Should().BeFalse();

    [Fact]
    public void Every_resolution_event_type_is_in_the_change_control_retention_set()
    {
        // The retention class and the event-type factory must not drift. An event type
        // emitted by For() but missing here would silently fall back to the ordinary
        // 365-day audit window — and since the interruptions row is CASCADE-deleted with
        // its task, that entry is the LAST copy of the approval.
        var emitted = new[] { ServerTaskKind.Deployment, ServerTaskKind.RunbookRun }
            .SelectMany(kind => new[]
            {
                InterruptionStatus.Approved,
                InterruptionStatus.Rejected,
                InterruptionStatus.TimedOut,
            }.Select(status => InterruptionAuditEvents.For(kind, status)))
            .ToList();

        InterruptionAuditEvents.ChangeControlEventTypes.Should().BeEquivalentTo(emitted,
            because: "the retention exemption is what makes the audit entry the durable " +
                     "change-control record");
    }
}
