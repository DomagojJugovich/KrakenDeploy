using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Regression tests for the defects the WP3 code review found. Each one FAILED against
/// the original implementation — that is the bar for being here, since the first test
/// pass was green while all of these were live.
/// <para>
/// The common root cause was that no test drove a resume through the PRODUCTION dispatch
/// path: <c>RunDeploymentAsync</c> calls <c>DispatchForTestAsync</c>, which bypasses
/// <c>GateThenDispatchCoreAsync</c> (and therefore <c>ProbeGateAsync</c>, the
/// <c>NodeTaskGate</c> and the F1 pre-gate skip) entirely. The tests below use
/// <c>StartWorkerAsync</c> + <c>ResolveAndSignalAsync</c> so the resume travels the same
/// road an approval does.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ManualInterventionRegressionTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // ── The two engine-breaking races ───────────────────────────────────────

    [Fact]
    public async Task A_duplicate_wake_up_does_not_resume_an_unanswered_gate()
    {
        // Wake-ups are at-least-once: a gated deployment that waits past the
        // stale-Queued grace accumulates duplicate channel items. Dispatch #1 claims,
        // pauses and frees its slot; dispatch #2 then found the row Paused, resumed it,
        // and the orchestrator hard-failed the task for the "impossible" state of
        // Running-with-a-Pending-gate — killing the deployment seconds after it paused.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "dup");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0", StepBuilder.Manual("hold"), StepBuilder.Script("after"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.StartWorkerAsync();
        await harness.EnqueueAsync(taskId);
        await harness.WaitForPausedAsync(taskId, TimeSpan.FromSeconds(30));

        // The duplicate lands while the gate is still Pending.
        await harness.EnqueueAsync(taskId);
        await harness.EnqueueAsync(taskId);
        await Task.Delay(1500);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Paused,
            because: "a duplicate wake-up must be eaten, exactly as the Queued claim " +
                     "eats duplicates — not resumed into a hard failure");
        (await harness.GetPauseCheckpointAsync(taskId)).Should().NotBeNullOrEmpty(
            because: "the checkpoint must survive a duplicate wake-up");
        (await harness.GetInterruptionsAsync(taskId)).Single()
            .Status.Should().Be(InterruptionStatus.Pending);
    }

    [Fact]
    public async Task A_resume_is_not_deferred_to_an_earlier_queued_sibling()
    {
        // The pre-gate probe applied the F1 claim-deferral predicate to EVERY wake-up
        // without reading Status. A Paused task already owns its (project, environment,
        // tenant) key, so deferring it to a queued sibling deadlocked both: the sibling
        // could not claim (the paused task holds the key) and the paused task could not
        // resume (the sibling is older and due), with arm 3 re-signalling every minute.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "fifo");
        harness.ConnectFakeAgent(targets[0]);

        // Sibling created FIRST but scheduled for the future, so it is not due when the
        // ad-hoc deployment claims — then becomes a due, earlier-created Queued peer.
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0", StepBuilder.Manual("hold"), StepBuilder.Script("after"));
        var siblingId = await harness.CreateDeploymentAsync(
            release.Id, env.Id, targets, scheduledFor: DateTimeOffset.UtcNow.AddHours(2));

        var gatedId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        await harness.StartWorkerAsync();
        await harness.EnqueueAsync(gatedId);
        await harness.WaitForPausedAsync(gatedId, TimeSpan.FromSeconds(30));

        // Make the sibling due, then approve.
        await using (var db = harness.CreateContext())
        {
            await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == siblingId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    t => t.ScheduledFor, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        var gate = (await harness.GetInterruptionsAsync(gatedId)).Single();
        await harness.ResolveAndSignalAsync(gate.Id, InterruptionStatus.Approved);

        var finished = await harness.WaitForTerminalAsync(gatedId, TimeSpan.FromSeconds(30));
        finished.Status.Should().Be(DeploymentStatus.Succeeded,
            because: "a paused task already owns the (project, environment, tenant) key, " +
                     "so the pre-gate FIFO check must not defer its resume to a sibling");
    }

    // ── Verdict + checkpoint fidelity ───────────────────────────────────────

    [Fact]
    public async Task A_refusal_short_circuits_a_later_gate_that_would_otherwise_pause_again()
    {
        // Gate B here is Condition=Success, so once gate A is refused (hasFailed = true) the
        // condition excludes it and it never materialises. That is correct behaviour, and
        // worth pinning — but note it does NOT exercise the checkpoint at all: the run
        // finalises inside the same resume dispatch, so interventionRejected never has to
        // survive a pause. See the sibling test below, which does.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "two");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate-a"),
            StepBuilder.Script("work"),
            StepBuilder.Manual("gate-b"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        var gateA = (await harness.GetInterruptionsAsync(taskId)).Single();
        await harness.ResolveInterruptionAsync(gateA.Id, InterruptionStatus.Rejected, "no");

        await harness.RunDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Failed,
            because: "a rejected gate is Failed in every failure mode");
        (await harness.GetInterruptionsAsync(taskId)).Should().ContainSingle(
            because: "a refused run must not park again asking a second team to approve " +
                     "work that is already dead");
    }

    [Fact]
    public async Task A_rejection_survives_a_later_pause_and_still_finalises_Failed()
    {
        // interventionRejected was a bare local, absent from the checkpoint. Reject gate A,
        // pause at gate B, approve B -> the flag was lost and the run finalised
        // SucceededWithWarnings: a refused change recorded as a warning-level success.
        //
        // WP3-b — this test previously used a Condition=Success gate B, which a refusal
        // excludes, so the run never paused a SECOND time and the checkpoint field was
        // never round-tripped. It passed with the field deleted outright. Gate B is now
        // Condition=Always so it still applies after the refusal, which is what forces
        // interventionRejected through the encrypt/decrypt cycle.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "surv");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate-a"),
            StepBuilder.Script("work"),
            StepBuilder.Manual("gate-b",
                condition: KrakenDeploy.Execution.StepCondition.Always),
            StepBuilder.Script("cleanup",
                condition: KrakenDeploy.Execution.StepCondition.Always));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        var gateA = (await harness.GetInterruptionsAsync(taskId)).Single(i => i.StepName == "gate-a");
        await harness.ResolveInterruptionAsync(gateA.Id, InterruptionStatus.Rejected, "no");

        // Resume #1: gate A is refused (hasFailed + interventionRejected set), but gate B is
        // Condition=Always so it still applies — the task pauses a SECOND time, which is
        // the only way the flag has to survive a checkpoint round-trip.
        await harness.RunDeploymentAsync(taskId);
        var parked = await harness.GetServerTaskAsync(taskId);
        parked.Status.Should().Be(DeploymentStatus.Paused,
            because: "an Always gate still applies after an earlier refusal — without this " +
                     "the test never reaches a second checkpoint and proves nothing");

        var gateB = (await harness.GetInterruptionsAsync(taskId)).Single(i => i.StepName == "gate-b");
        await harness.ResolveInterruptionAsync(gateB.Id, InterruptionStatus.Approved, null);

        // Resume #2: approving B must NOT rehabilitate the run.
        await harness.RunDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Failed,
            because: "the earlier REFUSAL is the verdict; losing it across the checkpoint " +
                     "reported a refused change as SucceededWithWarnings");
    }

    [Fact]
    public async Task Output_variables_captured_before_the_gate_survive_the_resume()
    {
        // The checkpoint's whole purpose, and it had no test. The per-target output bags
        // are NOT recoverable from task_output_variables (no target dimension), so a
        // lossy Export/RestoreFrom would silently drop them.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "out");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Script("capture"),
            StepBuilder.Manual("hold"),
            StepBuilder.Script("consume"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.StepResponses["capture"] = new FakeStepResponse(
            Success: true,
            Outputs: new Dictionary<string, string> { ["Token"] = "abc123" });

        await harness.RunDeploymentAsync(taskId);
        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Approved);
        await harness.RunDeploymentAsync(taskId);

        (await harness.GetDeploymentAsync(taskId)).Status
            .Should().Be(DeploymentStatus.Succeeded);

        // The post-gate wave's sub-plan must carry the pre-gate capture.
        var consumePlan = agent.ReceivedPlans
            .LastOrDefault(p => p.Steps.Any(st => st.Name == "consume"));
        consumePlan.Should().NotBeNull(because: "the post-gate wave must have dispatched");
        consumePlan!.Variables.Should().ContainKey("Octopus.Action[capture].Output.Token")
            .WhoseValue.Should().Be("abc123",
                because: "the checkpoint is the only link between the two halves of the " +
                         "run, so a lossy round-trip loses every captured output");
    }

    // ── Run conditions ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_gate_whose_run_condition_excludes_it_is_skipped_not_paused()
    {
        // A Condition=Failure gate — the natural way to write "approve the rollback" —
        // used to pause EVERY healthy deployment for the full timeout while holding its
        // (project, environment, tenant) slot, then auto-fail an otherwise-green task.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "cond");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Script("work"),
            StepBuilder.Manual("approve-the-rollback",
                condition: KrakenDeploy.Execution.StepCondition.Failure));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Succeeded,
            because: "nothing failed, so the Failure-conditioned gate must not apply");
        (await harness.GetInterruptionsAsync(taskId)).Should().BeEmpty(
            because: "a gate that does not apply was never a change-control question");
        (await harness.GetOutcomesAsync(taskId))
            .Should().ContainSingle(o => o.StepName == "approve-the-rollback")
            .Which.Outcome.Should().Be(StepOutcomeKind.Skipped);
    }

    // ── Secret handling ─────────────────────────────────────────────────────

    [Fact]
    public async Task Sensitive_values_are_redacted_out_of_the_stored_instructions()
    {
        // Instructions are Octostache-evaluated against a bag holding DECRYPTED
        // sensitive variables, then stored in a plain text column that is served over
        // REST and rendered to holders of InterruptionView — who do not need
        // VariableView. So `#{ApiKey}` used to persist the real secret in the clear.
        //
        // WP3-b — this test was VACUOUS. It seeded a PROJECT variable, but a deployment
        // resolves against the RELEASE's variable snapshot, which the harness seeds empty:
        // `#{ApiKey}` was never substituted, so "does not contain the secret" held for the
        // wrong reason and the test passed with the redaction deleted. It now snapshots the
        // variable onto the release and asserts the template is GONE, which is what makes
        // the NotContain load-bearing.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "sec");

        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("hold", instructions: "Confirm the key #{ApiKey} is right."),
            StepBuilder.Script("after"));
        await harness.SnapshotVariableAsync(
            release.Id, project.Id, "ApiKey", "super-secret-value", sensitive: true);
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        gate.Instructions.Should().NotContain("#{ApiKey",
            because: "an unsubstituted template makes the assertion below meaningless");
        gate.Instructions.Should().NotContain("super-secret-value",
            because: "a sensitive variable must never be laundered into a cleartext " +
                     "column that InterruptionView alone can read");
        gate.Instructions.Should().Contain("Confirm the key",
            because: "the operator-facing text itself must still be readable");
    }

    [Fact]
    public async Task A_mis_cased_responsible_team_key_does_not_widen_the_approver_set()
    {
        // step.Config is a jsonb-deserialised dictionary with the DEFAULT ordinal
        // comparer, so a key typed `octopus.action.manual.responsibleteamids` returned
        // null -> zero tokens -> EMPTY list -> "anyone with the respond permission",
        // while the step editor still displayed the restriction. Fails OPEN.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "case");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("hold",
                responsibleTeamIds: "not-a-guid",
                responsibleTeamIdsKey: "octopus.action.manual.responsibleteamids"),
            StepBuilder.Script("after"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        (await harness.GetDeploymentAsync(taskId)).Status
            .Should().Be(DeploymentStatus.Failed,
                because: "the mis-cased key must still be READ, so its unresolvable id " +
                         "fails the gate rather than silently allowing anyone to approve");
        (await harness.GetInterruptionsAsync(taskId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Naming_the_Everyone_team_is_refused_rather_than_reported_as_a_restriction()
    {
        // PermissionEvaluator adds the system Everyone team to every authenticated user,
        // so accepting it made the gate answerable by anyone while the panel and the log
        // both claimed a restriction was enforced — a false compliance record.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "evry");
        var everyoneId = await harness.SeedEveryoneTeamAsync();

        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("hold", responsibleTeamIds: everyoneId.ToString()),
            StepBuilder.Script("after"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        (await harness.GetDeploymentAsync(taskId)).Status
            .Should().Be(DeploymentStatus.Failed);
        (await harness.GetInterruptionsAsync(taskId)).Should().BeEmpty(
            because: "a restriction that restricts nobody must be refused, not recorded");
    }

    // ── Cancel + freeze ─────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_a_paused_task_closes_its_gate_and_clears_the_checkpoint()
    {
        // Cancel left the gate Pending and the checkpoint on the terminal row, so the
        // panel still offered Approve/Reject on a CANCELLED deployment and the response
        // was accepted — writing an InterventionApproved audit row naming a real person
        // for a change that never ran.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "canc");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0", StepBuilder.Manual("hold"), StepBuilder.Script("after"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        await harness.CancelDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Cancelled);
        (await harness.GetPauseCheckpointAsync(taskId)).Should().BeNull(
            because: "a terminal row must not keep a blob of captured sensitive values " +
                     "that every DEK rotation then re-encrypts forever");

        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        gate.Status.Should().Be(InterruptionStatus.Cancelled,
            because: "an unanswerable gate must be closed, not left Pending for the " +
                     "sweeper to audit as a timeout days later");
        gate.Status.IsDecision().Should().BeFalse(
            because: "the task went terminal underneath it — nobody approved or refused");
    }

    [Fact]
    public async Task A_freeze_starting_during_the_approval_window_does_not_kill_the_resume()
    {
        // The freeze gate sits before the resume branch, so approving inside a freeze
        // window failed the task from Paused WITHOUT running cleanup — leaving
        // already-deployed targets mid-deploy.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "frz");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Script("before"),
            StepBuilder.Manual("hold"),
            StepBuilder.Script("after"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        await harness.SeedFreezeAsync(project.Id, env.Id);

        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Approved);
        await harness.RunDeploymentAsync(taskId);

        (await harness.GetDeploymentAsync(taskId)).Status
            .Should().Be(DeploymentStatus.Succeeded,
                because: "the freeze stops NEW work entering a window; this deployment " +
                         "entered before it and is already part-deployed, so refusing " +
                         "here would strand production targets mid-deploy");
    }

    // ── Timeout knob is actually read ───────────────────────────────────────

    [Fact]
    public async Task The_engine_default_timeout_is_read_from_configuration()
    {
        // The original test used 72 h — the shipped default — so it could not tell the
        // option being read from a hardcoded constant.
        await using var harness = new OrchestratorTestHarness(
            postgres, new EngineOptions { DefaultInterventionTimeout = TimeSpan.FromHours(5) });
        var (project, env, targets) = await SeedAsync(harness, "cfg");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0", StepBuilder.Manual("hold"), StepBuilder.Script("after"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        (gate.ExpiresUtc!.Value - gate.CreatedUtc)
            .Should().BeCloseTo(TimeSpan.FromHours(5), TimeSpan.FromMinutes(1),
                because: "a distinctive value proves Engine:DefaultInterventionTimeout " +
                         "is actually consulted");
    }

    // ── WP3-c (b): resume-checkpoint mismatches fail THROUGH cleanup ────────

    [Fact]
    public async Task A_live_variable_edit_during_the_approval_window_fails_a_runbook_run_through_cleanup()
    {
        // Runbook runs resolve variables LIVE (no snapshot), so editing a variable
        // that drives a ForEach collection during the approval window changes the
        // flattened wave count. Before WP3-c the restore failure aborted BEFORE the
        // resume branch: the run failed blaming "the process changed" and no
        // Failure/Always cleanup executed.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "lvar");
        var variableId = await harness.SeedVariableAsync(
            project.Id, "Servers", "one,two", VariableType.StringArray);

        var group = new StepBuilder
        {
            Name              = "loop",
            StepType          = KrakenStepTypes.StepGroup,
            Required          = false,
            ForEachCollection = "Servers",
        };
        var runId = await harness.CreateRunbookRunAsync(project.Id, env.Id, targets,
        [
            StepBuilder.Manual("hold"),
            group,
            new StepBuilder
            {
                Name = "iterate", StepType = "Octopus.Script", RunOnServer = true,
            }.InGroup(group.Id),
            new StepBuilder
            {
                Name = "cleanup", StepType = "Octopus.Script", RunOnServer = true,
                Required = false, Condition = StepCondition.Always,
                // Server scripts execute for real — an empty body is a runner error.
                Config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Octopus.Action.Script.ScriptBody"] = "Write-Host cleanup",
                },
            },
        ]);

        await harness.RunDeploymentAsync(runId);
        (await harness.GetServerTaskAsync(runId)).Status.Should().Be(DeploymentStatus.Paused);

        // The edit the checkpoint cannot survive: the ForEach collection grows,
        // so the resumed flatten partitions into one more wave.
        await harness.UpdateVariableValueAsync(
            variableId, "Servers", "one,two,three", VariableType.StringArray);

        var gate = (await harness.GetInterruptionsAsync(runId)).Single();
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Approved);
        await harness.RunDeploymentAsync(runId);

        var run = await harness.GetServerTaskAsync(runId);
        run.Status.Should().Be(DeploymentStatus.Failed,
            because: "the approved plan no longer matches — the run must not execute it");

        var outcomes = await harness.GetOutcomesAsync(runId);
        outcomes.Should().Contain(
            o => o.StepName == "cleanup" && o.Outcome == StepOutcomeKind.Succeeded,
            because: "the mismatch must fail THROUGH the wave loop so Condition=Always " +
                     "cleanup still executes — abort-before-resume left no cleanup at all");

        // The task is terminal, so the banner has been compacted from staging into
        // the step-log blobs — read through the stitched view.
        await using var db = harness.CreateContext();
        var lines = await KrakenDeploy.Server.Data.Services.TaskLogService.ReadAllAsync(db, runId);
        lines.Should().Contain(
            l => l.Level == "error" && l.Message.Contains("Runbook runs resolve variables live"),
            because: "the operator message must name a variable edit as the likely cause " +
                     "instead of blaming the runbook process");
    }

    [Fact]
    public async Task A_wave_kind_flip_during_the_approval_window_fails_but_still_runs_cleanup()
    {
        // The kind-flip protection (a step package installed during the window can
        // flip a remaining wave Target→Server) must KEEP failing the resume — WP3-c
        // only changes the shape: through the wave loop, with cleanup, not an abort
        // before the resume branch.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (project, env, targets) = await SeedAsync(harness, "kflip");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("hold"),
            StepBuilder.Script("work"), // target-side when the approval was given
            new StepBuilder
            {
                Name = "cleanup", StepType = "Octopus.Script", RunOnServer = true,
                Required = false, Condition = StepCondition.Always,
                // Server scripts execute for real — an empty body is a runner error.
                Config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Octopus.Action.Script.ScriptBody"] = "Write-Host cleanup",
                },
            });
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        var agent = harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        (await harness.GetServerTaskAsync(taskId)).Status.Should().Be(DeploymentStatus.Paused);

        // The registry is read LIVE on every dispatch, so this flips the "work"
        // wave's kind under the pause. The catalog is class-shared — restore it.
        await SetScriptLocusAsync(harness, StepTypeExecutionLocus.ServerRunner);
        try
        {
            var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
            await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Approved);
            await harness.RunDeploymentAsync(taskId);
        }
        finally
        {
            await SetScriptLocusAsync(harness, StepTypeExecutionLocus.AgentPackage);
        }

        (await harness.GetServerTaskAsync(taskId)).Status.Should().Be(DeploymentStatus.Failed,
            because: "a wave whose execution side changed under the approval must not run");

        agent.ReceivedPlans.Should().BeEmpty(
            because: "nothing may reach the target: 'work' was approved target-side and " +
                     "its wave no longer matches");

        var outcomes = await harness.GetOutcomesAsync(taskId);
        outcomes.Should().Contain(
            o => o.StepName == "cleanup" && o.Outcome == StepOutcomeKind.Succeeded,
            because: "Condition=Always cleanup still runs on the mismatch path");
        outcomes.Should().NotContain(
            o => o.StepName == "work" && o.Outcome == StepOutcomeKind.Succeeded,
            because: "the flipped wave's own work must not execute on either side");

        await using var db = harness.CreateContext();
        var lines = await KrakenDeploy.Server.Data.Services.TaskLogService.ReadAllAsync(db, taskId);
        lines.Should().Contain(
            l => l.Level == "error" && l.Message.Contains("locus changed"),
            because: "the kind-flip keeps its own message — a package change, not a variable");
    }

    /// <summary>Flips the seeded script step types' execution locus in the LIVE
    /// registry — the mechanism by which a step-package install during an approval
    /// window changes a wave's kind.</summary>
    private static async Task SetScriptLocusAsync(
        OrchestratorTestHarness harness, StepTypeExecutionLocus locus)
    {
        await using var db = harness.CreateContext();
        await db.StepTypes
            .Where(t => t.TypeId == "octopus.script")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExecutionLocus, locus));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<(Project Project, DeploymentEnvironment Env, List<DeploymentTarget> Targets)>
        SeedAsync(OrchestratorTestHarness harness, string prefix)
    {
        var suffix = $"{Guid.NewGuid():N}"[..8];
        return (
            await harness.SeedProjectAsync($"{prefix}p-{suffix}"),
            await harness.SeedEnvironmentAsync($"{prefix}e-{suffix}"),
            await harness.SeedTargetsAsync($"{prefix}t-{suffix}"));
    }
}
