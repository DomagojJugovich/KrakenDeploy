using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Online regression guard for the per-step <c>TimeoutSeconds</c> bug on the
/// <see cref="DeployReleaseStepRunner"/> path — the orchestrator step whose wait
/// is a pure DB poll loop (<c>WaitForChildAsync</c>), no external process.
/// <para>
/// Before the fix, <c>WaitForChildAsync</c> swallowed a cancelled token: its
/// <c>while (!ct.IsCancellationRequested)</c> guard and the
/// <c>catch (TaskCanceledException) { return false; }</c> around the poll delay
/// turned a per-attempt timeout into a generic <c>false</c> (Failed). This drives
/// the REAL runner through the SAME <see cref="StepRetryRunner"/> wiring the
/// worker uses (<c>RunServerStepWithRetriesAsync</c>): a child deployment is
/// created and left Queued (nothing drains the queue in this provider), so the
/// runner polls until the per-attempt timeout cancels it.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DeployReleaseStepTimeoutTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task DeployRelease_step_exceeding_TimeoutSeconds_yields_TimedOut()
    {
        // ── Seed: child project (no lifecycle → lifecycle gate passes) + a
        //    release, an environment, a target, and a parent deployment that
        //    points at all three. ───────────────────────────────────────────
        Guid childProjectId, parentDeploymentId;
        await using (var db = postgres.CreateContext())
        {
            var childProject = new Project
            {
                SpaceId     = WellKnown.DefaultSpaceId,
                Name        = $"child-{Guid.NewGuid():N}",
                Slug        = $"child-{Guid.NewGuid():N}",
                Description = "deploy-release timeout test child",
            };
            db.Projects.Add(childProject);

            var env = new DeploymentEnvironment
            {
                SpaceId = WellKnown.DefaultSpaceId,
                Name    = $"env-{Guid.NewGuid():N}",
                Slug    = $"env-{Guid.NewGuid():N}",
                SortOrder = 1,
            };
            db.Environments.Add(env);

            var target = new DeploymentTarget
            {
                SpaceId       = WellKnown.DefaultSpaceId,
                Name          = $"target-{Guid.NewGuid():N}",
                Roles         = ["web"],
                TransportMode = TransportMode.Reverse,
                Status        = TargetStatus.Online,
            };
            db.DeploymentTargets.Add(target);
            await db.SaveChangesAsync();

            var childRelease = new Release
            {
                SpaceId                    = WellKnown.DefaultSpaceId,
                ProjectId                  = childProject.Id,
                Version                    = "1.0.0",
                ProcessSnapshot            = [],
                VariableSnapshot           = [],
                VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
            };
            db.Releases.Add(childRelease);
            await db.SaveChangesAsync();

            // Parent deployment: the runner only reads its SpaceId / EnvironmentId
            // / TargetId / Targets / TenantId, so reuse the child release for the
            // FK and point it at the seeded environment + target.
            var parent = new Deployment
            {
                SpaceId       = WellKnown.DefaultSpaceId,
                ReleaseId     = childRelease.Id,
                EnvironmentId = env.Id,
                TargetId      = target.Id,
                Status        = DeploymentStatus.Running,
            };
            db.Deployments.Add(parent);
            await db.SaveChangesAsync();

            childProjectId     = childProject.Id;
            parentDeploymentId = parent.Id;
        }

        // ── A provider mirroring OrchestratorTestHarness's data wiring, so the
        //    runner can resolve KrakenDbContext (scope) + DeploymentService
        //    (for the child create). ──────────────────────────────────────────
        var services = new ServiceCollection();
        services.AddKrakenDeployData(postgres.ConnectionString);
        services.AddSingleton<IEncryptionService>(_ => new AesEncryptionService(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        var runner = new DeployReleaseStepRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new NullUiHubContext(),
            TimeProvider.System,
            NullLogger<DeployReleaseStepRunner>.Instance);

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OctopusDeployReleaseConfigKeys.ProjectId] = childProjectId.ToString(),
        };
        var step = new DeploymentStepPlan(
            0, "DeployChild", DeployReleaseStepRunner.StepType, "", "", config);
        var planVars = new Dictionary<string, string>();

        // Mirror DeploymentWorker.RunServerStepWithRetriesAsync: a 2s per-attempt
        // timeout, no retries. The child create finishes in well under 2s, so the
        // timeout fires inside the WaitForChildAsync poll loop (PollInterval 500ms)
        // while the child is still Queued.
        var outcome = await StepRetryRunner.RunAsync<bool>(
            stepName:                step.Name,
            maxRetries:              0,
            retryDelaySeconds:       0,
            timeoutSeconds:          2,
            runAttempt:              ct => runner.ExecuteAsync(
                                         parentDeploymentId, step, planVars,
                                         WellKnown.DefaultSpaceId, ct),
            isSuccess:               ok => ok,
            onTimeoutResult:         () => false,
            onAttemptTimedOutAsync:  null,
            onRetryAsync:            null,
            onLateSuccessAsync:      null,
            ct:                      CancellationToken.None);

        outcome.TimedOut.Should().BeTrue(
            "a DeployRelease step that exceeds TimeoutSeconds while waiting on its " +
            "child must surface as TimedOut — WaitForChildAsync must let the OCE " +
            "propagate, not swallow it into a Failed result");
        outcome.Result.Should().BeFalse("the timed-out attempt is a failed result");
    }
}
