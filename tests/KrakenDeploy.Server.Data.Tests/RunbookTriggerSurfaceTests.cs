using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// D1 Phase 2 — the runbook trigger surface: <c>RunbookService.TriggerAsync</c>
/// mirrors <c>DeploymentService.CreateAsync</c> semantics for the target set
/// (primary + additional, de-duplicated, validated before insert, persisted as
/// microsecond-ordered assignment rows so the primary stays canonical) and for
/// <c>ScheduledFor</c> (only a genuinely FUTURE instant is persisted and then
/// the scheduled-dispatch job is the sole dispatcher; a due/past value is
/// normalized to null and dispatched immediately — exactly one dispatch path).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class RunbookTriggerSurfaceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Trigger_fans_out_over_primary_plus_additional_targets_in_order()
    {
        var g = await SeedRunbookGraphAsync(targetCount: 3);
        var (svc, queue) = NewService();

        // Additional list contains the primary again + an internal duplicate —
        // both must de-duplicate away.
        var run = await svc.TriggerAsync(
            g.RunbookId, g.EnvironmentId, g.Targets[0],
            initiator: TaskInitiator.Scheduled("trigger-surface-test"),
            caller: CallerAuthorization.System,
            additionalTargetIds: [g.Targets[1], g.Targets[0], g.Targets[2], g.Targets[1]]);

        run.Status.Should().Be(DeploymentStatus.Queued);
        run.ScheduledFor.Should().BeNull();

        await using var db = postgres.CreateContext();
        var assignments = await db.TaskTargetAssignments
            .Where(a => a.TaskId == run.Id)
            .OrderBy(a => a.AddedUtc)
            .Select(a => a.TargetId)
            .ToListAsync();
        assignments.Should().Equal(
            new[] { g.Targets[0], g.Targets[1], g.Targets[2] },
            "the primary is the first-assigned (canonical) target and order is " +
            "preserved via strictly increasing microsecond AddedUtc");

        queue.Reader.TryRead(out var item).Should().BeTrue("an unscheduled trigger dispatches immediately");
        item.Id.Should().Be(run.Id);
        queue.Reader.TryRead(out _).Should().BeFalse("exactly one wake-up per trigger");
    }

    [Fact]
    public async Task Trigger_with_future_scheduledFor_persists_and_does_not_enqueue()
    {
        var g = await SeedRunbookGraphAsync(targetCount: 1);
        var (svc, queue) = NewService();
        var scheduled = DateTimeOffset.UtcNow.AddHours(1);

        var run = await svc.TriggerAsync(
            g.RunbookId, g.EnvironmentId, g.Targets[0],
            initiator: TaskInitiator.Scheduled("trigger-surface-test"),
            caller: CallerAuthorization.System,
            scheduledFor: scheduled);

        run.Status.Should().Be(DeploymentStatus.Queued);
        run.ScheduledFor.Should().Be(scheduled);
        queue.Reader.TryRead(out _).Should().BeFalse(
            "a future-scheduled run is dispatched by the scheduled-dispatch job, " +
            "never double-dispatched at create time");

        await using var db = postgres.CreateContext();
        var persisted = await db.RunbookRuns
            .Where(r => r.Id == run.Id).Select(r => r.ScheduledFor).FirstAsync();
        persisted.Should().NotBeNull();
        // Postgres timestamptz stores microseconds — sub-µs ticks truncate on
        // the round-trip.
        persisted!.Value.Should().BeCloseTo(scheduled, TimeSpan.FromMicroseconds(1));
    }

    [Fact]
    public async Task Trigger_with_past_scheduledFor_normalizes_to_immediate()
    {
        var g = await SeedRunbookGraphAsync(targetCount: 1);
        var (svc, queue) = NewService();

        var run = await svc.TriggerAsync(
            g.RunbookId, g.EnvironmentId, g.Targets[0],
            initiator: TaskInitiator.Scheduled("trigger-surface-test"),
            caller: CallerAuthorization.System,
            scheduledFor: DateTimeOffset.UtcNow.AddMinutes(-1));

        run.ScheduledFor.Should().BeNull(
            "a due/past instant is normalized to null so the minutely job can " +
            "never re-enqueue what the create-time wake-up already dispatched");
        queue.Reader.TryRead(out var item).Should().BeTrue();
        item.Id.Should().Be(run.Id);
    }

    [Fact]
    public async Task Trigger_persists_the_failure_mode_and_defaults_to_BestEffort()
    {
        var g = await SeedRunbookGraphAsync(targetCount: 2);
        var (svc, _) = NewService();

        var atomic = await svc.TriggerAsync(
            g.RunbookId, g.EnvironmentId, g.Targets[0],
            initiator: TaskInitiator.Scheduled("trigger-surface-test"),
            caller: CallerAuthorization.System,
            additionalTargetIds: [g.Targets[1]],
            failureMode: DeploymentFailureMode.Atomic);

        var defaulted = await svc.TriggerAsync(
            g.RunbookId, g.EnvironmentId, g.Targets[0],
            initiator: TaskInitiator.Scheduled("trigger-surface-test"),
            caller: CallerAuthorization.System);

        await using var db = postgres.CreateContext();
        (await db.RunbookRuns.Where(r => r.Id == atomic.Id)
                .Select(r => r.FailureMode).FirstAsync())
            .Should().Be(DeploymentFailureMode.Atomic,
                "the knob rides the trigger onto the persisted run — the rolling " +
                "orchestrator reads it from the row");
        (await db.RunbookRuns.Where(r => r.Id == defaulted.Id)
                .Select(r => r.FailureMode).FirstAsync())
            .Should().Be(DeploymentFailureMode.BestEffort);
    }

    [Fact]
    public async Task Trigger_with_unknown_additional_target_fails_fast_and_persists_nothing()
    {
        var g = await SeedRunbookGraphAsync(targetCount: 1);
        var (svc, queue) = NewService();
        var bogus = Guid.NewGuid();

        var act = () => svc.TriggerAsync(
            g.RunbookId, g.EnvironmentId, g.Targets[0],
            initiator: TaskInitiator.Scheduled("trigger-surface-test"),
            caller: CallerAuthorization.System,
            additionalTargetIds: [bogus]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{bogus}*");

        await using var db = postgres.CreateContext();
        (await db.RunbookRuns.CountAsync(r => r.RunbookId == g.RunbookId))
            .Should().Be(0, "the target set is validated BEFORE the run row is inserted");
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private (RunbookService Svc, Channel<TenantWorkItem> Queue) NewService()
    {
        var queue = Channel.CreateUnbounded<TenantWorkItem>();
        var svc = new RunbookService(
            postgres,
            queue,
            TimeProvider.System,
            new Accounts.DisabledAccountContext(),
            new AllowAllPermissionEvaluator());
        return (svc, queue);
    }

    private sealed record Graph(Guid RunbookId, Guid EnvironmentId, List<Guid> Targets);

    /// <summary>Seeds env + project + runbook + a one-step runbook process +
    /// <paramref name="targetCount"/> online targets, and returns their ids.</summary>
    private async Task<Graph> SeedRunbookGraphAsync(int targetCount)
    {
        await using var db = postgres.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];

        var env = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"rts-e-{tag}", Slug = $"rts-e-{tag}", SortOrder = 1,
        };
        db.Environments.Add(env);

        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"rts-p-{tag}", Slug = $"rts-p-{tag}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var runbook = new Runbook
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProjectId = project.Id,
            Name = $"rts-rb-{tag}",
        };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();

        var process = new Process
        {
            SpaceId = WellKnown.DefaultSpaceId,
            OwnerKind = ProcessOwnerKind.Runbook,
            OwnerId = runbook.Id,
        };
        db.Processes.Add(process);
        await db.SaveChangesAsync();
        db.ProcessSteps.Add(new ProcessStep
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProcessId = process.Id,
            Name = "step-1",
            StepType = "Kraken.Script",
            PackageId = "",
            TargetRoles = ["web"],
            Config = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptBody"] = "echo hi" },
            SortOrder = 1,
        });

        var targets = new List<Guid>();
        for (var i = 0; i < targetCount; i++)
        {
            var target = new DeploymentTarget
            {
                SpaceId = WellKnown.DefaultSpaceId,
                Name = $"rts-t{i}-{tag}",
                Roles = ["web"],
                TransportMode = TransportMode.Reverse,
                Status = TargetStatus.Online,
            };
            db.DeploymentTargets.Add(target);
            targets.Add(target.Id);
        }
        await db.SaveChangesAsync();

        return new Graph(runbook.Id, env.Id, targets);
    }
}
