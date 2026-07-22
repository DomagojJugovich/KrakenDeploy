using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for library variable sets: CRUD, project inclusion, the
/// live resolution overlay (tenant &lt; library &lt; project), and the
/// release-snapshot folding with layer-dominant precedence.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class LibraryVariableSetTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // 32-byte dev key: base64 of "KrakenDeployDevMasterKey32Bytes!"
    private const string DevMasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";

    private static VariableService NewSvc(IDbContextFactory<KrakenDbContext> f)
        => new(f, TestCrypto.Service(DevMasterKey), new AllowAllPermissionEvaluator());

    // ── CRUD ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateLibrarySet_is_listed_and_not_a_project_set()
    {
        var svc = NewSvc(postgres);
        var set = await svc.CreateLibrarySetAsync("Shared DB", "common database settings");

        set.Kind.Should().Be(VariableSetKind.Library);
        set.ProjectId.Should().BeNull();
        set.Name.Should().Be("Shared DB");

        var all = await svc.GetLibrarySetsAsync();
        all.Should().Contain(s => s.Id == set.Id);
    }

    [Fact]
    public async Task DeleteLibrarySet_cascades_variables_and_inclusions()
    {
        var svc = NewSvc(postgres);
        var project = await SeedProjectAsync();
        var set = await svc.CreateLibrarySetAsync("Temp", null);
        await svc.CreateVariableInSetAsync(set.Id, "K", "v", VariableType.Text, null, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, set.Id, CallerAuthorization.System);

        (await svc.DeleteLibrarySetAsync(set.Id)).Should().BeTrue();

        await using var db = postgres.CreateContext();
        (await db.VariableSets.AnyAsync(vs => vs.Id == set.Id)).Should().BeFalse();
        (await db.ProjectVariableSetLinks.AnyAsync(l => l.VariableSetId == set.Id)).Should().BeFalse();
        (await db.Variables.AnyAsync(v => v.SetId == set.Id)).Should().BeFalse();
    }

    // ── Live resolution overlay ───────────────────────────────────────────────

    [Fact]
    public async Task Resolve_surfaces_library_variable_when_project_has_none()
    {
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync(["web"]);

        var set = await svc.CreateLibrarySetAsync("Lib", null);
        await svc.CreateVariableInSetAsync(set.Id, "Shared", "from-library", VariableType.Text, null, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, set.Id, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved.Should().ContainKey("Shared").WhoseValue.Should().Be("from-library");
    }

    [Fact]
    public async Task Resolve_project_variable_overrides_included_library_variable()
    {
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync(["web"]);

        var set = await svc.CreateLibrarySetAsync("Lib", null);
        await svc.CreateVariableInSetAsync(set.Id, "Db", "lib-db", VariableType.Text, null, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, set.Id, CallerAuthorization.System);

        await svc.CreateVariableAsync(project.Id, "Db", "project-db", VariableType.Text, null, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["Db"].Should().Be("project-db",
            because: "the project's own variable always wins over an included library set");
    }

    [Fact]
    public async Task Resolve_more_specific_library_beats_less_specific_project()
    {
        // Octopus precedence: scope specificity is PRIMARY; source (project >
        // library) only breaks EQUAL-scope ties. An env-scoped library variable
        // is more specific than an unscoped project variable, so library wins.
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync(["web"]);

        var set = await svc.CreateLibrarySetAsync("Lib", null);
        await svc.CreateVariableInSetAsync(set.Id, "Db", "lib-env-db", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, set.Id, CallerAuthorization.System);

        await svc.CreateVariableAsync(project.Id, "Db", "project-db", VariableType.Text, null, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["Db"].Should().Be("lib-env-db",
            because: "a more-specific (env-scoped) library variable beats a less-specific (unscoped) project one");
    }

    [Fact]
    public async Task Resolve_equal_scope_tie_goes_to_project()
    {
        // Same name, both UNSCOPED (equal specificity) → source tie-break: project.
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync(["web"]);

        var set = await svc.CreateLibrarySetAsync("Lib", null);
        await svc.CreateVariableInSetAsync(set.Id, "Db", "lib-db", VariableType.Text, null, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, set.Id, CallerAuthorization.System);

        await svc.CreateVariableAsync(project.Id, "Db", "project-db", VariableType.Text, null, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["Db"].Should().Be("project-db",
            because: "when scoped equally, project-defined beats library-defined");
    }

    [Fact]
    public async Task Resolve_specificity_order_target_beats_role_beats_environment()
    {
        // Octopus place-value order (KrakenDeploy dimensions): target > roles > env.
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync(["web"]);

        await svc.CreateVariableAsync(project.Id, "X", "env", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "X", "role", VariableType.Text,
            new VariableScope { Roles = ["web"] }, CallerAuthorization.System);

        var roleWins = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);
        roleWins["X"].Should().Be("role", because: "a role/tag scope is more specific than an environment scope");

        await svc.CreateVariableAsync(project.Id, "X", "target", VariableType.Text,
            new VariableScope { TargetId = target.Id }, CallerAuthorization.System);

        var targetWins = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);
        targetWins["X"].Should().Be("target", because: "a machine/target scope is the most specific dimension");
    }

    [Fact]
    public async Task Resolve_higher_sortorder_library_set_wins()
    {
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync([]);

        var first = await svc.CreateLibrarySetAsync("First", null);
        await svc.CreateVariableInSetAsync(first.Id, "Color", "from-first", VariableType.Text, null, CallerAuthorization.System);

        var second = await svc.CreateLibrarySetAsync("Second", null);
        await svc.CreateVariableInSetAsync(second.Id, "Color", "from-second", VariableType.Text, null, CallerAuthorization.System);

        // Included in order → second gets the higher SortOrder → wins.
        await svc.IncludeSetAsync(project.Id, first.Id, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, second.Id, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["Color"].Should().Be("from-second",
            because: "a later-included library set overlays (overwrites) an earlier one");
    }

    [Fact]
    public async Task Exclude_removes_the_library_overlay()
    {
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync([]);

        var set = await svc.CreateLibrarySetAsync("Lib", null);
        await svc.CreateVariableInSetAsync(set.Id, "Shared", "x", VariableType.Text, null, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, set.Id, CallerAuthorization.System);
        await svc.ExcludeSetAsync(project.Id, set.Id, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved.Should().NotContainKey("Shared");
    }

    // ── Release-snapshot folding ───────────────────────────────────────────────

    [Fact]
    public async Task CreateRelease_folds_library_variables_into_snapshot_with_layers()
    {
        var svc = NewSvc(postgres);
        var (project, env, _) = await SeedProjectWithEnvAsync();

        var set = await svc.CreateLibrarySetAsync("Lib", null);
        await svc.CreateVariableInSetAsync(set.Id, "OnlyLib", "lib-value", VariableType.Text, null, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, set.Id, CallerAuthorization.System);

        await svc.CreateVariableAsync(project.Id, "ProjVar", "proj-value", VariableType.Text, null, CallerAuthorization.System);
        await SeedSimpleProcessAsync(project.Id);

        var release = await new ReleaseService(postgres, new AllowAllPermissionEvaluator()).CreateAsync(project.Id, "1.0.0", CallerAuthorization.System);

        release.VariableSnapshot.Should().Contain(v =>
            v.Name == "OnlyLib" && v.Value == "lib-value" && v.Layer == 0);
        release.VariableSnapshot.Should().Contain(v =>
            v.Name == "ProjVar" && v.Value == "proj-value" && v.Layer == VariableSnapshot.ProjectLayer);
    }

    [Fact]
    public async Task ResolveFromSnapshot_project_wins_equal_scope_tie_and_library_surfaces()
    {
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedProjectWithEnvAsync();

        var set = await svc.CreateLibrarySetAsync("Lib", null);
        await svc.CreateVariableInSetAsync(set.Id, "Db", "lib-db", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableInSetAsync(set.Id, "OnlyLib", "lib-only", VariableType.Text, null, CallerAuthorization.System);
        await svc.IncludeSetAsync(project.Id, set.Id, CallerAuthorization.System);

        await svc.CreateVariableAsync(project.Id, "Db", "proj-db", VariableType.Text, null, CallerAuthorization.System);
        await SeedSimpleProcessAsync(project.Id);

        var release = await new ReleaseService(postgres, new AllowAllPermissionEvaluator()).CreateAsync(project.Id, "1.0.0", CallerAuthorization.System);

        var resolved = await svc.ResolveFromSnapshotAsync(
            release.VariableSnapshot, env.Id, target.Id, []);

        resolved["Db"].Should().Be("proj-db", because: "equal scope (both unscoped) → project beats library");
        resolved["OnlyLib"].Should().Be("lib-only", because: "library variables surface when the project has none");
    }

    // ── Channel scope ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_channel_scoped_wins_over_unscoped()
    {
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync([]);
        var channel = Guid.NewGuid();

        await svc.CreateVariableAsync(project.Id, "Feed", "default", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "Feed", "channel", VariableType.Text,
            new VariableScope { ChannelId = channel }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles, null, channel);

        resolved["Feed"].Should().Be("channel");
    }

    [Fact]
    public async Task Resolve_channel_scoped_excluded_for_different_channel()
    {
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync([]);

        await svc.CreateVariableAsync(project.Id, "Feed", "channel-a", VariableType.Text,
            new VariableScope { ChannelId = Guid.NewGuid() }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles, null, Guid.NewGuid());

        resolved.Should().NotContainKey("Feed",
            because: "a variable scoped to a different channel must not match");
    }

    [Fact]
    public async Task Resolve_environment_is_more_specific_than_channel()
    {
        // Octopus ordering: environment (item 7) is MORE specific than channel (item 8).
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync([]);
        var channel = Guid.NewGuid();

        await svc.CreateVariableAsync(project.Id, "X", "from-channel", VariableType.Text,
            new VariableScope { ChannelId = channel }, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "X", "from-env", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles, null, channel);

        resolved["X"].Should().Be("from-env", because: "environment is more specific than channel");
    }

    // ── Step scope (per-step manifest) ──────────────────────────────────────────

    [Fact]
    public async Task ResolveWithSteps_step_scoped_wins_for_its_step_only()
    {
        var svc = NewSvc(postgres);
        var env = Guid.NewGuid();
        var target = Guid.NewGuid();
        var stepRunSql = Guid.NewGuid();
        var stepOther = Guid.NewGuid();

        var snapshot = new List<VariableSnapshot>
        {
            new() { Name = "Conn", Value = "default", Type = VariableType.Text,
                    Layer = VariableSnapshot.ProjectLayer, Scope = new VariableScope() },
            new() { Name = "Conn", Value = "for-sql", Type = VariableType.Text,
                    Layer = VariableSnapshot.ProjectLayer,
                    Scope = new VariableScope { ProcessStepId = stepRunSql } },
        };

        var res = await svc.ResolveFromSnapshotWithStepsAsync(
            snapshot, env, target, [], tenantId: null, channelId: null,
            steps: [(stepRunSql, "Run SQL"), (stepOther, "Other")]);

        // Deployment-wide manifest excludes step-scoped variables.
        res.DeploymentWide["Conn"].Should().Be("default");
        // The "Run SQL" step gets the step-scoped value as a delta.
        res.PerStepDelta.Should().ContainKey(stepRunSql);
        res.PerStepDelta[stepRunSql]["Conn"].Should().Be("for-sql");
        // The other step has no delta — its winner equals the deployment-wide value.
        res.PerStepDelta.Should().NotContainKey(stepOther);
    }

    [Fact]
    public async Task ResolveWithSteps_no_step_scope_yields_no_per_step_deltas()
    {
        var svc = NewSvc(postgres);
        var snapshot = new List<VariableSnapshot>
        {
            new() { Name = "A", Value = "1", Type = VariableType.Text,
                    Layer = VariableSnapshot.ProjectLayer, Scope = new VariableScope() },
        };

        var res = await svc.ResolveFromSnapshotWithStepsAsync(
            snapshot, Guid.NewGuid(), Guid.NewGuid(), [], null, null,
            steps: [(Guid.NewGuid(), "Step 1"), (Guid.NewGuid(), "Step 2")]);

        res.DeploymentWide["A"].Should().Be("1");
        res.PerStepDelta.Should().BeEmpty(
            because: "no variable is step-scoped, so there is no per-step work");
    }

    [Fact]
    public async Task CreateVariableInSet_rejects_step_or_channel_scope_on_library_set()
    {
        var svc = NewSvc(postgres);
        var set = await svc.CreateLibrarySetAsync("Lib", null);

        var stepScoped = () => svc.CreateVariableInSetAsync(
            set.Id, "X", "v", VariableType.Text, new VariableScope { ProcessStepId = Guid.NewGuid() }, CallerAuthorization.System);
        await stepScoped.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*steps or channels*");

        var channelScoped = () => svc.CreateVariableInSetAsync(
            set.Id, "Y", "v", VariableType.Text, new VariableScope { ChannelId = Guid.NewGuid() }, CallerAuthorization.System);
        await channelScoped.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*steps or channels*");
    }

    [Fact]
    public async Task ResolveWithStepsAsync_live_step_scoped_wins_for_its_step()
    {
        // Live per-step path used by runbook runs (project variables, no snapshot).
        var svc = NewSvc(postgres);
        var (project, env, target) = await SeedContextAsync([]);
        var stepX = Guid.NewGuid();
        var stepY = Guid.NewGuid();

        await svc.CreateVariableAsync(project.Id, "Conn", "default", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "Conn", "for-x", VariableType.Text,
            new VariableScope { ProcessStepId = stepX }, CallerAuthorization.System);

        var res = await svc.ResolveWithStepsAsync(
            project.Id, env.Id, target.Id, target.Roles, tenantId: null, channelId: null,
            steps: [(stepX, "Step X"), (stepY, "Step Y")]);

        res.DeploymentWide["Conn"].Should().Be("default");
        res.PerStepDelta.Should().ContainKey(stepX);
        res.PerStepDelta[stepX]["Conn"].Should().Be("for-x");
        res.PerStepDelta.Should().NotContainKey(stepY);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Project> SeedProjectAsync()
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            Slug = $"lvs-{Guid.NewGuid():N}",
            Name = "Lib VarSet Test",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    private async Task<(Project project, DeploymentEnvironment env, DeploymentTarget target)>
        SeedContextAsync(string[] roles)
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            Slug = $"lvs-{Guid.NewGuid():N}",
            Name = "Lib VarSet Test",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        var env = new DeploymentEnvironment { Slug = $"env-{Guid.NewGuid():N}", Name = "Production" };
        var target = new DeploymentTarget
        {
            Name = $"tgt-{Guid.NewGuid():N}",
            Roles = [.. roles],
            TransportMode = TransportMode.Reverse,
        };
        db.Projects.Add(project);
        db.Environments.Add(env);
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();
        return (project, env, target);
    }

    private async Task<(Project project, DeploymentEnvironment env, DeploymentTarget target)>
        SeedProjectWithEnvAsync()
        => await SeedContextAsync([]);

    private async Task SeedSimpleProcessAsync(Guid projectId)
    {
        await using var db = postgres.CreateContext();
        var process = new Process { OwnerKind = ProcessOwnerKind.Project, OwnerId = projectId };
        db.Processes.Add(process);
        await db.SaveChangesAsync();

        db.ProcessSteps.Add(new ProcessStep
        {
            ProcessId = process.Id,
            Name = "Approve",
            StepType = "Octopus.Manual",
            PackageId = "",
            TargetRoles = [],
            Config = [],
            SortOrder = 0,
        });
        await db.SaveChangesAsync();
    }
}
