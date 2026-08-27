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
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// BG1/T13 — the maintenance CREATION refusal, the authoritative gate for every
/// surface (the UI rides the /_blazor middleware exemption, and a caller holding
/// BypassMaintenance passes the middleware — this service-layer refusal is what
/// actually stops them). The REST/MCP creation routes call the exact same
/// service methods; their non-bypass path is additionally pinned by
/// MaintenanceMiddlewareTests. Unconditional by design: these tests use an
/// allow-everything permission evaluator, proving no permission (BypassMaintenance
/// included) exempts creation. Only a child creation (ParentTaskId — the
/// DeployRelease step) passes, so an in-flight parent can never strand.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class MaintenanceCreationRefusalTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Maintenance_refuses_a_new_deployment_at_the_service_layer()
    {
        var g = await SeedDeploymentGraphAsync();
        var (svc, queue) = NewDeploymentService();

        await WithMaintenanceAsync("Upgrading to v2", async () =>
        {
            var act = () => svc.CreateAsync(
                g.ReleaseId, g.EnvironmentId, g.TargetId,
                initiator: TaskInitiator.Api(null, "test"),
                caller: CallerAuthorization.System);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Maintenance mode is enabled*",
                    "creation must be refused with a message naming the switch");
            queue.Reader.TryRead(out _).Should().BeFalse("a refused creation must not enqueue");
        });

        // Window closed → the very same call succeeds.
        var deployment = await svc.CreateAsync(
            g.ReleaseId, g.EnvironmentId, g.TargetId,
            initiator: TaskInitiator.Api(null, "test"),
            caller: CallerAuthorization.System);
        deployment.Status.Should().Be(DeploymentStatus.Queued);
    }

    [Fact]
    public async Task Maintenance_allows_a_child_creation_carrying_ParentTaskId()
    {
        var g = await SeedDeploymentGraphAsync();
        var (svc, _) = NewDeploymentService();

        // The in-flight parent (a DeployRelease step's own task, claimed before
        // the window) — its child creation must pass or the parent strands.
        var parent = await svc.CreateAsync(
            g.ReleaseId, g.EnvironmentId, g.TargetId,
            initiator: TaskInitiator.Api(null, "test"),
            caller: CallerAuthorization.System);

        await WithMaintenanceAsync("Upgrading", async () =>
        {
            var child = await svc.CreateAsync(
                g.ReleaseId, g.EnvironmentId, g.TargetId,
                initiator: TaskInitiator.Api(null, "test"),
                caller: CallerAuthorization.System,
                parentTaskId: parent.Id);
            child.ParentTaskId.Should().Be(parent.Id,
                "a DeployRelease child is the continuation of claimed work, not new work");
        });
    }

    [Fact]
    public async Task Maintenance_refuses_a_runbook_trigger_with_zero_escape_hatch()
    {
        var g = await SeedRunbookGraphAsync();
        var queue = Channel.CreateUnbounded<TenantWorkItem>();
        var svc = new RunbookService(
            postgres, queue, TimeProvider.System,
            new Accounts.DisabledAccountContext(),
            new AllowAllPermissionEvaluator());

        await WithMaintenanceAsync("Patching", async () =>
        {
            var act = () => svc.TriggerAsync(
                g.RunbookId, g.EnvironmentId, g.TargetId,
                initiator: TaskInitiator.Api(null, "test"),
                caller: CallerAuthorization.System);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Maintenance mode is enabled*",
                    "runbooks have no parent exemption and no bypass (T-B2) — " +
                    "run preparation runbooks BEFORE enabling maintenance");
            queue.Reader.TryRead(out _).Should().BeFalse();
        });

        var run = await svc.TriggerAsync(
            g.RunbookId, g.EnvironmentId, g.TargetId,
            initiator: TaskInitiator.Api(null, "test"),
            caller: CallerAuthorization.System);
        run.Status.Should().Be(DeploymentStatus.Queued);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private (DeploymentService Svc, Channel<TenantWorkItem> Queue) NewDeploymentService()
    {
        var queue = Channel.CreateUnbounded<TenantWorkItem>();
        var svc = new DeploymentService(
            postgres, queue, TimeProvider.System,
            new Accounts.DisabledAccountContext(),
            new AllowAllPermissionEvaluator());
        return (svc, queue);
    }

    private async Task WithMaintenanceAsync(string reason, Func<Task> body)
    {
        var maintenance = new MaintenanceModeService(
            new SettingsService(postgres.ScopeFactory, TimeProvider.System), TimeProvider.System);
        await maintenance.EnableAsync(reason, userId: null);
        try
        {
            await body();
        }
        finally
        {
            await maintenance.DisableAsync();
        }
    }

    private sealed record DeploymentGraph(Guid ReleaseId, Guid EnvironmentId, Guid TargetId);

    private async Task<DeploymentGraph> SeedDeploymentGraphAsync()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"mc-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"mc-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync($"mc-{Guid.NewGuid():N}"[..12]);
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        return new DeploymentGraph(release.Id, env.Id, targets[0].Id);
    }

    private sealed record RunbookGraph(Guid RunbookId, Guid EnvironmentId, Guid TargetId);

    /// <summary>Mirrors RunbookTriggerSurfaceTests.SeedRunbookGraphAsync (one step, one target).</summary>
    private async Task<RunbookGraph> SeedRunbookGraphAsync()
    {
        await using var db = postgres.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];

        var env = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"mcr-e-{tag}", Slug = $"mcr-e-{tag}", SortOrder = 1,
        };
        db.Environments.Add(env);

        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"mcr-p-{tag}", Slug = $"mcr-p-{tag}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var runbook = new Runbook
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProjectId = project.Id,
            Name = $"mcr-rb-{tag}",
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

        var target = new DeploymentTarget
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"mcr-t-{tag}",
            Roles = ["web"],
            TransportMode = TransportMode.Reverse,
            Status = TargetStatus.Online,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        return new RunbookGraph(runbook.Id, env.Id, target.Id);
    }
}
