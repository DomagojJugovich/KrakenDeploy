using FluentAssertions;
using KrakenDeploy.Contracts.Logging;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP3-b — regressions for the defects the max-effort <c>/code-review</c> of 2026-07-30
/// found in WP3 and WP3-a.
/// <para>
/// Each test names the fail-open or fail-wrong behaviour it pins. The distinction matters
/// for this work package specifically: the gate IS the change-control control, so a bug
/// that lets a deployment proceed is not a degraded feature, it is the absence of the
/// feature while the UI claims otherwise.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ManualInterventionWp3bTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // ── The gate must honour a recorded decision ─────────────────────────────

    [Fact]
    public async Task A_rejected_gate_still_refuses_when_its_role_filter_stops_matching()
    {
        // THE most severe finding. EvaluateAsync used to apply the run condition and role
        // filter BEFORE reading approval state, and return Skip when nothing applied —
        // without ever loading the Interruption rows. Both inputs are evaluated against
        // LIVE state (StepAppliesToTarget reads the target's current roles, not the release
        // snapshot), so retagging a machine during the approval window made a recorded
        // REJECTION invisible: the gate reported Skip, the gated wave ran, and the task
        // finalised Succeeded — deploying exactly what a human refused, leaving only an
        // orphaned Rejected row and a Steps-tab line reading "roles don't overlap".
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-role");
        var env = await harness.SeedEnvironmentAsync("wp3b-role-env");
        var targets = await harness.SeedTargetsAsync("wp3b-role-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate", targetRoles: ["web"]),
            StepBuilder.Script("the-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Rejected, "no");

        // The remediation somebody would plausibly perform after refusing the change.
        await harness.SetTargetRolesAsync(targets[0].Id, "web-app");

        await harness.RunDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Failed,
            because: "a recorded refusal outranks the condition and role filter — both are " +
                     "live reads that can flip during a 72 h window");
    }

    [Fact]
    public async Task A_gate_that_is_neither_approved_nor_refused_does_not_run_the_wave()
    {
        // The approval test was a DENY-list ("anything that is not a rejection proceeds"),
        // so InterruptionStatus.Cancelled — and every status added in future — read as an
        // APPROVAL and ran the gated wave. The only thing standing in the way was
        // OutcomeFor throwing ArgumentOutOfRangeException afterwards, i.e. a framework
        // crash rather than a refusal. Forced directly, because ServerTaskCanceller (the
        // only production writer of Cancelled) is careful to make the task terminal in the
        // same transaction — the guard must hold on its own terms, not because today's
        // single writer happens to order two statements correctly.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-cancelled");
        var env = await harness.SeedEnvironmentAsync("wp3b-cancelled-env");
        var targets = await harness.SeedTargetsAsync("wp3b-cancelled-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate"),
            StepBuilder.Script("the-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        await harness.ForceInterruptionStatusAsync(gate.Id, InterruptionStatus.Cancelled);

        await harness.RunDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Failed,
            because: "a gate proceeds ONLY when explicitly Approved; anything else refuses");

        var outcomes = await harness.GetOutcomesAsync(taskId);
        outcomes.Should().NotContain(
            o => o.StepName == "the-work" && o.Outcome == StepOutcomeKind.Succeeded,
            because: "the gated wave must not have run");
    }

    // ── Secrets must not reach the persisted instructions ────────────────────

    [Fact]
    public async Task An_Octostache_filter_cannot_launder_a_sensitive_variable_into_the_instructions()
    {
        // Instructions are persisted in cleartext and served to holders of
        // InterruptionView, who do not need VariableView. The first fix redacted the
        // RENDERED text, but SecretRedactor is an ordinal substring match on the raw
        // secret, and Octostache 3.9.2 ships transforming filters — `| ToBase64`,
        // `| ToUpper`, `| Md5` — each producing a string it cannot recognise. So the
        // secret persisted, transformed but trivially recoverable, on a row no retention
        // sweep touches. The fix masks the VALUE in the dictionary before Octostache sees
        // it, which no filter can undo.
        const string secret = "Tr0ub4dor-and-3";
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-secret");
        var env = await harness.SeedEnvironmentAsync("wp3b-secret-env");
        var targets = await harness.SeedTargetsAsync("wp3b-secret-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate",
                instructions: "raw=#{Db.Password} b64=#{Db.Password | ToBase64} " +
                              "up=#{Db.Password | ToUpper} md5=#{Db.Password | Md5}"),
            StepBuilder.Script("the-work"));
        // Into the RELEASE snapshot — a project variable alone never reaches a deployment,
        // which is what made the earlier version of this assertion pass vacuously.
        await harness.SnapshotVariableAsync(
            release.Id, project.Id, "Db.Password", secret, sensitive: true);
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        var instructions = (await harness.GetInterruptionsAsync(taskId)).Single().Instructions;
        instructions.Should().NotBeNull();
        // Guard against a vacuous pass: if nothing substituted, every NotContain below
        // would hold trivially. The template must be GONE.
        instructions.Should().NotContain("#{Db.Password",
            because: "an unsubstituted template would make every assertion below vacuous");
        instructions.Should().NotContain(secret,
            because: "the plain value must never be persisted");
        instructions.Should().NotContain(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)),
            because: "| ToBase64 is exactly what defeated redact-after-evaluate");
        instructions.Should().NotContain(secret.ToUpperInvariant(),
            because: "| ToUpper defeats an ordinal substring match just as well");
        instructions.Should().Contain(SecretRedactor.Mask,
            because: "the approver should see that something was withheld, not a blank");
    }

    // ── Freeze on resume: block when nothing ran, continue mid-work ──────────

    [Fact]
    public async Task Approving_inside_a_freeze_is_refused_when_the_deployment_had_not_started()
    {
        // The freeze exemption for a resume was justified on "a paused task is already
        // part-deployed, and its remaining waves bring targets to a consistent version".
        // That premise is false for a gate authored as an EARLY step: the gate is evaluated
        // before its wave dispatches, so nothing has been touched. An operator without
        // DeploymentFreezeOverride could therefore park a deployment before a window opened
        // and have it approved straight through the middle of one.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-freeze-a");
        var env = await harness.SeedEnvironmentAsync("wp3b-freeze-a-env");
        var targets = await harness.SeedTargetsAsync("wp3b-freeze-a-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate-first"),
            StepBuilder.Script("the-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();

        // The window opens AFTER the deployment queued, which is the whole point: the
        // pre-dispatch freeze gate passed, and the approval arrives inside the window.
        await harness.SeedFreezeAsync(project.Id, env.Id);
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Approved, null);

        await harness.RunDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Failed,
            because: "resuming a deployment that had executed nothing is NEW work entering " +
                     "the freeze window, not a running deployment being allowed to finish");
    }

    [Fact]
    public async Task Approving_inside_a_freeze_still_completes_a_part_deployed_release()
    {
        // The other half of the split, and the reason it is a split rather than a blanket
        // block: once steps have actually run against targets, failing mid-way leaves the
        // farm split-version. That is the long-standing "let running deployments finish"
        // policy, and a gate late in the process is squarely inside it.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-freeze-b");
        var env = await harness.SeedEnvironmentAsync("wp3b-freeze-b-env");
        var targets = await harness.SeedTargetsAsync("wp3b-freeze-b-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Script("first-real-work"),
            StepBuilder.Manual("gate-after-work"),
            StepBuilder.Script("more-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();

        await harness.SeedFreezeAsync(project.Id, env.Id);
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Approved, null);

        await harness.RunDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Succeeded,
            because: "the first step already ran, so this IS a running deployment finishing");
    }

    // ── Every consumed gate leaves a trace ──────────────────────────────────

    [Fact]
    public async Task A_gate_excluded_by_its_run_condition_is_recorded_Skipped_even_when_a_peer_pauses()
    {
        // The worker strips the wave's WHOLE gate set on every branch, but EvaluateAsync
        // only surfaced the condition-excluded ones on the all-excluded Skip branch. A wave
        // holding one applicable gate and one excluded gate therefore dropped both and
        // recorded an outcome for neither — the excluded gate vanished from the Steps tab
        // entirely, unlike every other step type, which records Skipped.
        //
        // Condition=Failure on a healthy run is the natural way to author "approve the
        // rollback", so this shape is not contrived.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-mixed");
        var env = await harness.SeedEnvironmentAsync("wp3b-mixed-env");
        var targets = await harness.SeedTargetsAsync("wp3b-mixed-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("approve-rollback",
                condition: KrakenDeploy.Execution.StepCondition.Failure),
            StepBuilder.Manual("approve-prod-push"),
            StepBuilder.Script("the-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        // Only the applicable gate materialises a row and parks the task.
        var gates = await harness.GetInterruptionsAsync(taskId);
        gates.Should().ContainSingle()
            .Which.StepName.Should().Be("approve-prod-push",
                because: "a Condition=Failure gate must not pause a healthy deployment");

        var outcomes = await harness.GetOutcomesAsync(taskId);
        outcomes.Should().Contain(
            o => o.StepName == "approve-rollback" && o.Outcome == StepOutcomeKind.Skipped,
            because: "the excluded gate is removed from the wave, so it must say so on the " +
                     "Steps tab rather than silently disappearing");
    }

    [Fact]
    public async Task A_refused_gate_records_Skipped_for_the_steps_abandoned_with_its_wave()
    {
        // The rejection branch jumped straight to the next wave, past the "strip the gates,
        // run what is left" filter — so a server step sharing the gate's wave was neither
        // executed nor recorded, leaving a hole in the trail. The identical step one wave
        // later would have run under Condition=Always.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-companion");
        var env = await harness.SeedEnvironmentAsync("wp3b-companion-env");
        var targets = await harness.SeedTargetsAsync("wp3b-companion-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate"),
            StepBuilder.ServerScript("notify"),
            StepBuilder.Script("the-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        await harness.ResolveInterruptionAsync(gate.Id, InterruptionStatus.Rejected, "no");

        await harness.RunDeploymentAsync(taskId);

        var outcomes = await harness.GetOutcomesAsync(taskId);
        outcomes.Should().Contain(
            o => o.StepName == "notify",
            because: "a step abandoned with a refused wave still needs an outcome row — " +
                     "silently omitting it leaves the Steps tab claiming the step was " +
                     "never authored");
    }

    // ── The gate must stay answerable exactly as long as the task is ─────────

    [Fact]
    public async Task Cancelling_a_paused_task_closes_its_gate_in_the_same_transaction()
    {
        // The close used to be a separate statement AFTER the status flip had already
        // committed, so a crash — or just the request token firing when the operator
        // navigated away — left the task durably Cancelled with a Pending gate that
        // NOTHING could close: the timeout sweeper skips a non-Paused task, RespondAsync
        // refuses a terminal one, and retrying the cancel throws before reaching the close.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-cancel");
        var env = await harness.SeedEnvironmentAsync("wp3b-cancel-env");
        var targets = await harness.SeedTargetsAsync("wp3b-cancel-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate"),
            StepBuilder.Script("the-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);
        (await harness.GetInterruptionsAsync(taskId)).Single()
            .Status.Should().Be(InterruptionStatus.Pending);

        await harness.CancelDeploymentAsync(taskId);

        var task = await harness.GetServerTaskAsync(taskId);
        task.Status.Should().Be(DeploymentStatus.Cancelled);
        (await harness.GetInterruptionsAsync(taskId)).Single()
            .Status.Should().Be(InterruptionStatus.Cancelled,
                because: "an unanswerable gate must not keep advertising itself, and no " +
                         "other code path can ever close it once the task is terminal");
    }

    // ── The persisted record must be readable without the live team rows ────

    [Fact]
    public async Task A_gate_snapshots_its_responsible_team_NAMES_not_just_ids()
    {
        // Names are resolved at pause time and persisted, because they are frequently NOT
        // recoverable when needed: the break-glass path exists precisely because a named
        // team can be DELETED during the window, and the resolution audit entry — which
        // outlives the row — would then render as bare GUIDs. It also fixed DescribePause
        // degrading to "N responsible team(s)" after any restart.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-names");
        var env = await harness.SeedEnvironmentAsync("wp3b-names-env");
        var targets = await harness.SeedTargetsAsync("wp3b-names-t1");
        var teamId = await harness.SeedTeamAsync("Change Board", project.SpaceId);
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            StepBuilder.Manual("gate", responsibleTeamIds: teamId.ToString()),
            StepBuilder.Script("the-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        var gate = (await harness.GetInterruptionsAsync(taskId)).Single();
        gate.ResponsibleTeamIds.Should().Equal([teamId]);
        gate.ResponsibleTeamNames.Should().Equal(["Change Board"],
            because: "a change-control record reading \"team 3f9a…\" is not a record");
    }

    // ── Timeouts are always bounded ─────────────────────────────────────────

    [Fact]
    public async Task Every_gate_gets_an_expiry_so_the_sweeper_can_always_reap_it()
    {
        // Paused is in InFlightAfterClaim, so a parked task holds its
        // (project, environment, tenant) key — and InterruptionTimeoutJob filters on
        // `ExpiresUtc != null`. A gate with no expiry was therefore never reaped, and a
        // ProcessEdit-only author could block every later release of a project+environment
        // until somebody with TaskCancel intervened.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync("wp3b-expiry");
        var env = await harness.SeedEnvironmentAsync("wp3b-expiry-env");
        var targets = await harness.SeedTargetsAsync("wp3b-expiry-t1");
        var release = await harness.SeedReleaseAsync(
            project.Id, "1.0",
            // 0 is refused at save; a process that reached the gate carrying one anyway
            // must fall back to the engine default rather than wait forever.
            StepBuilder.Manual("gate", timeoutHours: 0),
            StepBuilder.Script("the-work"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        harness.ConnectFakeAgent(targets[0]);

        await harness.RunDeploymentAsync(taskId);

        (await harness.GetInterruptionsAsync(taskId)).Single()
            .ExpiresUtc.Should().NotBeNull(
                because: "an unexpiring gate parks its task on the F1 slot indefinitely");
    }
}
