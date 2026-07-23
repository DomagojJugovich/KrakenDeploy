using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// E3 (execution-engine audit 2026-07-16) — the <c>NodeTaskGate</c> deadlock and
/// the unbounded <c>DeployReleaseStepRunner.WaitForChildAsync</c>.
/// <list type="number">
///   <item><b>Child deployments bypass the gate</b> — a parent holds a gate slot
///     for the whole child wait; if the child also needed a slot, capacity-many
///     parents on their children would deadlock the node. This test runs
///     capacity-many parents (each with a DeployRelease step) + their children
///     end-to-end through the real gate-aware dispatch loop and asserts they all
///     complete (no deadlock).</item>
///   <item><b>WaitForChildAsync ceiling fires as TimedOut</b> — a child that never
///     completes must not pin the parent's slot forever; the Engine ceiling bounds
///     the wait and a ceiling hit is classified TimedOut, not generic Failed.</item>
///   <item><b>Self-recursive cascades are refused at plan time</b> — a project
///     cannot deploy-release itself; no child is created.</item>
/// </list>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OrchestratorDeployReleaseGateTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Capacity_many_parents_with_children_complete_without_deadlock()
    {
        // Gate capacity == parent count: both parents hold BOTH slots while each
        // waits on its child. Pre-fix, the two children would each need a slot the
        // parents hold → permanent node stall. With the child-bypass fix they run
        // gate-free and the parents complete.
        const int capacity = 2;
        var engine = new EngineOptions { MaxConcurrentTasks = capacity };
        await using var harness = new OrchestratorTestHarness(postgres, engine);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var env = await harness.SeedEnvironmentAsync($"dr-env-{tag}");

        // Two parent projects, each a single server-side DeployRelease → its OWN
        // distinct child project. Distinct (project, env) keys so the two children
        // run CONCURRENTLY, which is what this gate-bypass/no-deadlock test needs.
        // (A shared child project would now serialize under F1 (project,env,tenant)
        // serialization — the second child would wait for the first, and this
        // harness runs no reconciler to re-signal it within the test window. F1
        // child serialization is covered in ServerTaskLeaseTests /
        // OrchestratorSerializationTests, not here.)
        var deployments = new List<(Guid Id, FakeAgent Agent)>(capacity);
        for (var i = 0; i < capacity; i++)
        {
            var childProjectId = await harness.SeedChildProjectWithReleaseAsync(
                $"dr-child-{tag}-{i}", StepBuilder.Script("child-step"));
            var parentProject = await harness.SeedProjectAsync($"dr-parent-{tag}-{i}");
            var parentRelease = await harness.SeedReleaseAsync(
                parentProject.Id, "1.0", StepBuilder.DeployRelease("deploy-child", childProjectId));
            var target = (await harness.SeedTargetsAsync($"dr-t-{tag}-{i}"))[0];
            // The agent serves the CHILD's target wave (the parent is server-only).
            var agent = harness.ConnectFakeAgent(target);
            var deploymentId = await harness.CreateDeploymentAsync(parentRelease.Id, env.Id, [target]);
            deployments.Add((deploymentId, agent));
        }

        await harness.StartWorkerAsync();
        foreach (var (id, _) in deployments)
        {
            await harness.EnqueueAsync(id);
        }

        // The deadlock detector: without the fix these never terminate.
        foreach (var (id, _) in deployments)
        {
            var d = await harness.WaitForTerminalAsync(id, TimeSpan.FromSeconds(30));
            d.Status.Should().Be(DeploymentStatus.Succeeded,
                because: "the parent's DeployRelease child ran gate-free and succeeded");
        }

        // Both children were dispatched to their targets' agents (proves they ran,
        // rather than every parent Condition-skipping).
        foreach (var (_, agent) in deployments)
        {
            agent.WaveCount.Should().Be(1,
                because: "each child's single target wave dispatched to its agent");
        }
    }

    [Fact]
    public async Task WaitForChild_ceiling_fires_as_TimedOut()
    {
        // Short ceiling + a child left Queued (the worker loop is NOT started, so
        // nothing drains the child) → WaitForChildAsync polls until the ceiling
        // cancels the attempt, which StepRetryRunner classifies TimedOut.
        var engine = new EngineOptions { MaxDeployReleaseWaitDuration = TimeSpan.FromSeconds(2) };
        await using var harness = new OrchestratorTestHarness(postgres, engine);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var env = await harness.SeedEnvironmentAsync($"cl-env-{tag}");
        var childProjectId = await harness.SeedChildProjectWithReleaseAsync($"cl-child-{tag}");

        var parentProject = await harness.SeedProjectAsync($"cl-parent-{tag}");
        // TimeoutSeconds=0 → the Engine ceiling governs (independent of per-step timeout).
        var parentRelease = await harness.SeedReleaseAsync(
            parentProject.Id, "1.0",
            StepBuilder.DeployRelease("deploy-child", childProjectId, timeoutSeconds: 0));
        var target = (await harness.SeedTargetsAsync($"cl-t-{tag}"))[0];
        harness.ConnectFakeAgent(target);
        var deploymentId = await harness.CreateDeploymentAsync(parentRelease.Id, env.Id, [target]);

        // DispatchForTestAsync runs the parent's server wave synchronously; the
        // child it creates is never dispatched (no worker loop), so the wait hits
        // the ceiling. WaitAsync bounds the test: if the ceiling regressed
        // (TimeoutSeconds=0 → unlimited) this would hang the suite; instead it
        // fails cleanly with a TimeoutException.
        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TimeSpan.FromSeconds(20));

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed,
            because: "the Required DeployRelease step timed out on the ceiling → the deployment fails");

        var outcomes = await harness.GetOutcomesAsync(deploymentId);
        outcomes.Should().ContainSingle(o => o.Outcome == StepOutcomeKind.TimedOut,
            because: "a ceiling hit classifies the DeployRelease step TimedOut, not generic Failed");
    }

    [Fact]
    public async Task DeployRelease_ceiling_timeout_does_not_retry_into_duplicate_children()
    {
        // A DeployRelease step is capped to a SINGLE attempt: a step-level retry
        // would re-trigger a fresh child deployment while the prior (timed-out)
        // child is still running — racing up to MaxRetries+1 concurrent deploys of
        // the same release to the same targets. Configure MaxRetries=2 + a child
        // that never terminates (Queued, no worker loop); exactly ONE child must be
        // created, and the step must be TimedOut.
        var engine = new EngineOptions { MaxDeployReleaseWaitDuration = TimeSpan.FromSeconds(2) };
        await using var harness = new OrchestratorTestHarness(postgres, engine);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var env = await harness.SeedEnvironmentAsync($"rt-env-{tag}");
        var childProjectId = await harness.SeedChildProjectWithReleaseAsync($"rt-child-{tag}");

        var parentProject = await harness.SeedProjectAsync($"rt-parent-{tag}");
        var parentRelease = await harness.SeedReleaseAsync(
            parentProject.Id, "1.0",
            StepBuilder.DeployRelease("deploy-child", childProjectId, timeoutSeconds: 0, maxRetries: 2));
        var target = (await harness.SeedTargetsAsync($"rt-t-{tag}"))[0];
        harness.ConnectFakeAgent(target);
        var deploymentId = await harness.CreateDeploymentAsync(parentRelease.Id, env.Id, [target]);

        // One attempt = one ~2s ceiling. Three attempts (regressed cap) would take
        // ~6s and create 3 children; the WaitAsync bound stays above either.
        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TimeSpan.FromSeconds(30));

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed);

        await using var db = harness.CreateContext();
        var childCount = await db.Deployments.IgnoreQueryFilters()
            .CountAsync(d => d.ProjectId == childProjectId);
        childCount.Should().Be(1,
            because: "a DeployRelease step is single-attempt — a retry must NOT spawn a second " +
                     "concurrent child deployment of the same release");
    }

    [Fact]
    public async Task Direct_self_recursive_cascade_is_refused_without_creating_a_child()
    {
        // A short ceiling guards against a hang IF the refusal regresses: without
        // the guard a child of P would be created (count 2) and the wait would then
        // time out — either way the assertions below fail fast, not hang.
        var engine = new EngineOptions { MaxDeployReleaseWaitDuration = TimeSpan.FromSeconds(2) };
        await using var harness = new OrchestratorTestHarness(postgres, engine);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var env = await harness.SeedEnvironmentAsync($"sr-env-{tag}");

        // Project P whose release deploy-releases P itself (A→A).
        var p = await harness.SeedProjectAsync($"sr-p-{tag}");
        var release = await harness.SeedReleaseAsync(
            p.Id, "1.0", StepBuilder.DeployRelease("deploy-self", p.Id));
        var target = (await harness.SeedTargetsAsync($"sr-t-{tag}"))[0];
        harness.ConnectFakeAgent(target);
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, [target]);

        await harness.RunDeploymentAsync(deploymentId).WaitAsync(TimeSpan.FromSeconds(20));

        var deployment = await harness.GetDeploymentAsync(deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed,
            because: "the self-recursive DeployRelease step is refused, failing the Required step");

        await using var db = harness.CreateContext();
        var deploymentsOfP = await db.Deployments.IgnoreQueryFilters()
            .CountAsync(d => d.ProjectId == p.Id);
        deploymentsOfP.Should().Be(1,
            because: "the refusal happens at plan time — no child deployment of P is ever created");
    }

    [Fact]
    public async Task Transitive_self_recursive_cascade_is_refused()
    {
        // A -> B -> A: a deployment of B (whose parent is a deployment of A) runs a
        // DeployRelease step targeting project A. The refusal must walk the
        // ParentTaskId ancestry (not just the immediate project) to find A.
        var engine = new EngineOptions { MaxDeployReleaseWaitDuration = TimeSpan.FromSeconds(2) };
        await using var harness = new OrchestratorTestHarness(postgres, engine);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var env = await harness.SeedEnvironmentAsync($"tr-env-{tag}");
        var target = (await harness.SeedTargetsAsync($"tr-t-{tag}"))[0];
        harness.ConnectFakeAgent(target);

        var projectA = await harness.SeedProjectAsync($"tr-A-{tag}");
        var releaseA = await harness.SeedReleaseAsync(projectA.Id, "1.0", StepBuilder.Script("a-step"));

        var projectB = await harness.SeedProjectAsync($"tr-B-{tag}");
        // B's process deploy-releases A.
        var releaseB = await harness.SeedReleaseAsync(
            projectB.Id, "1.0", StepBuilder.DeployRelease("deploy-A", projectA.Id));

        // Parent deployment of A (top-level), then a child deployment of B whose
        // ParentTaskId points at it.
        var parentOfA = await harness.CreateDeploymentAsync(releaseA.Id, env.Id, [target]);
        var childOfB = await harness.CreateDeploymentAsync(
            releaseB.Id, env.Id, [target], parentTaskId: parentOfA);

        // Running B's DeployRelease-A step must be refused (A is in B's ancestry).
        await harness.RunDeploymentAsync(childOfB).WaitAsync(TimeSpan.FromSeconds(20));

        var deployment = await harness.GetDeploymentAsync(childOfB);
        deployment.Status.Should().Be(DeploymentStatus.Failed,
            because: "the transitive self-recursive cascade A->B->A is refused");

        await using var db = harness.CreateContext();
        var deploymentsOfA = await db.Deployments.IgnoreQueryFilters()
            .CountAsync(d => d.ProjectId == projectA.Id);
        deploymentsOfA.Should().Be(1,
            because: "only the seeded parent deployment of A exists — the transitive refusal " +
                     "creates no new child of A");
    }
}
