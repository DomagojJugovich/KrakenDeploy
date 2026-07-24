using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for <see cref="VariableService"/>:
/// CRUD, AES-256-GCM encryption, scope resolution, and StringArray handling.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public class VariableServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // 32-byte dev key: base64 of "KrakenDeployDevMasterKey32Bytes!"
    private const string DevMasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";

    private static VariableService CreateService(IDbContextFactory<KrakenDbContext> factory)
        => new(factory, TestCrypto.Service(DevMasterKey), new AllowAllPermissionEvaluator());

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<Project> SeedProjectAsync(KrakenDbContext db)
    {
        var project = new Project
        {
            Slug = $"var-test-{Guid.NewGuid():N}",
            Name = "Var Test Project",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    private static async Task<(Project project, DeploymentEnvironment env, DeploymentTarget target)>
        SeedContextAsync(KrakenDbContext db, string[] roles)
    {
        var project = await SeedProjectAsync(db);

        var env = new DeploymentEnvironment
        {
            Slug = $"env-{Guid.NewGuid():N}",
            Name = "Production",
        };
        db.Environments.Add(env);

        var target = new DeploymentTarget
        {
            Name = $"target-{Guid.NewGuid():N}",
            Roles = [.. roles],
            TransportMode = TransportMode.Reverse,
        };
        db.DeploymentTargets.Add(target);

        await db.SaveChangesAsync();
        return (project, env, target);
    }

    // ── CRUD tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVariable_persists_plain_text_value()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "MyVar", "hello-world", VariableType.Text, null, CallerAuthorization.System);

        variable.Id.Should().NotBeEmpty();
        variable.Name.Should().Be("MyVar");
        variable.Value.Should().Be("hello-world");
        variable.Type.Should().Be(VariableType.Text);
    }

    [Fact]
    public async Task CreateVariable_encrypts_sensitive_value_at_rest()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "SecretKey", "s3cr3t!", VariableType.Sensitive, null, CallerAuthorization.System);

        // Stored value must NOT be the plaintext.
        variable.Value.Should().NotBe("s3cr3t!", because: "sensitive vars must be encrypted at rest");

        // Round-trip: decrypt stored value.
        var crypto = TestCrypto.Service(DevMasterKey);
        crypto.Decrypt(variable.Value).Should().Be("s3cr3t!");
    }

    [Fact]
    public async Task GetVariables_redacts_sensitive_values()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        await svc.CreateVariableAsync(project.Id, "PlainVar", "visible", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "SecretVar", "hidden", VariableType.Sensitive, null, CallerAuthorization.System);

        var dtos = await svc.GetVariablesAsync(project.Id);

        dtos.Should().HaveCount(2);
        dtos.Single(v => v.Name == "PlainVar").Value.Should().Be("visible");
        dtos.Single(v => v.Name == "SecretVar").Value.Should().Be("***",
            because: "sensitive variable values must be redacted in the API response");
    }

    [Fact]
    public async Task DeleteVariable_removes_variable()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "ToDelete", "bye", VariableType.Text, null, CallerAuthorization.System);

        var deleted = await svc.DeleteVariableAsync(variable.Id, CallerAuthorization.System);
        deleted.Should().BeTrue();

        var remaining = await svc.GetVariablesAsync(project.Id);
        remaining.Should().BeEmpty();
    }

    // ── StringArray tests ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVariable_normalises_comma_separated_string_array()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "Tags", "alpha, beta, gamma", VariableType.StringArray, null, CallerAuthorization.System);

        // Should be stored as JSON array.
        variable.Value.Should().StartWith("[", because: "StringArray values are stored as JSON arrays");
    }

    // ── Scope resolution tests ───────────────────────────────────────────────

    [Fact]
    public async Task Resolve_returns_unscoped_variable_as_fallback()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        await svc.CreateVariableAsync(project.Id, "Greeting", "hello", VariableType.Text, null, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved.Should().ContainKey("Greeting").WhoseValue.Should().Be("hello");
    }

    [Fact]
    public async Task Resolve_env_scoped_wins_over_unscoped()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        // Unscoped fallback.
        await svc.CreateVariableAsync(project.Id, "DbServer", "dev-db", VariableType.Text, null, CallerAuthorization.System);

        // Environment-scoped — should win.
        await svc.CreateVariableAsync(project.Id, "DbServer", "prod-db", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["DbServer"].Should().Be("prod-db",
            because: "environment-scoped variables take priority over unscoped ones");
    }

    [Fact]
    public async Task Resolve_env_and_role_scoped_beats_env_only()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        // Env-scoped only (score +4).
        await svc.CreateVariableAsync(project.Id, "MaxWorkers", "4", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);

        // Env + role scoped (score +4 +2 = +6).
        await svc.CreateVariableAsync(project.Id, "MaxWorkers", "8", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id, Roles = ["web"] }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["MaxWorkers"].Should().Be("8",
            because: "env+role scope is more specific than env-only");
    }

    [Fact]
    public async Task Resolve_env_scoped_var_excluded_for_different_env()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, _) = await SeedContextAsync(db, ["web"]);

        var otherEnvId = Guid.NewGuid();  // Non-existent env — just a different ID

        // Only scoped to otherEnv — should not match the real env.
        await svc.CreateVariableAsync(project.Id, "OnlyForOtherEnv", "xyz", VariableType.Text,
            new VariableScope { EnvironmentId = otherEnvId }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, null, []);

        resolved.Should().NotContainKey("OnlyForOtherEnv");
    }

    [Fact]
    public async Task Resolve_decrypts_sensitive_variable()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        await svc.CreateVariableAsync(project.Id, "ApiKey", "topsecret", VariableType.Sensitive, null, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["ApiKey"].Should().Be("topsecret",
            because: "ResolveAsync must decrypt sensitive variables for the deployment plan");
    }

    [Fact]
    public async Task Resolve_returns_empty_dict_when_no_variable_set_exists()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        // Don't create any variables — there's no variable set yet.
        var resolved = await svc.ResolveAsync(project.Id, Guid.NewGuid(), null, []);

        resolved.Should().BeEmpty();
    }

    // ── UpdateVariable tests ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateVariable_changes_name_and_value()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "OldName", "old-value", VariableType.Text, null, CallerAuthorization.System);

        var updated = await svc.UpdateVariableAsync(
            variable.Id, "NewName", "new-value", VariableType.Text, null, CallerAuthorization.System);

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("NewName");
        updated.Value.Should().Be("new-value");
    }

    [Fact]
    public async Task UpdateVariable_re_encrypts_sensitive_value()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "SecKey", "original-secret", VariableType.Sensitive, null, CallerAuthorization.System);

        var updated = await svc.UpdateVariableAsync(
            variable.Id, "SecKey", "updated-secret", VariableType.Sensitive, null, CallerAuthorization.System);

        updated.Should().NotBeNull();
        updated!.Value.Should().NotBe("updated-secret",
            because: "sensitive vars must be encrypted at rest after update");

        var crypto = TestCrypto.Service(DevMasterKey);
        crypto.Decrypt(updated.Value).Should().Be("updated-secret");
    }

    [Fact]
    public async Task UpdateVariable_returns_null_for_nonexistent_id()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);

        var result = await svc.UpdateVariableAsync(
            Guid.NewGuid(), "Ghost", "value", VariableType.Text, null, CallerAuthorization.System);

        result.Should().BeNull();
    }

    // ── DeleteVariable edge cases ────────────────────────────────────────────

    [Fact]
    public async Task DeleteVariable_returns_false_for_nonexistent_id()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);

        var deleted = await svc.DeleteVariableAsync(Guid.NewGuid(), CallerAuthorization.System);

        deleted.Should().BeFalse();
    }

    // ── GetVariables edge cases ──────────────────────────────────────────────

    [Fact]
    public async Task GetVariables_returns_empty_list_for_project_without_variables()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        // No variables created — set may not even exist yet.
        var result = await svc.GetVariablesAsync(project.Id);

        result.Should().BeEmpty();
    }

    // ── Additional scope resolution tests ────────────────────────────────────

    [Fact]
    public async Task Resolve_target_scoped_wins_over_unscoped()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, ["api"]);

        // Unscoped fallback.
        await svc.CreateVariableAsync(project.Id, "Host", "global-host", VariableType.Text, null, CallerAuthorization.System);

        // Target-scoped — should win.
        await svc.CreateVariableAsync(project.Id, "Host", "target-host", VariableType.Text,
            new VariableScope { TargetId = target.Id }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["Host"].Should().Be("target-host",
            because: "target-scoped variable (score +1) beats unscoped fallback (score 0)");
    }

    [Fact]
    public async Task Resolve_target_scoped_var_excluded_for_different_target()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, _) = await SeedContextAsync(db, []);

        var otherTargetId = Guid.NewGuid();

        // Only scoped to a different target — must not match.
        await svc.CreateVariableAsync(project.Id, "TargetOnly", "secret", VariableType.Text,
            new VariableScope { TargetId = otherTargetId }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, null, []);

        resolved.Should().NotContainKey("TargetOnly",
            because: "variable scoped to a different target must be excluded");
    }

    [Fact]
    public async Task Resolve_role_scoped_excluded_when_target_has_no_matching_role()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, ["worker"]);

        // Variable scoped to role "web" — target only has "worker".
        await svc.CreateVariableAsync(project.Id, "WebOnly", "web-value", VariableType.Text,
            new VariableScope { Roles = ["web"] }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved.Should().NotContainKey("WebOnly",
            because: "role-scoped variable must be excluded when target roles do not intersect");
    }

    [Fact]
    public async Task Resolve_env_target_and_role_scoped_beats_all_lower_priority_combinations()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        // Unscoped (score 0).
        await svc.CreateVariableAsync(project.Id, "Priority", "score-0", VariableType.Text, null, CallerAuthorization.System);

        // Env-only (score +4).
        await svc.CreateVariableAsync(project.Id, "Priority", "score-4", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);

        // Env + roles (score +6).
        await svc.CreateVariableAsync(project.Id, "Priority", "score-6", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id, Roles = ["web"] }, CallerAuthorization.System);

        // Env + target + roles (score +7 — maximum).
        await svc.CreateVariableAsync(project.Id, "Priority", "score-7", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id, TargetId = target.Id, Roles = ["web"] }, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["Priority"].Should().Be("score-7",
            because: "env+target+role scope (score 7) is the maximum specificity combination");
    }

    [Fact]
    public async Task Resolve_string_array_variable_is_returned_as_json_array()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, []);

        await svc.CreateVariableAsync(
            project.Id, "Tags", "alpha, beta, gamma", VariableType.StringArray, null, CallerAuthorization.System);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved.Should().ContainKey("Tags");
        resolved["Tags"].Should().StartWith("[",
            because: "StringArray variables are returned as JSON arrays for the deployment worker to split");
    }

    // ── SearchVariables (cross-project grid query) ───────────────────────────

    [Fact]
    public async Task SearchVariables_name_filter_is_case_insensitive()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        await svc.CreateVariableAsync(project.Id, "ConnectionString", "x", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "Unrelated", "y", VariableType.Text, null, CallerAuthorization.System);

        var hits = await svc.SearchVariablesAsync(projectId: project.Id, nameContains: "connection");

        hits.Should().ContainSingle(v => v.Name == "ConnectionString",
            because: "the UI promises case-insensitive name search");
    }

    [Fact]
    public async Task SearchVariables_escapes_like_wildcards_in_the_search_term()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);

        await svc.CreateVariableAsync(project.Id, "Rate100%", "x", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "Rate100x", "y", VariableType.Text, null, CallerAuthorization.System);

        var hits = await svc.SearchVariablesAsync(projectId: project.Id, nameContains: "100%");

        hits.Should().ContainSingle(v => v.Name == "Rate100%",
            because: "a literal % in the term must not act as a LIKE wildcard");
    }

    [Fact]
    public async Task SearchVariables_empty_projectIds_matches_nothing()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var project = await SeedProjectAsync(db);
        await svc.CreateVariableAsync(project.Id, "SomeVar", "x", VariableType.Text, null, CallerAuthorization.System);

        // Empty collection = a project-tag filter that matched no project.
        var hits = await svc.SearchVariablesAsync(projectIds: Array.Empty<Guid>());

        hits.Should().BeEmpty(
            because: "an empty containment set must not silently widen to 'no filter'");
    }

    [Fact]
    public async Task SearchVariables_projectIds_restricts_to_the_given_projects()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var projectA = await SeedProjectAsync(db);
        var projectB = await SeedProjectAsync(db);

        await svc.CreateVariableAsync(projectA.Id, "VarA", "x", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableAsync(projectB.Id, "VarB", "y", VariableType.Text, null, CallerAuthorization.System);

        var hits = await svc.SearchVariablesAsync(projectIds: [projectA.Id]);

        hits.Should().OnlyContain(v => v.Set.ProjectId == projectA.Id);
        hits.Should().ContainSingle(v => v.Name == "VarA");
    }

    // ── ReplaceVariableScopes (atomic multi-scope expansion) ─────────────────

    [Fact]
    public async Task ReplaceVariableScopes_expands_to_one_row_per_scope_and_removes_original()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, envA, _) = await SeedContextAsync(db, []);
        var envB = new DeploymentEnvironment { Slug = $"env-{Guid.NewGuid():N}", Name = "Staging" };
        db.Environments.Add(envB);
        await db.SaveChangesAsync();

        var original = await svc.CreateVariableAsync(
            project.Id, "Multi", "shared-value", VariableType.Text, null, CallerAuthorization.System);

        var created = await svc.ReplaceVariableScopesAsync(
            original.Id,
            [new VariableScope { EnvironmentId = envA.Id }, new VariableScope { EnvironmentId = envB.Id }],
            CallerAuthorization.System);

        created.Should().HaveCount(2);
        var rows = (await svc.SearchVariablesAsync(projectId: project.Id)).Where(v => v.Name == "Multi").ToList();
        rows.Should().HaveCount(2);
        rows.Should().NotContain(v => v.Id == original.Id, because: "the original is replaced by the clones");
        rows.Select(v => v.Scope.EnvironmentId).Should().BeEquivalentTo(new Guid?[] { envA.Id, envB.Id });
        rows.Should().OnlyContain(v => v.Value == "shared-value");
    }

    [Fact]
    public async Task ReplaceVariableScopes_single_scope_updates_in_place()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, _) = await SeedContextAsync(db, []);

        var original = await svc.CreateVariableAsync(
            project.Id, "Single", "v", VariableType.Text, null, CallerAuthorization.System);

        var result = await svc.ReplaceVariableScopesAsync(
            original.Id, [new VariableScope { EnvironmentId = env.Id }], CallerAuthorization.System);

        result.Should().ContainSingle().Which.Id.Should().Be(original.Id,
            because: "a single scope is an in-place update, not a replace");
        (await svc.GetVariableAsync(original.Id))!.Scope.EnvironmentId.Should().Be(env.Id);
    }

    [Fact]
    public async Task ReplaceVariableScopes_refuses_sensitive_multi_scope_and_keeps_original()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, envA, _) = await SeedContextAsync(db, []);
        var envB = new DeploymentEnvironment { Slug = $"env-{Guid.NewGuid():N}", Name = "Staging" };
        db.Environments.Add(envB);
        await db.SaveChangesAsync();

        var original = await svc.CreateVariableAsync(
            project.Id, "Secret", "s3cr3t", VariableType.Sensitive, null, CallerAuthorization.System);

        var act = () => svc.ReplaceVariableScopesAsync(
            original.Id,
            [new VariableScope { EnvironmentId = envA.Id }, new VariableScope { EnvironmentId = envB.Id }],
            CallerAuthorization.System);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await svc.GetVariableAsync(original.Id)).Should().NotBeNull(
            because: "a refused expansion must leave the variable untouched");
    }

    [Fact]
    public async Task ReplaceVariableScopes_library_step_scope_is_refused_atomically()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (_, env, _) = await SeedContextAsync(db, []);

        var lib = await svc.CreateLibrarySetAsync("Atomic Lib", null);
        var original = await svc.CreateVariableInSetAsync(
            lib.Id, "LibVar", "x", VariableType.Text, null, CallerAuthorization.System);

        var act = () => svc.ReplaceVariableScopesAsync(
            original.Id,
            [new VariableScope { EnvironmentId = env.Id }, new VariableScope { ProcessStepId = Guid.NewGuid() }],
            CallerAuthorization.System);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "library variables cannot carry step scope");
        var still = await svc.GetVariablesInSetAsync(lib.Id);
        still.Should().ContainSingle(v => v.Id == original.Id,
            because: "the guard fires before any row is touched — no clones, original intact");
    }

    // ── PreviewResolve (provenance) ──────────────────────────────────────────

    [Fact]
    public async Task PreviewResolve_reports_winner_source_scope_specificity_and_candidate_count()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, []);

        var lib = await svc.CreateLibrarySetAsync("Common Config", null);
        await svc.IncludeSetAsync(project.Id, lib.Id, CallerAuthorization.System);

        await svc.CreateVariableAsync(
            project.Id, "Url", "project-default", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableInSetAsync(
            lib.Id, "Url", "lib-env-scoped", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);

        var rows = await svc.PreviewResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        var url = rows.Single(r => r.Name == "Url");
        url.Value.Should().Be("lib-env-scoped", because: "env scope beats the unscoped project default");
        url.Source.Should().Be("Common Config");
        url.Specificity.Should().Be(1 << 3, because: "environment is rank bit 3 in the place-value order");
        url.CandidateCount.Should().Be(2);
        url.Scope.EnvironmentId.Should().Be(env.Id);
    }

    [Fact]
    public async Task PreviewResolve_channel_scoped_variable_resolves_only_when_that_channel_is_given()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, []);
        var channelId = Guid.NewGuid();

        await svc.CreateVariableAsync(
            project.Id, "Feature", "base", VariableType.Text, null, CallerAuthorization.System);
        await svc.CreateVariableAsync(
            project.Id, "Feature", "channel-override", VariableType.Text,
            new VariableScope { ChannelId = channelId }, CallerAuthorization.System);

        // No channel context — the channel-scoped row is excluded, base wins.
        var noChannel = await svc.PreviewResolveAsync(project.Id, env.Id, target.Id, target.Roles);
        var baseRow = noChannel.Single(r => r.Name == "Feature");
        baseRow.Value.Should().Be("base");
        baseRow.CandidateCount.Should().Be(1, because: "the channel-scoped definition must not even be a candidate");

        // With the matching channel — the channel-scoped row wins on specificity.
        var withChannel = await svc.PreviewResolveAsync(
            project.Id, env.Id, target.Id, target.Roles, channelId: channelId);
        var overrideRow = withChannel.Single(r => r.Name == "Feature");
        overrideRow.Value.Should().Be("channel-override",
            because: "a channel-scoped definition must resolve exactly as it would at deploy time");
        overrideRow.CandidateCount.Should().Be(2);
    }

    [Fact]
    public async Task PreviewResolve_masks_sensitive_winners_without_decrypting()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, []);

        await svc.CreateVariableAsync(
            project.Id, "ApiKey", "super-secret", VariableType.Sensitive, null, CallerAuthorization.System);

        var rows = await svc.PreviewResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        var row = rows.Single(r => r.Name == "ApiKey");
        row.Sensitive.Should().BeTrue();
        row.Value.Should().BeEmpty(because: "the preview must never carry the decrypted secret");
        row.Source.Should().Be("Project");
    }

    // ── PreviewResolve ambiguity detection ───────────────────────────────────

    [Fact]
    public async Task PreviewResolve_flags_ambiguous_when_equal_specificity_and_origin_disagree_on_value()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, []);

        // Two project variables, same name, IDENTICAL scope, DIFFERENT values →
        // same specificity + same origin → the winner is picked arbitrarily.
        await svc.CreateVariableAsync(project.Id, "Dup", "value-a", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "Dup", "value-b", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);

        var rows = await svc.PreviewResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        var dup = rows.Single(r => r.Name == "Dup");
        dup.Ambiguous.Should().BeTrue(because: "two equally-scoped project vars with differing values are non-deterministic");
        dup.TiedCount.Should().Be(2);
    }

    [Fact]
    public async Task PreviewResolve_not_ambiguous_when_equal_specificity_broken_by_origin()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, []);

        var lib = await svc.CreateLibrarySetAsync("Overlay", null);
        await svc.IncludeSetAsync(project.Id, lib.Id, CallerAuthorization.System);

        // Same name + same specificity but DIFFERENT origins (project vs library):
        // project wins deterministically, so this is NOT flagged.
        await svc.CreateVariableAsync(project.Id, "Edge", "from-project", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);
        await svc.CreateVariableInSetAsync(lib.Id, "Edge", "from-library", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);

        var rows = await svc.PreviewResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        var edge = rows.Single(r => r.Name == "Edge");
        edge.Value.Should().Be("from-project");
        edge.Ambiguous.Should().BeFalse(because: "a same-specificity clash resolved by origin (project > library) is deterministic");
        edge.TiedCount.Should().Be(1, because: "only the project definition sits at the winning specificity AND origin");
    }

    [Fact]
    public async Task PreviewResolve_not_ambiguous_when_tied_values_are_identical()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(postgres);
        var (project, env, target) = await SeedContextAsync(db, []);

        // Tied at the same specificity + origin, but SAME value → harmless, not flagged.
        await svc.CreateVariableAsync(project.Id, "Same", "one", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);
        await svc.CreateVariableAsync(project.Id, "Same", "one", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id }, CallerAuthorization.System);

        var rows = await svc.PreviewResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        var same = rows.Single(r => r.Name == "Same");
        same.TiedCount.Should().Be(2);
        same.Ambiguous.Should().BeFalse(because: "identical values tying produce the same result either way");
    }
}
