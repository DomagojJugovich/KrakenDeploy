using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Regression: lifecycle retention must run for ORCHESTRATED deployments (the
/// primary path). Before the fix, RetentionService.PruneAfterDeploymentAsync was
/// wired ONLY to AgentHub's non-orchestrated fallback completion — but every
/// online deployment goes through the DeploymentWorker orchestrator, whose target
/// completions resolve via the sub-plan registry and early-return before AgentHub's
/// retention trigger. So old successful deployments and their task_step_logs /
/// task_log_live children accumulated unbounded despite RetentionKeepDeployments.
/// This drives a real deployment through DeploymentWorker (via the harness) and
/// asserts an older over-the-keep deployment is pruned once the new one succeeds.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OrchestratorRetentionTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Orchestrated_deployment_success_prunes_old_deployments_beyond_keep()
    {
        await using var harness = new OrchestratorTestHarness(postgres);

        var project = await harness.SeedProjectAsync($"orch-ret-{Guid.NewGuid():N}"[..18]);
        var env = await harness.SeedEnvironmentAsync($"ore-{Guid.NewGuid():N}"[..12]);
        var targets = await harness.SeedTargetsAsync($"ort-{Guid.NewGuid():N}"[..12]);
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));

        // keep=1 lifecycle covering the env, pointed at by the project (the release
        // has no channel, so RetentionService falls back to project.Lifecycle). Plus
        // an OLDER successful deployment for the same project+environment — it must
        // be pruned once the new deployment succeeds (keep=1 => keep newest only).
        Guid oldDeploymentId;
        await using (var db = harness.CreateContext())
        {
            var lifecycle = new Lifecycle
            {
                SpaceId = WellKnown.DefaultSpaceId,
                Name    = "orch-ret-lc",
                Phases  = [new LifecyclePhase
                {
                    Name = "P", EnvironmentIds = [env.Id], RetentionKeepDeployments = 1,
                }],
            };
            db.Lifecycles.Add(lifecycle);
            await db.SaveChangesAsync();

            var proj = await db.Projects.FirstAsync(p => p.Id == project.Id);
            proj.LifecycleId = lifecycle.Id;

            var old = new Deployment
            {
                SpaceId       = WellKnown.DefaultSpaceId,
                ProjectId     = project.Id,
                ReleaseId     = release.Id,
                EnvironmentId = env.Id,
                Status        = DeploymentStatus.Succeeded,
                CompletedUtc  = DateTimeOffset.UtcNow.AddHours(-1),
            };
            db.Deployments.Add(old);
            await db.SaveChangesAsync();
            oldDeploymentId = old.Id;
        }

        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);   // steps succeed by default

        await harness.RunDeploymentAsync(deploymentId);

        // The orchestrated deployment reached Succeeded...
        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Succeeded);

        // ...and its success triggered lifecycle retention, which pruned the older
        // deployment. The trigger is intentionally fire-and-forget, so poll for it.
        var pruned = await WaitUntilAsync(async () =>
        {
            await using var db = harness.CreateContext();
            return !await db.Deployments.IgnoreQueryFilters()
                .AnyAsync(d => d.Id == oldDeploymentId);
        });
        pruned.Should().BeTrue(
            "orchestrated-deployment success must trigger lifecycle retention (keep=1) and " +
            "prune the older successful deployment — before the fix retention never fired here");

        await using var check = postgres.CreateContext();
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == deploymentId))
            .Should().BeTrue("the just-succeeded deployment is the one retained");
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 150; i++)   // up to ~15 s; the prune completes in ms on a test DB
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }
}
