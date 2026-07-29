using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Ai;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Ai.Adhoc;
using KrakenDeploy.Server.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M11.E.13/14/15 — <see cref="AdhocSessionService"/>.
/// Exercises the full state machine: create → generate → approve → dispatch →
/// verdict → advance. Uses real Postgres for the aggregate, real
/// <see cref="AdhocScriptGate"/> (it's static), real
/// <see cref="AdhocScriptSigner"/> + provider with a generated keypair, fake
/// <see cref="IKrakenAi"/> for canned generation + verdict results, and a
/// fake <see cref="IAdhocDispatcher"/> for canned per-target results.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AdhocSessionServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.AdhocSessions.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.DeploymentTargets.IgnoreQueryFilters().ExecuteDeleteAsync();
        // Per-Space AI settings document drives the iteration cap — clear it so a
        // seeded override in one test can't leak into the config-fallback tests.
        await db.Set<Setting>().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Round-trip + sequencing ─────────────────────────────────────────────

    [Fact]
    public async Task Iter1_AllSucceeded_closes_session_with_one_iteration()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(
            generation: CannedGeneration("Get-Process"),
            verdict:    CannedVerdict("AllSucceeded", "Everything is fine."));

        var sessionId = await harness.Service.CreateSessionAsync(
            "list processes", AdhocMode.Readonly,
            [target.Id], Guid.NewGuid(), "ops@laus.hr", default);

        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);

        var outcome = await harness.Service.ApproveIterationAsync(
            sessionId, iterId, Guid.NewGuid(), "approver@laus.hr",
            editedScript: null, default);

        outcome.SessionStatus.Should().Be(AdhocSessionStatus.Closed);
        outcome.Verdict.Should().Be(AdhocVerdict.AllSucceeded);
        outcome.NextIterationId.Should().BeNull();

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.Include(s => s.Iterations)
            .SingleAsync(s => s.Id == sessionId);
        session.Iterations.Should().ContainSingle();
        session.Iterations[0].Verdict.Should().Be(AdhocVerdict.AllSucceeded);
        session.Iterations[0].ScriptSignature.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Iter1_partial_fail_then_iter2_fix_closes_with_two_iterations()
    {
        var target = await SeedTargetAsync("web-01");
        var aiSequence = new SequencedKrakenAi(
            new AdhocGenerationResult { GeneratedScript = "Get-Service" },            // iter 1 generation
            new IterationVerdict { Verdict = "ProposeFix",                            // iter 1 verdict
                                   ProposedScript = "Get-Service | Where-Object { $_.Name -eq 'w3svc' }",
                                   ProposedScriptDescription = "Filter to w3svc",
                                   Narrative = "One target reported missing service info" },
            new IterationVerdict { Verdict = "AllSucceeded", Narrative = "Resolved" }); // iter 2 verdict

        var harness = NewHarness(ai: aiSequence);

        var sessionId = await harness.Service.CreateSessionAsync(
            "check w3svc", AdhocMode.Readonly,
            [target.Id], Guid.NewGuid(), "ops", default);

        var iter1Id = await harness.Service.GenerateFirstIterationAsync(sessionId, default);
        var iter1Outcome = await harness.Service.ApproveIterationAsync(
            sessionId, iter1Id, Guid.NewGuid(), "ops", null, default);

        iter1Outcome.Verdict.Should().Be(AdhocVerdict.ProposeFix);
        iter1Outcome.NextIterationId.Should().NotBeNull();
        iter1Outcome.SessionStatus.Should().Be(AdhocSessionStatus.Active);

        var iter2Outcome = await harness.Service.ApproveIterationAsync(
            sessionId, iter1Outcome.NextIterationId!.Value,
            Guid.NewGuid(), "ops", null, default);

        iter2Outcome.SessionStatus.Should().Be(AdhocSessionStatus.Closed);
        iter2Outcome.Verdict.Should().Be(AdhocVerdict.AllSucceeded);
    }

    // ── Cap (M11.E.14) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Cap_reached_auto_closes_with_CapReached_status()
    {
        var target = await SeedTargetAsync("web-01");
        // Configure cap=2.
        var harness = NewHarness(
            maxIterations: 2,
            ai: new SequencedKrakenAi(
                new AdhocGenerationResult { GeneratedScript = "Get-Date" },               // iter 1 gen
                new IterationVerdict { Verdict = "ProposeFix",                            // iter 1 verdict
                                       ProposedScript = "Get-Date -UFormat %s",
                                       ProposedScriptDescription = "try unix format",
                                       Narrative = "needs another shot" },
                new IterationVerdict { Verdict = "ProposeFix",                            // iter 2 verdict
                                       ProposedScript = "Get-Date -Date '2026-01-01'",
                                       ProposedScriptDescription = "and another",
                                       Narrative = "still off" }));

        var sessionId = await harness.Service.CreateSessionAsync(
            "what's the date", AdhocMode.Readonly,
            [target.Id], Guid.NewGuid(), "ops", default);

        var iter1Id = await harness.Service.GenerateFirstIterationAsync(sessionId, default);
        var iter1 = await harness.Service.ApproveIterationAsync(
            sessionId, iter1Id, Guid.NewGuid(), "ops", null, default);
        var iter2 = await harness.Service.ApproveIterationAsync(
            sessionId, iter1.NextIterationId!.Value, Guid.NewGuid(), "ops", null, default);

        iter2.SessionStatus.Should().Be(AdhocSessionStatus.CapReached);
        iter2.NextIterationId.Should().BeNull();

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(AdhocSessionStatus.CapReached);
        session.MaxIterations.Should().Be(2);
    }

    // ── F5: the AI ad-hoc flow is READ-always ───────────────────────────────

    /// <summary>
    /// F5 / locked decision P5 — the AI session flow ALWAYS dispatches with
    /// <c>AllowParallelTaskExecution = true</c>, so the script takes the SHARED side of
    /// every agent's machine gate: it co-runs with other shared work and never excludes
    /// anything. Notably this ignores each target's own
    /// <c>DeploymentTarget.AllowParallelTaskExecution</c>, which F2 used to stamp
    /// per-target. That mapping was correct while the flag meant "bypass the gate" (a
    /// machine-local policy) but inverts the intent once it means "which SIDE": a
    /// serial target would turn an LLM-generated, gate-checked, operator-approved
    /// read-only diagnostic into an EXCLUSIVE holder that blocks live deployments.
    /// WP16's script console is where a per-run operator choice flows in instead.
    /// </summary>
    [Fact]
    public async Task Dispatch_of_an_ai_session_always_takes_the_shared_gate_side()
    {
        // One target opts into parallel execution, one does not — the AI flow must
        // send the same read-always mode regardless.
        var serial = await SeedTargetAsync("web-serial");
        var parallel = await SeedTargetAsync("web-parallel", allowParallelTaskExecution: true);
        var harness = NewHarness(
            generation: CannedGeneration("Get-Date"),
            verdict:    CannedVerdict("AllSucceeded"));

        var sessionId = await harness.Service.CreateSessionAsync(
            "what's the date", AdhocMode.Readonly,
            [serial.Id, parallel.Id], Guid.NewGuid(), "ops", default);
        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);
        await harness.Service.ApproveIterationAsync(
            sessionId, iterId, Guid.NewGuid(), "ops", null, default);

        harness.Dispatcher.LastAllowParallel.Should().BeTrue(
            "an approved AI ad-hoc script is read-always — the serial target's own " +
            "flag must not promote it to an exclusive holder");
    }

    // ── Gate invariants (M11.E.15) ──────────────────────────────────────────

    [Fact]
    public async Task Verdict_proposing_a_mode_escalation_fix_is_gate_rejected_and_closes_session()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(
            ai: new SequencedKrakenAi(
                new AdhocGenerationResult { GeneratedScript = "Get-Process" },     // iter 1 generation
                new IterationVerdict { Verdict = "ProposeFix",                      // mode-escalation attempt
                                       ProposedScript = "Stop-Service -Name w3svc",
                                       ProposedScriptDescription = "restart it",
                                       Narrative = "service stuck" }));

        var sessionId = await harness.Service.CreateSessionAsync(
            "check service", AdhocMode.Readonly,
            [target.Id], Guid.NewGuid(), "ops", default);

        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);
        var outcome = await harness.Service.ApproveIterationAsync(
            sessionId, iterId, Guid.NewGuid(), "ops", null, default);

        // Iter 1 itself completed; the gate rejected the proposed fix, so the
        // session closes without opening iter 2.
        outcome.SessionStatus.Should().Be(AdhocSessionStatus.Closed);
        outcome.NextIterationId.Should().BeNull();

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.Include(s => s.Iterations)
            .SingleAsync(s => s.Id == sessionId);
        session.Iterations.Should().ContainSingle(
            "the rejected proposed fix never becomes a real iteration row");
        // Audit row records the gate rejection.
        var auditDetails = await db.AuditEntries
            .Where(e => e.SubjectType == "AdhocSession" && e.SubjectId == sessionId.ToString()
                        && e.EventType == AuditEventType.AdhocGateRejected)
            .Select(e => e.Details)
            .ToListAsync();
        auditDetails.Should().NotBeEmpty();
        auditDetails[0].Should().Contain("ModeEscalation");
    }

    [Fact]
    public async Task Generation_of_a_forbidden_script_for_iter1_throws_gate_rejected()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(
            generation: new AdhocGenerationResult { GeneratedScript = "Invoke-Expression $x" },
            verdict:    CannedVerdict("AllSucceeded"));

        var sessionId = await harness.Service.CreateSessionAsync(
            "do a thing", AdhocMode.Mutating,
            [target.Id], Guid.NewGuid(), "ops", default);

        var act = async () => await harness.Service.GenerateFirstIterationAsync(sessionId, default);

        await act.Should().ThrowAsync<AdhocGateRejectedException>();

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.Include(s => s.Iterations)
            .SingleAsync(s => s.Id == sessionId);
        session.Iterations.Should().BeEmpty(
            "a gate-rejected generation never creates an iteration row");
    }

    [Fact]
    public async Task Approval_of_an_operator_edited_script_that_fails_gate_throws()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness();

        var sessionId = await harness.Service.CreateSessionAsync(
            "check", AdhocMode.Readonly, [target.Id], Guid.NewGuid(), "ops", default);
        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);

        // Operator "edits" the script to a mutating command before approving.
        var act = async () => await harness.Service.ApproveIterationAsync(
            sessionId, iterId, Guid.NewGuid(), "ops",
            editedScript: "Stop-Service -Name w3svc", default);

        await act.Should().ThrowAsync<AdhocGateRejectedException>();

        await using var db = postgres.CreateContext();
        var iter = await db.AdhocIterations.SingleAsync(i => i.Id == iterId);
        iter.Status.Should().Be(AdhocIterationStatus.PendingApproval,
            "a gate-rejected edit must NOT advance the iteration past PendingApproval");
        iter.ScriptSignature.Should().BeNull(
            "a rejected script is never signed");
    }

    // ── Operator-driven close paths ─────────────────────────────────────────

    [Fact]
    public async Task RejectIteration_marks_iteration_Rejected_and_leaves_session_Active()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness();

        var sessionId = await harness.Service.CreateSessionAsync(
            "x", AdhocMode.Readonly, [target.Id], Guid.NewGuid(), "ops", default);
        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);

        await harness.Service.RejectIterationAsync(sessionId, iterId, "ops", default);

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.Include(s => s.Iterations)
            .SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(AdhocSessionStatus.Active);
        session.Iterations.Single().Status.Should().Be(AdhocIterationStatus.Rejected);
    }

    [Fact]
    public async Task StopSession_transitions_to_OperatorStopped_and_is_idempotent()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness();
        var sessionId = await harness.Service.CreateSessionAsync(
            "x", AdhocMode.Readonly, [target.Id], Guid.NewGuid(), "ops", default);

        await harness.Service.StopSessionAsync(sessionId, "ops", default);
        await harness.Service.StopSessionAsync(sessionId, "ops", default); // idempotent

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(AdhocSessionStatus.OperatorStopped);
    }

    [Fact]
    public async Task MarkResolved_transitions_to_Closed()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness();
        var sessionId = await harness.Service.CreateSessionAsync(
            "x", AdhocMode.Readonly, [target.Id], Guid.NewGuid(), "ops", default);

        await harness.Service.MarkResolvedAsync(sessionId, "ops", default);

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(AdhocSessionStatus.Closed);
    }

    // ── CreateSession config + frozen JSON ──────────────────────────────────

    [Fact]
    public async Task CreateSession_persists_frozen_targets_and_configured_max_iterations()
    {
        var a = await SeedTargetAsync("a");
        var b = await SeedTargetAsync("b");
        var harness = NewHarness(maxIterations: 7);

        var sessionId = await harness.Service.CreateSessionAsync(
            "audit", AdhocMode.Mutating,
            [a.Id, b.Id], Guid.NewGuid(), "ops@laus.hr", default);

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.SingleAsync(s => s.Id == sessionId);
        session.Mode.Should().Be(AdhocMode.Mutating);
        session.MaxIterations.Should().Be(7);
        session.CreatedByDisplay.Should().Be("ops@laus.hr");
        var ids = JsonSerializer.Deserialize<List<Guid>>(session.FrozenTargetSetJson)!;
        ids.Should().BeEquivalentTo(new[] { a.Id, b.Id });
    }

    [Fact]
    public async Task CreateSession_with_empty_target_set_is_rejected()
    {
        var harness = NewHarness();
        var act = async () => await harness.Service.CreateSessionAsync(
            "x", AdhocMode.Readonly, [], Guid.NewGuid(), "ops", default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateSession_uses_per_Space_AdhocMaxIterations_over_config()
    {
        var target = await SeedTargetAsync("web-01");
        // Per-Space setting says 3; deployment-wide config says 9 — the
        // per-Space value must win (SaaS: every Space tunes its own cap).
        await SeedSpaceAdhocMaxIterationsAsync(3);
        var harness = NewHarness(maxIterations: 9);

        var sessionId = await harness.Service.CreateSessionAsync(
            "audit", AdhocMode.Readonly,
            [target.Id], Guid.NewGuid(), "ops@laus.hr", default);

        await using var db = postgres.CreateContext();
        var session = await db.AdhocSessions.SingleAsync(s => s.Id == sessionId);
        session.MaxIterations.Should().Be(3,
            "the per-Space SpaceAiSettings.AdhocMaxIterations overrides the " +
            "deployment-wide config fallback");
    }

    // ── Two-person approval (M11.E.11) ───────────────────────────────────────

    [Fact]
    public async Task TwoPerson_mutating_requires_two_distinct_approvers()
    {
        var target = await SeedTargetAsync("web-01");
        await EnableTwoPersonAsync();
        var harness = NewHarness();
        var creator = Guid.NewGuid();

        var sessionId = await harness.Service.CreateSessionAsync(
            "do x", AdhocMode.Mutating, [target.Id], creator, "creator@laus.hr", default);
        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);

        // First approval by user A → awaits a second approver; nothing dispatched.
        var userA = Guid.NewGuid();
        var first = await harness.Service.ApproveIterationAsync(
            sessionId, iterId, userA, "a@laus.hr", null, default);
        first.AwaitingSecondApproval.Should().BeTrue();
        first.SessionStatus.Should().Be(AdhocSessionStatus.Active);

        await using (var db = postgres.CreateContext())
        {
            var it = await db.AdhocIterations.SingleAsync(i => i.Id == iterId);
            it.Status.Should().Be(AdhocIterationStatus.PendingSecondApproval);
            it.ScriptSignature.Should().BeNull("the script is not signed until the second approval");
            it.FirstApprovedByUserId.Should().Be(userA);
        }

        // Second approval by a distinct user B → signs, dispatches, completes.
        var userB = Guid.NewGuid();
        var second = await harness.Service.ApproveIterationAsync(
            sessionId, iterId, userB, "b@laus.hr", null, default);
        second.AwaitingSecondApproval.Should().BeFalse();
        second.Verdict.Should().Be(AdhocVerdict.AllSucceeded);

        await using (var db = postgres.CreateContext())
        {
            var it = await db.AdhocIterations.SingleAsync(i => i.Id == iterId);
            it.Status.Should().Be(AdhocIterationStatus.Completed);
            it.ScriptSignature.Should().NotBeNullOrEmpty();
            it.ApprovedByUserId.Should().Be(userB);
        }
    }

    [Fact]
    public async Task TwoPerson_second_approver_must_differ_from_first()
    {
        var target = await SeedTargetAsync("web-01");
        await EnableTwoPersonAsync();
        var harness = NewHarness();

        var sessionId = await harness.Service.CreateSessionAsync(
            "x", AdhocMode.Mutating, [target.Id], Guid.NewGuid(), "creator", default);
        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);
        var userA = Guid.NewGuid();
        await harness.Service.ApproveIterationAsync(sessionId, iterId, userA, "a", null, default);

        var act = async () => await harness.Service.ApproveIterationAsync(
            sessionId, iterId, userA, "a", null, default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different person*");
    }

    [Fact]
    public async Task TwoPerson_second_approver_must_not_be_creator()
    {
        var target = await SeedTargetAsync("web-01");
        await EnableTwoPersonAsync();
        var harness = NewHarness();
        var creator = Guid.NewGuid();

        var sessionId = await harness.Service.CreateSessionAsync(
            "x", AdhocMode.Mutating, [target.Id], creator, "creator", default);
        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);
        await harness.Service.ApproveIterationAsync(sessionId, iterId, Guid.NewGuid(), "a", null, default);

        var act = async () => await harness.Service.ApproveIterationAsync(
            sessionId, iterId, creator, "creator", null, default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*creator*");
    }

    [Fact]
    public async Task TwoPerson_readonly_nonprod_stays_single_approver()
    {
        var target = await SeedTargetAsync("web-01", TargetRiskLevel.Development);
        await EnableTwoPersonAsync();
        var harness = NewHarness();

        var sessionId = await harness.Service.CreateSessionAsync(
            "list", AdhocMode.Readonly, [target.Id], Guid.NewGuid(), "creator", default);
        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);

        var outcome = await harness.Service.ApproveIterationAsync(
            sessionId, iterId, Guid.NewGuid(), "a", null, default);
        outcome.AwaitingSecondApproval.Should().BeFalse(
            "readonly + non-Production doesn't meet the two-person trigger");
        outcome.Verdict.Should().Be(AdhocVerdict.AllSucceeded);
    }

    [Fact]
    public async Task TwoPerson_readonly_production_target_requires_two_person()
    {
        var dev  = await SeedTargetAsync("dev-01",  TargetRiskLevel.Development);
        var prod = await SeedTargetAsync("prod-01", TargetRiskLevel.Production);
        await EnableTwoPersonAsync();
        var harness = NewHarness();

        var sessionId = await harness.Service.CreateSessionAsync(
            "list", AdhocMode.Readonly, [dev.Id, prod.Id], Guid.NewGuid(), "creator", default);
        var iterId = await harness.Service.GenerateFirstIterationAsync(sessionId, default);

        var first = await harness.Service.ApproveIterationAsync(
            sessionId, iterId, Guid.NewGuid(), "a", null, default);
        first.AwaitingSecondApproval.Should().BeTrue(
            "one Production target makes the session's max risk Production → two-person even in readonly");
    }

    [Fact]
    public async Task GetEffectiveRisk_is_max_across_frozen_targets()
    {
        var dev  = await SeedTargetAsync("dev",  TargetRiskLevel.Development);
        var prod = await SeedTargetAsync("prod", TargetRiskLevel.Production);
        var harness = NewHarness();

        var sessionId = await harness.Service.CreateSessionAsync(
            "x", AdhocMode.Readonly, [dev.Id, prod.Id], Guid.NewGuid(), "c", default);

        (await harness.Service.GetEffectiveRiskAsync(sessionId))
            .Should().Be(TargetRiskLevel.Production);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<DeploymentTarget> SeedTargetAsync(
        string name, TargetRiskLevel risk = TargetRiskLevel.Development,
        bool allowParallelTaskExecution = false)
    {
        await using var db = postgres.CreateContext();
        var t = new DeploymentTarget
        {
            Name = name,
            OperatingSystem = "Windows Server 2022",
            Roles = ["web"],
            Status = TargetStatus.Online,
            RiskLevel = risk,
            AllowParallelTaskExecution = allowParallelTaskExecution,
        };
        db.DeploymentTargets.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    private Task EnableTwoPersonAsync() =>
        new SettingsService(postgres.ScopeFactory, TimeProvider.System).SaveAsync(
            new SpaceAiSettings
            {
                Provider               = KrakenAiProviderValue.Anthropic,
                AdhocEnabled           = true,
                AdhocTwoPersonApproval = true,
            },
            WellKnown.DefaultSpaceId);

    private Task SeedSpaceAdhocMaxIterationsAsync(int value) =>
        new SettingsService(postgres.ScopeFactory, TimeProvider.System).SaveAsync(
            new SpaceAiSettings
            {
                Provider           = KrakenAiProviderValue.Anthropic,
                AdhocEnabled       = true,
                AdhocMaxIterations = value,
            },
            WellKnown.DefaultSpaceId);

    private sealed record Harness(AdhocSessionService Service, FakeAdhocDispatcher Dispatcher);

    private Harness NewHarness(
        AdhocGenerationResult? generation = null,
        IterationVerdict? verdict = null,
        IKrakenAi? ai = null,
        int? maxIterations = null)
    {
        var krakenAi = ai ?? new SequencedKrakenAi(
            (object?)generation ?? new AdhocGenerationResult { GeneratedScript = "Get-Date" },
            (object?)verdict ?? new IterationVerdict { Verdict = "AllSucceeded", Narrative = "ok" });

        var logger = NullLogger<AdhocSessionService>.Instance;
        var genSvc = new AdhocGenerationService(krakenAi, NullLogger<AdhocGenerationService>.Instance);
        var verdictSvc = new AdhocVerdictService(krakenAi, NullLogger<AdhocVerdictService>.Instance);

        // Generate a one-off signing key for this harness.
        using var src = RSA.Create(2048);
        var configValues = new Dictionary<string, string?>
        {
            ["Adhoc:SigningKey"] = src.ExportRSAPrivateKeyPem(),
        };
        if (maxIterations is not null)
        {
            configValues["Ai:Adhoc:MaxIterationsPerSession"] =
                maxIterations.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues).Build();

        var keyProvider = new AdhocSigningKeyProvider(config);
        var dispatcher = new FakeAdhocDispatcher();
        var audit = new RecordingAuditLog(postgres);

        var service = new AdhocSessionService(
            postgres,
            new SettingsService(postgres.ScopeFactory, TimeProvider.System),
            genSvc, verdictSvc, keyProvider, dispatcher,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            audit, config, TimeProvider.System, logger);
        return new Harness(service, dispatcher);
    }

    private static AdhocGenerationResult CannedGeneration(string script)
        => new() { GeneratedScript = script };

    private static IterationVerdict CannedVerdict(string verdict, string narrative = "")
        => new() { Verdict = verdict, Narrative = narrative };

    /// <summary>
    /// Hands out responses in the order they were given to the constructor.
    /// Each call to <see cref="CompleteAsync{TResult}"/> pops the next item,
    /// regardless of whether the caller expects a generation or verdict shape
    /// — the orchestrator's call order (gen → verdict → gen → verdict …) is
    /// what the test feeds in.
    /// </summary>
    private sealed class SequencedKrakenAi(params object?[] responses) : IKrakenAi
    {
        private int _idx;

        public Task<TResult> CompleteAsync<TResult>(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            where TResult : class
        {
            if (_idx >= responses.Length)
            {
                throw new InvalidOperationException(
                    $"SequencedKrakenAi: ran out of responses (had {responses.Length}).");
            }
            var next = responses[_idx++];
            return Task.FromResult((TResult)next!);
        }

        public Task<KrakenAiCompletion> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Fake dispatcher that returns one success result per target in
    /// the frozen set — enough for the orchestrator to advance through the
    /// verdict + state-machine without hitting a real agent.</summary>
    private sealed class FakeAdhocDispatcher : IAdhocDispatcher
    {
        /// <summary>F5 — the gate mode the service asked for on the last dispatch, so a
        /// test can assert the AI flow's read-always rule without a live agent.</summary>
        public bool? LastAllowParallel { get; private set; }

        public Task<IReadOnlyList<AdhocPerTargetResult>> DispatchAsync(
            AdhocSession session, AdhocIteration iteration, Guid dispatchAccountId,
            bool allowParallelTaskExecution,
            CancellationToken ct, TimeSpan? timeout = null)
        {
            LastAllowParallel = allowParallelTaskExecution;
            var ids = JsonSerializer.Deserialize<List<Guid>>(session.FrozenTargetSetJson) ?? [];
            var results = ids
                .Select(id => new AdhocPerTargetResult(id, new AdhocScriptResult(
                    session.Id, iteration.IterNumber, ExitCode: 0,
                    Stdout: $"ok on {id:N}", Stderr: "", AgentError: null)))
                .ToList();
            return Task.FromResult<IReadOnlyList<AdhocPerTargetResult>>(results);
        }
    }

    /// <summary>
    /// Minimal IAuditLog that writes rows straight to Postgres so tests can
    /// query the audit trail. The production wire uses an EF interceptor +
    /// AuditLog service; we don't need that whole chain here.
    /// </summary>
    private sealed class RecordingAuditLog(PostgresFixture postgres) : IAuditLog
    {
        public async Task RecordAsync(
            string eventType,
            string? subjectType = null,
            string? subjectId = null,
            string? subjectName = null,
            string? details = null,
            Guid? userId = null,
            string? userDisplay = null,
            CancellationToken ct = default)
        {
            await using var db = postgres.CreateContext();
            db.AuditEntries.Add(new AuditEntry
            {
                EventType   = eventType,
                SubjectType = subjectType,
                SubjectId   = subjectId,
                SubjectName = subjectName,
                Details     = details,
                OccurredUtc = DateTimeOffset.UtcNow,
                SpaceId     = WellKnown.DefaultSpaceId,
                UserId      = userId,
                UserDisplay = userDisplay ?? "test",
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
