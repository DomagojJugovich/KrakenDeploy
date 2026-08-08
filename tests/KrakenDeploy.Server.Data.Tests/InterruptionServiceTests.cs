using System.Security.Claims;
using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP3 — <see cref="InterruptionService"/>: the authority for answering a
/// manual-intervention gate.
/// <para>
/// The interesting cases are all authorization and race-shaped, because a
/// change-control gate that the wrong person can answer, or that two people can
/// answer differently, is worse than no gate at all:
/// </para>
/// <list type="bullet">
///   <item>the scoped permission AND responsible-team membership are BOTH required;</item>
///   <item>notes are mandatory on reject at the SERVICE, not just in the dialog;</item>
///   <item>a second response loses to the first (conditional UPDATE), and so does the
///     timeout sweeper;</item>
///   <item>a resolved gate enqueues exactly one resume wake-up.</item>
/// </list>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class InterruptionServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static readonly Guid ResponderId = Guid.CreateVersion7();

    // ── Authorization ───────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_requires_the_scoped_respond_permission()
    {
        var (harness, gateId, _) = await PausedDeploymentAsync("dn");
        await using var _h = harness;

        var svc = NewService(new DenyAllPermissionEvaluator(), out _);

        await FluentActions
            .Awaiting(() => svc.ApproveAsync(gateId, notes: null, Caller()))
            .Should().ThrowAsync<AuthorizationException>();

        (await harness.GetInterruptionsAsync((await GateAsync(harness, gateId)).TaskId))
            .Single().Status.Should().Be(InterruptionStatus.Pending,
                because: "a denied response must not record a decision");
    }

    [Fact]
    public async Task Responding_to_a_team_restricted_gate_requires_membership_of_a_listed_team()
    {
        var (harness, gateId, _) = await PausedDeploymentAsync("tg");
        await using var _h = harness;

        // Restrict the gate to a team the caller is NOT in. The permission passes; the
        // team gate must not.
        var otherTeamId = Guid.CreateVersion7();
        await SetResponsibleTeamsAsync(harness, gateId, otherTeamId);

        // Holds the respond permission but is NOT a system administrator — otherwise
        // WP3's AdministerSystem break-glass override would let this caller through and
        // the test would pass for the wrong reason.
        var outsider = NewService(
            new AllowAllPermissionEvaluator
            {
                TeamIds = [Guid.CreateVersion7()],
                Denied  = [Permission.AdministerSystem],
            }, out _);

        await FluentActions
            .Awaiting(() => outsider.ApproveAsync(gateId, notes: null, Caller()))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        (await svcCanRespond(outsider)).Should().BeFalse(
            because: "the UI must not offer buttons the service will refuse");

        // Same permission, but now a member of the listed team → allowed.
        var member = NewService(
            new AllowAllPermissionEvaluator
            {
                TeamIds = [otherTeamId],
                Denied  = [Permission.AdministerSystem],
            }, out _);
        (await svcCanRespond(member)).Should().BeTrue();
        var approved = await member.ApproveAsync(gateId, notes: null, Caller());
        approved.Status.Should().Be(InterruptionStatus.Approved);

        Task<bool> svcCanRespond(InterruptionService s) => s.CanRespondAsync(gateId, Caller());
    }

    [Fact]
    public async Task An_empty_responsible_team_list_lets_any_permitted_user_respond()
    {
        var (harness, gateId, _) = await PausedDeploymentAsync("et");
        await using var _h = harness;

        // No teams on the gate (the orchestrator's default) + no team memberships.
        var svc = NewService(new AllowAllPermissionEvaluator(), out _);

        (await svc.CanRespondAsync(gateId, Caller())).Should().BeTrue();
        (await svc.ApproveAsync(gateId, notes: null, Caller())).Status
            .Should().Be(InterruptionStatus.Approved);
    }

    [Fact]
    public async Task A_system_administrator_can_break_glass_on_a_stranded_gate()
    {
        // Responsible teams are an FK-free snapshot (so deleting a team cannot rewrite
        // history), which meant deleting the named team left the gate unanswerable by
        // EVERYONE — including a sysadmin — while it kept holding the (project,
        // environment, tenant) slot until its timeout. A holder of TeamDelete alone
        // could therefore force-fail a release and block that environment.
        var (harness, gateId, taskId) = await PausedDeploymentAsync("bg");
        await using var _h = harness;

        // A team id that resolves to nothing — the deleted-team situation.
        await SetResponsibleTeamsAsync(harness, gateId, Guid.CreateVersion7());

        var sysadmin = NewService(new AllowAllPermissionEvaluator(), out _);
        (await sysadmin.CanRespondAsync(gateId, Caller("root"))).Should().BeTrue(
            because: "AdministerSystem is the break-glass path out of a stranded gate");

        await sysadmin.ApproveAsync(gateId, notes: null, Caller("root"));

        (await GateAsync(harness, gateId)).Status
            .Should().Be(InterruptionStatus.Approved);

        // And the override must be visible in the trail — never a silent widening.
        await using var db = harness.CreateContext();
        var details = await db.AuditEntries.IgnoreQueryFilters()
            .Where(a => a.SubjectId == taskId.ToString()
                     && a.EventType == AuditEventType.DeploymentInterventionApproved)
            .Select(a => a.Details)
            .ToListAsync();
        details.Should().ContainSingle()
            .Which.Should().Contain("Override=AdministerSystem",
                because: "a reviewer must be able to see the gate was answered by " +
                         "override rather than by a responsible team member");
    }

    // ── Notes on reject ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reject_without_notes_is_refused_by_the_service(string? notes)
    {
        var (harness, gateId, _) = await PausedDeploymentAsync("nn");
        await using var _h = harness;
        var svc = NewService(new AllowAllPermissionEvaluator(), out _);

        await FluentActions
            .Awaiting(() => svc.RejectAsync(gateId, notes!, Caller()))
            .Should().ThrowAsync<ArgumentException>(
                because: "'why was this change refused' is the line a reviewer reads, so a " +
                         "REST or CLI caller must not be able to skip it either");

        (await GateAsync(harness, gateId)).Status.Should().Be(InterruptionStatus.Pending);
    }

    [Fact]
    public async Task Reject_with_notes_records_the_reason_and_the_responder()
    {
        var (harness, gateId, _) = await PausedDeploymentAsync("rn");
        await using var _h = harness;
        var svc = NewService(new AllowAllPermissionEvaluator(), out _);

        await svc.RejectAsync(gateId, "Change window closed.", Caller("Ana Anić"));

        var gate = await GateAsync(harness, gateId);
        gate.Status.Should().Be(InterruptionStatus.Rejected);
        gate.Notes.Should().Be("Change window closed.");
        gate.ActedByDisplay.Should().Be("Ana Anić");
        gate.ActedByUserId.Should().Be(ResponderId);
        gate.ActedUtc.Should().NotBeNull();
    }

    // ── Races ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_response_loses_to_the_first()
    {
        var (harness, gateId, _) = await PausedDeploymentAsync("rc");
        await using var _h = harness;
        var svc = NewService(new AllowAllPermissionEvaluator(), out _);

        await svc.ApproveAsync(gateId, notes: null, Caller("first"));

        await FluentActions
            .Awaiting(() => svc.RejectAsync(gateId, "changed my mind", Caller("second")))
            .Should().ThrowAsync<InvalidOperationException>();

        var gate = await GateAsync(harness, gateId);
        gate.Status.Should().Be(InterruptionStatus.Approved,
            because: "the recorded decision stands — a gate is answered exactly once");
        gate.ActedByDisplay.Should().Be("first");
    }

    [Fact]
    public async Task The_timeout_sweeper_loses_to_a_human_who_answered_first()
    {
        var (harness, gateId, _) = await PausedDeploymentAsync("ts");
        await using var _h = harness;
        var svc = NewService(new AllowAllPermissionEvaluator(), out _);

        await svc.ApproveAsync(gateId, notes: null, Caller("alice"));
        // Backdate the expiry so the sweeper considers it due.
        await ExpireNowAsync(harness, gateId);

        await NewTimeoutJob(svc).ExecuteAsync(CancellationToken.None);

        var gate = await GateAsync(harness, gateId);
        gate.Status.Should().Be(InterruptionStatus.Approved,
            because: "the sweeper's conditional Pending→TimedOut update must not overwrite " +
                     "a decision a human already made");
        gate.ActedByDisplay.Should().Be("alice");
    }

    [Fact]
    public async Task The_timeout_sweeper_expires_an_unanswered_gate_and_signals_a_resume()
    {
        var (harness, gateId, taskId) = await PausedDeploymentAsync("tx");
        await using var _h = harness;
        var svc = NewService(new AllowAllPermissionEvaluator(), out var queue);

        await ExpireNowAsync(harness, gateId);
        await NewTimeoutJob(svc).ExecuteAsync(CancellationToken.None);

        var gate = await GateAsync(harness, gateId);
        gate.Status.Should().Be(InterruptionStatus.TimedOut);
        gate.ActedByUserId.Should().BeNull(because: "nobody acted");
        gate.ActedByDisplay.Should().Contain("timeout",
            because: "a reviewer must be able to tell 'nobody responded' from 'someone refused'");

        queue.Reader.TryRead(out var item).Should().BeTrue(
            because: "the task must be woken so it fails with its cleanup steps");
        item.Id.Should().Be(taskId);

        // A gate that is NOT yet due must be left alone.
        var (harness2, gate2Id, _) = await PausedDeploymentAsync("tn");
        await using var _h2 = harness2;
        await NewTimeoutJob(NewService(new AllowAllPermissionEvaluator(), out _))
            .ExecuteAsync(CancellationToken.None);
        (await GateAsync(harness2, gate2Id)).Status.Should().Be(InterruptionStatus.Pending);
    }

    [Fact]
    public async Task Responding_enqueues_exactly_one_resume_wake_up_and_audits_at_decision_time()
    {
        var (harness, gateId, taskId) = await PausedDeploymentAsync("wk");
        await using var _h = harness;
        var svc = NewService(new AllowAllPermissionEvaluator(), out var queue);

        await svc.ApproveAsync(gateId, notes: null, Caller());

        queue.Reader.TryRead(out var item).Should().BeTrue();
        item.Id.Should().Be(taskId);
        queue.Reader.TryRead(out _).Should().BeFalse(because: "one decision, one wake-up");

        // The audit lands when the human acts, not when the orchestrator resumes: a
        // subscription must notify even if the resume is delayed by maintenance mode.
        (await harness.GetAuditEventTypesAsync(taskId))
            .Should().Contain(AuditEventType.DeploymentInterventionApproved);
    }

    // ── Read surface ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pending_gate_lookup_is_empty_for_a_user_without_the_view_permission()
    {
        var (harness, _, taskId) = await PausedDeploymentAsync("vw");
        await using var _h = harness;

        var permitted = NewService(new AllowAllPermissionEvaluator(), out _);
        (await permitted.FindTasksAwaitingResponseAsync(
                [taskId], WellKnown.DefaultSpaceId, Caller()))
            .Should().Contain(taskId);

        var denied = NewService(new DenyAllPermissionEvaluator(), out _);
        (await denied.FindTasksAwaitingResponseAsync(
                [taskId], WellKnown.DefaultSpaceId, Caller()))
            .Should().BeEmpty(because: "the indicator is decoration — no permission, no chip");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a real deployment up to its manual-intervention gate and returns the
    /// harness (caller disposes), the gate id and the task id.
    /// </summary>
    private async Task<(OrchestratorTestHarness Harness, Guid GateId, Guid TaskId)>
        PausedDeploymentAsync(string prefix)
    {
        var harness = new OrchestratorTestHarness(postgres);
        var suffix = $"{Guid.NewGuid():N}"[..8];
        var project = await harness.SeedProjectAsync($"{prefix}p-{suffix}");
        var env = await harness.SeedEnvironmentAsync($"{prefix}e-{suffix}");
        var targets = await harness.SeedTargetsAsync($"{prefix}t-{suffix}");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0", StepBuilder.Manual("gate"), StepBuilder.Script("after"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        // The responder must be a real user row: interruptions.acted_by_user_id is a
        // real FK (SET NULL) so the change-control trail cannot point at a phantom.
        await using (var db = harness.CreateContext())
        {
            await TestData.EnsureUserAsync(db, ResponderId);
        }

        await harness.RunDeploymentAsync(taskId);
        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        return (harness, gate.Id, taskId);
    }

    private InterruptionService NewService(
        IPermissionEvaluator permissions, out Channel<TenantWorkItem> queue)
    {
        queue = Channel.CreateUnbounded<TenantWorkItem>();
        return new InterruptionService(
            postgres,
            permissions,
            new AuditLogService(
                postgres,
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new KrakenDeploy.Server.Data.Spaces.DefaultSpaceContext(),
                TimeProvider.System),
            new KrakenDeploy.Server.Data.Spaces.DefaultSpaceContext(),
            queue,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            TimeProvider.System,
            NullLogger<InterruptionService>.Instance);
    }

    private InterruptionTimeoutJob NewTimeoutJob(InterruptionService svc)
        => new(postgres, svc, TimeProvider.System,
               NullLogger<InterruptionTimeoutJob>.Instance);

    private static CallerAuthorization Caller(string display = "test-responder")
        => CallerAuthorization.ForUser(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ResponderId.ToString()),
                new Claim(ClaimTypes.Name, display),
            ],
            authenticationType: "Test")));

    private static async Task<Interruption> GateAsync(
        OrchestratorTestHarness harness, Guid gateId)
    {
        await using var db = harness.CreateContext();
        return await db.Interruptions.IgnoreQueryFilters().FirstAsync(i => i.Id == gateId);
    }

    private static async Task SetResponsibleTeamsAsync(
        OrchestratorTestHarness harness, Guid gateId, params Guid[] teamIds)
    {
        await using var db = harness.CreateContext();
        await db.Interruptions.IgnoreQueryFilters()
            .Where(i => i.Id == gateId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ResponsibleTeamIds, teamIds));
    }

    private static async Task ExpireNowAsync(OrchestratorTestHarness harness, Guid gateId)
    {
        await using var db = harness.CreateContext();
        await db.Interruptions.IgnoreQueryFilters()
            .Where(i => i.Id == gateId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                i => i.ExpiresUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }
}
