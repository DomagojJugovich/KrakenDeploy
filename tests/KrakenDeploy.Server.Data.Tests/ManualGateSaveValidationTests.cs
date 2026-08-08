using FluentAssertions;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP3-a — a manual-intervention step's approver and timeout configuration is validated
/// at process SAVE, not only when a deployment reaches the gate.
/// <para>
/// The runtime refusals stay (a process can arrive by REST or by import without ever
/// passing through the editor, so the gate is the fail-closed backstop). What these
/// tests pin is WHEN the operator finds out: before, the only feedback was a failed
/// deployment at the moment somebody was waiting on an approval.
/// </para>
/// <para>
/// Both layers go through the one <see cref="ResponsibleTeamResolver"/>, so the rules
/// cannot drift between save time and run time — which is the actual reason this is
/// worth having rather than a second copy of the checks.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ManualGateSaveValidationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_gate_naming_an_unresolvable_team_is_refused_at_save()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);

        var error = await ValidateAsync(harness, project.SpaceId, new()
        {
            // An Octopus team id, exactly as an import leaves it.
            [ManualInterventionConfigKeys.ResponsibleTeamIds] = "teams-123",
        });

        error.Should().NotBeNull()
            .And.Contain("do not resolve",
                because: "silently dropping it would turn \"only these teams\" into " +
                         "\"anyone with the approve permission\"");
    }

    [Fact]
    public async Task A_gate_naming_the_Everyone_team_is_refused_at_save()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);
        var everyoneId = await harness.SeedEveryoneTeamAsync();

        var error = await ValidateAsync(harness, project.SpaceId, new()
        {
            [ManualInterventionConfigKeys.ResponsibleTeamIds] = everyoneId.ToString(),
        });

        error.Should().NotBeNull().And.Contain("restricts nobody");
    }

    [Fact]
    public async Task A_gate_naming_a_real_team_saves_and_resolves_its_name()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);
        var teamId = await harness.SeedTeamAsync("Change Board", project.SpaceId);

        var config = new Dictionary<string, string>
        {
            [ManualInterventionConfigKeys.ResponsibleTeamIds] = teamId.ToString(),
        };

        (await ValidateAsync(harness, project.SpaceId, config)).Should().BeNull();

        await using var db = harness.CreateContext();
        var resolution = await ResponsibleTeamResolver.ResolveAsync(
            db, project.SpaceId, "gate", config);

        resolution.IsValid.Should().BeTrue();
        resolution.TeamIds.Should().Equal([teamId]);
        resolution.TeamNames.Should().Equal(["Change Board"],
            because: "the names feed the pause log line and the audit trail, which used " +
                     "to say only \"1 responsible team(s)\"");
    }

    [Fact]
    public async Task A_team_from_another_Space_does_not_resolve()
    {
        // A Space-scoped team of ANOTHER Space must not be able to gate this task, and
        // must not silently degrade to "unrestricted" either.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);
        var foreignSpaceId = Guid.CreateVersion7();
        var foreignTeamId = await harness.SeedTeamAsync("Other Space Board", foreignSpaceId);

        var error = await ValidateAsync(harness, project.SpaceId, new()
        {
            [ManualInterventionConfigKeys.ResponsibleTeamIds] = foreignTeamId.ToString(),
        });

        error.Should().NotBeNull().And.Contain("do not resolve");
    }

    [Fact]
    public async Task An_empty_approver_list_is_valid_and_unrestricted()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);

        await using var db = harness.CreateContext();
        var resolution = await ResponsibleTeamResolver.ResolveAsync(
            db, project.SpaceId, "gate", new Dictionary<string, string>());

        resolution.IsValid.Should().BeTrue();
        resolution.TeamIds.Should().BeEmpty(
            because: "an author who leaves the field empty means anyone holding the " +
                     "respond permission — the one case where empty is intentional");
    }

    [Theory]
    [InlineData("0,5")]     // Croatian decimal comma — parses as nothing invariantly
    [InlineData("Infinity")]
    [InlineData("1e30")]
    [InlineData("-4")]
    [InlineData("later")]
    public async Task An_unusable_timeout_is_refused_at_save(string raw)
    {
        // At run time an unparseable value silently falls back to the 72 h engine
        // default, so an operator who typed 0,5 meaning thirty minutes would get three
        // days with no warning anywhere. Save time is where that must surface.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);

        var error = await ValidateAsync(harness, project.SpaceId, new()
        {
            [ManualInterventionConfigKeys.TimeoutHours] = raw,
        });

        error.Should().NotBeNull().And.Contain("Auto-fail after");
    }

    [Fact]
    public async Task A_zero_timeout_is_refused_at_save_with_its_own_reason()
    {
        // WP3-b reversal — "0" used to be accepted as "wait forever". It is the one
        // rejected value an operator may have typed deliberately, so the message explains
        // the consequence rather than just quoting a range: an unexpiring gate is skipped
        // by the timeout sweeper while its task keeps holding the (project, environment,
        // tenant) slot, so one unanswered gate blocks every later release of that pair
        // until somebody with TaskCancel intervenes.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);

        var error = await ValidateAsync(harness, project.SpaceId, new()
        {
            [ManualInterventionConfigKeys.TimeoutHours] = "0",
        });

        error.Should().NotBeNull()
            .And.Contain("0 is not allowed")
            .And.Contain("slot",
                because: "the operator needs the consequence — a blocked project + " +
                         "environment — not just \"out of range\"");
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("48")]
    [InlineData("")]
    public async Task A_usable_timeout_saves(string raw)
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);

        (await ValidateAsync(harness, project.SpaceId, new()
        {
            [ManualInterventionConfigKeys.TimeoutHours] = raw,
        })).Should().BeNull();
    }

    [Fact]
    public async Task A_non_gate_step_is_not_validated()
    {
        // The guard must be inert for every other step type — a script step whose config
        // happens to carry a stray key must still save.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);

        await using var db = harness.CreateContext();
        var error = await ResponsibleTeamResolver.ValidateStepConfigAsync(
            db, project.SpaceId, "Octopus.Script", "run-it",
            new Dictionary<string, string>
            {
                [ManualInterventionConfigKeys.ResponsibleTeamIds] = "teams-123",
                [ManualInterventionConfigKeys.TimeoutHours] = "nonsense",
            });

        error.Should().BeNull();
    }

    [Fact]
    public async Task The_approver_key_is_read_case_insensitively_at_save_too()
    {
        // The save-time path must not be the one place the casing trap survives: a
        // mis-cased key that read as "absent" would save happily and then fail the gate.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);

        var error = await ValidateAsync(harness, project.SpaceId, new(StringComparer.Ordinal)
        {
            ["octopus.action.manual.responsibleteamids"] = "teams-123",
        });

        error.Should().NotBeNull().And.Contain("do not resolve");
    }

    // ── WP3-b: the guard must cover every write path, not just ProcessService ─

    [Fact]
    public async Task The_shared_guard_throws_rather_than_returning_a_message()
    {
        // EnsureStepConfigValidAsync is now the single guard every save path calls —
        // ProcessService (add + update) AND RunbookService (add + update). Before it, the
        // two private copies lived on ProcessService alone, so the SAME step editor refused
        // a bad gate on a project process and silently accepted it on a runbook, which then
        // hard-failed when the run reached the gate with somebody waiting on an approval.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"sv-{Guid.NewGuid():N}"[..14]);

        await using var db = harness.CreateContext();
        var act = async () => await ResponsibleTeamResolver.EnsureStepConfigValidAsync(
            db, project.SpaceId, ManualInterventionConfigKeys.StepType, "gate",
            new Dictionary<string, string>
            {
                [ManualInterventionConfigKeys.ResponsibleTeamIds] = "teams-123",
            });

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("do not resolve");
    }

    [Theory]
    [InlineData(false)]  // null Space
    [InlineData(true)]   // Guid.Empty Space
    public async Task An_unresolvable_Space_refuses_instead_of_validating_against_nothing(bool empty)
    {
        // The Space id is nullable on purpose. A caller that cannot resolve the owning Space
        // cannot judge approver visibility either, and Guid.Empty is NOT a safe stand-in: it
        // matches only system teams, so a legitimate Space-scoped approver would be reported
        // as unresolvable and the operator sent chasing a typo that is not there. This is the
        // (Guid?) cast EnsureManualGateConfigForProjectAsync had dropped.
        await using var harness = new OrchestratorTestHarness(postgres);
        await using var db = harness.CreateContext();

        var act = async () => await ResponsibleTeamResolver.EnsureStepConfigValidAsync(
            db, empty ? Guid.Empty : null, ManualInterventionConfigKeys.StepType, "gate",
            new Dictionary<string, string>());

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("Space could not be resolved");
    }

    [Fact]
    public void IsGateStep_is_case_insensitive_so_callers_can_skip_the_Space_read()
    {
        // Callers use this to avoid resolving a Space id they would only need for the rare
        // gate. Were it case-SENSITIVE, a step stored as "octopus.manual" would skip
        // validation entirely while still gating at run time — the same casing trap that
        // let a mis-cased approver key widen the approver set to everyone.
        ResponsibleTeamResolver.IsGateStep("Octopus.Manual").Should().BeTrue();
        ResponsibleTeamResolver.IsGateStep("octopus.manual").Should().BeTrue();
        ResponsibleTeamResolver.IsGateStep("OCTOPUS.MANUAL").Should().BeTrue();
        ResponsibleTeamResolver.IsGateStep("Octopus.Script").Should().BeFalse();
        ResponsibleTeamResolver.IsGateStep(null).Should().BeFalse();
    }

    private static async Task<string?> ValidateAsync(
        OrchestratorTestHarness harness, Guid spaceId, Dictionary<string, string> config)
    {
        await using var db = harness.CreateContext();
        return await ResponsibleTeamResolver.ValidateStepConfigAsync(
            db, spaceId, ManualInterventionConfigKeys.StepType, "gate", config);
    }
}
