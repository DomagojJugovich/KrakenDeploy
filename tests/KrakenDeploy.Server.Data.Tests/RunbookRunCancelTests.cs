using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B6 — runbook runs' first cancel surface. The service flip rides the B5
/// guarded writer (never overwrites a terminal verdict) and fires the agent
/// abort push after the verdict is durably recorded.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class RunbookRunCancelTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Cancels_a_queued_run_and_pushes_the_abort()
    {
        var runId = await SeedRunAsync(DeploymentStatus.Queued);
        var pusher = new RecordingCancelPusher();
        var svc = NewService(pusher);

        var updated = await svc.CancelRunAsync(runId, CallerAuthorization.System);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(DeploymentStatus.Cancelled);
        updated.CompletedUtc.Should().NotBeNull();

        await using var db = postgres.CreateContext();
        var persisted = await db.RunbookRuns.FirstAsync(r => r.Id == runId);
        persisted.Status.Should().Be(DeploymentStatus.Cancelled);
        persisted.ScheduledFor.Should().BeNull();

        pusher.Pushes.Should().ContainSingle().Which.TaskId.Should().Be(runId);
    }

    [Fact]
    public async Task Cancelling_a_terminal_run_throws_and_writes_nothing()
    {
        var runId = await SeedRunAsync(DeploymentStatus.Succeeded);
        var pusher = new RecordingCancelPusher();
        var svc = NewService(pusher);

        var act = () => svc.CancelRunAsync(runId, CallerAuthorization.System);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in a terminal state*");

        await using var db = postgres.CreateContext();
        (await db.RunbookRuns.FirstAsync(r => r.Id == runId)).Status
            .Should().Be(DeploymentStatus.Succeeded, "the recorded verdict stands");
        pusher.Pushes.Should().BeEmpty("no push for a refused cancel");
    }

    [Fact]
    public async Task Cancelling_an_unknown_run_returns_null()
    {
        (await NewService(new RecordingCancelPusher()).CancelRunAsync(Guid.NewGuid(), CallerAuthorization.System))
            .Should().BeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private RunbookService NewService(IAgentCancelPusher pusher) => new(
        postgres,
        new RunbookRunChannel(),
        TimeProvider.System,
        new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
        new AllowAllPermissionEvaluator(),
        cancelPusher: pusher);

    private async Task<Guid> SeedRunAsync(DeploymentStatus status)
    {
        await using var db = postgres.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];

        var env = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"rrc-e-{tag}", Slug = $"rrc-e-{tag}", SortOrder = 1,
        };
        db.Environments.Add(env);

        var lifecycle = new Lifecycle
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"rrc-lc-{tag}",
            Phases = [new LifecyclePhase { Name = "P", EnvironmentIds = [env.Id] }],
        };
        db.Lifecycles.Add(lifecycle);
        await db.SaveChangesAsync();

        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"rrc-p-{tag}", Slug = $"rrc-p-{tag}",
            LifecycleId = lifecycle.Id,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);

        var runbook = new Runbook
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProjectId = project.Id,
            Name = $"rrc-rb-{tag}",
        };
        db.Runbooks.Add(runbook);

        var run = new RunbookRun
        {
            SpaceId = WellKnown.DefaultSpaceId,
            RunbookId = runbook.Id,
            ProjectId = project.Id,
            EnvironmentId = env.Id,
            Status = status,
            ProcessSnapshot = [],
            CompletedUtc = status.IsTerminal() ? DateTimeOffset.UtcNow : null,
        };
        db.RunbookRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private sealed class RecordingCancelPusher : IAgentCancelPusher
    {
        public List<(Guid TaskId, string? Reason)> Pushes { get; } = [];

        public Task PushCancelAsync(Guid taskId, string? reason, CancellationToken ct = default)
        {
            Pushes.Add((taskId, reason));
            return Task.CompletedTask;
        }
    }
}
