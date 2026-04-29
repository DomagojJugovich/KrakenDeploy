using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for <see cref="VariableService"/>:
/// CRUD, AES-256-GCM encryption, scope resolution, and StringArray handling.
/// </summary>
[Collection("Postgres")]
public class VariableServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // 32-byte dev key: base64 of "KrakenDeployDevMasterKey32Bytes!"
    private const string DevMasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";

    private static VariableService CreateService(KrakenDbContext db)
        => new(db, new AesEncryptionService(DevMasterKey));

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<Project> SeedProjectAsync(KrakenDbContext db)
    {
        var project = new Project
        {
            Slug = $"var-test-{Guid.NewGuid():N}",
            Name = "Var Test Project",
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
        var svc = CreateService(db);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "MyVar", "hello-world", VariableType.Text, null);

        variable.Id.Should().NotBeEmpty();
        variable.Name.Should().Be("MyVar");
        variable.Value.Should().Be("hello-world");
        variable.Type.Should().Be(VariableType.Text);
    }

    [Fact]
    public async Task CreateVariable_encrypts_sensitive_value_at_rest()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "SecretKey", "s3cr3t!", VariableType.Sensitive, null);

        // Stored value must NOT be the plaintext.
        variable.Value.Should().NotBe("s3cr3t!", because: "sensitive vars must be encrypted at rest");

        // Round-trip: decrypt stored value.
        var crypto = new AesEncryptionService(DevMasterKey);
        crypto.Decrypt(variable.Value).Should().Be("s3cr3t!");
    }

    [Fact]
    public async Task GetVariables_redacts_sensitive_values()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var project = await SeedProjectAsync(db);

        await svc.CreateVariableAsync(project.Id, "PlainVar", "visible", VariableType.Text, null);
        await svc.CreateVariableAsync(project.Id, "SecretVar", "hidden", VariableType.Sensitive, null);

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
        var svc = CreateService(db);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "ToDelete", "bye", VariableType.Text, null);

        var deleted = await svc.DeleteVariableAsync(variable.Id);
        deleted.Should().BeTrue();

        var remaining = await svc.GetVariablesAsync(project.Id);
        remaining.Should().BeEmpty();
    }

    // ── StringArray tests ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVariable_normalises_comma_separated_string_array()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "Tags", "alpha, beta, gamma", VariableType.StringArray, null);

        // Should be stored as JSON array.
        variable.Value.Should().StartWith("[", because: "StringArray values are stored as JSON arrays");
    }

    // ── Scope resolution tests ───────────────────────────────────────────────

    [Fact]
    public async Task Resolve_returns_unscoped_variable_as_fallback()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        await svc.CreateVariableAsync(project.Id, "Greeting", "hello", VariableType.Text, null);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved.Should().ContainKey("Greeting").WhoseValue.Should().Be("hello");
    }

    [Fact]
    public async Task Resolve_env_scoped_wins_over_unscoped()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        // Unscoped fallback.
        await svc.CreateVariableAsync(project.Id, "DbServer", "dev-db", VariableType.Text, null);

        // Environment-scoped — should win.
        await svc.CreateVariableAsync(project.Id, "DbServer", "prod-db", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id });

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["DbServer"].Should().Be("prod-db",
            because: "environment-scoped variables take priority over unscoped ones");
    }

    [Fact]
    public async Task Resolve_env_and_role_scoped_beats_env_only()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        // Env-scoped only (score +4).
        await svc.CreateVariableAsync(project.Id, "MaxWorkers", "4", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id });

        // Env + role scoped (score +4 +2 = +6).
        await svc.CreateVariableAsync(project.Id, "MaxWorkers", "8", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id, Roles = ["web"] });

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["MaxWorkers"].Should().Be("8",
            because: "env+role scope is more specific than env-only");
    }

    [Fact]
    public async Task Resolve_env_scoped_var_excluded_for_different_env()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, _) = await SeedContextAsync(db, ["web"]);

        var otherEnvId = Guid.NewGuid();  // Non-existent env — just a different ID

        // Only scoped to otherEnv — should not match the real env.
        await svc.CreateVariableAsync(project.Id, "OnlyForOtherEnv", "xyz", VariableType.Text,
            new VariableScope { EnvironmentId = otherEnvId });

        var resolved = await svc.ResolveAsync(project.Id, env.Id, null, []);

        resolved.Should().NotContainKey("OnlyForOtherEnv");
    }

    [Fact]
    public async Task Resolve_decrypts_sensitive_variable()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        await svc.CreateVariableAsync(project.Id, "ApiKey", "topsecret", VariableType.Sensitive, null);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["ApiKey"].Should().Be("topsecret",
            because: "ResolveAsync must decrypt sensitive variables for the deployment plan");
    }

    [Fact]
    public async Task Resolve_returns_empty_dict_when_no_variable_set_exists()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
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
        var svc = CreateService(db);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "OldName", "old-value", VariableType.Text, null);

        var updated = await svc.UpdateVariableAsync(
            variable.Id, "NewName", "new-value", VariableType.Text, null);

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("NewName");
        updated.Value.Should().Be("new-value");
    }

    [Fact]
    public async Task UpdateVariable_re_encrypts_sensitive_value()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var project = await SeedProjectAsync(db);

        var variable = await svc.CreateVariableAsync(
            project.Id, "SecKey", "original-secret", VariableType.Sensitive, null);

        var updated = await svc.UpdateVariableAsync(
            variable.Id, "SecKey", "updated-secret", VariableType.Sensitive, null);

        updated.Should().NotBeNull();
        updated!.Value.Should().NotBe("updated-secret",
            because: "sensitive vars must be encrypted at rest after update");

        var crypto = new AesEncryptionService(DevMasterKey);
        crypto.Decrypt(updated.Value).Should().Be("updated-secret");
    }

    [Fact]
    public async Task UpdateVariable_returns_null_for_nonexistent_id()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);

        var result = await svc.UpdateVariableAsync(
            Guid.NewGuid(), "Ghost", "value", VariableType.Text, null);

        result.Should().BeNull();
    }

    // ── DeleteVariable edge cases ────────────────────────────────────────────

    [Fact]
    public async Task DeleteVariable_returns_false_for_nonexistent_id()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);

        var deleted = await svc.DeleteVariableAsync(Guid.NewGuid());

        deleted.Should().BeFalse();
    }

    // ── GetVariables edge cases ──────────────────────────────────────────────

    [Fact]
    public async Task GetVariables_returns_empty_list_for_project_without_variables()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
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
        var svc = CreateService(db);
        var (project, env, target) = await SeedContextAsync(db, ["api"]);

        // Unscoped fallback.
        await svc.CreateVariableAsync(project.Id, "Host", "global-host", VariableType.Text, null);

        // Target-scoped — should win.
        await svc.CreateVariableAsync(project.Id, "Host", "target-host", VariableType.Text,
            new VariableScope { TargetId = target.Id });

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["Host"].Should().Be("target-host",
            because: "target-scoped variable (score +1) beats unscoped fallback (score 0)");
    }

    [Fact]
    public async Task Resolve_target_scoped_var_excluded_for_different_target()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, _) = await SeedContextAsync(db, []);

        var otherTargetId = Guid.NewGuid();

        // Only scoped to a different target — must not match.
        await svc.CreateVariableAsync(project.Id, "TargetOnly", "secret", VariableType.Text,
            new VariableScope { TargetId = otherTargetId });

        var resolved = await svc.ResolveAsync(project.Id, env.Id, null, []);

        resolved.Should().NotContainKey("TargetOnly",
            because: "variable scoped to a different target must be excluded");
    }

    [Fact]
    public async Task Resolve_role_scoped_excluded_when_target_has_no_matching_role()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, target) = await SeedContextAsync(db, ["worker"]);

        // Variable scoped to role "web" — target only has "worker".
        await svc.CreateVariableAsync(project.Id, "WebOnly", "web-value", VariableType.Text,
            new VariableScope { Roles = ["web"] });

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved.Should().NotContainKey("WebOnly",
            because: "role-scoped variable must be excluded when target roles do not intersect");
    }

    [Fact]
    public async Task Resolve_env_target_and_role_scoped_beats_all_lower_priority_combinations()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, target) = await SeedContextAsync(db, ["web"]);

        // Unscoped (score 0).
        await svc.CreateVariableAsync(project.Id, "Priority", "score-0", VariableType.Text, null);

        // Env-only (score +4).
        await svc.CreateVariableAsync(project.Id, "Priority", "score-4", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id });

        // Env + roles (score +6).
        await svc.CreateVariableAsync(project.Id, "Priority", "score-6", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id, Roles = ["web"] });

        // Env + target + roles (score +7 — maximum).
        await svc.CreateVariableAsync(project.Id, "Priority", "score-7", VariableType.Text,
            new VariableScope { EnvironmentId = env.Id, TargetId = target.Id, Roles = ["web"] });

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved["Priority"].Should().Be("score-7",
            because: "env+target+role scope (score 7) is the maximum specificity combination");
    }

    [Fact]
    public async Task Resolve_string_array_variable_is_returned_as_json_array()
    {
        await using var db = postgres.CreateContext();
        var svc = CreateService(db);
        var (project, env, target) = await SeedContextAsync(db, []);

        await svc.CreateVariableAsync(
            project.Id, "Tags", "alpha, beta, gamma", VariableType.StringArray, null);

        var resolved = await svc.ResolveAsync(project.Id, env.Id, target.Id, target.Roles);

        resolved.Should().ContainKey("Tags");
        resolved["Tags"].Should().StartWith("[",
            because: "StringArray variables are returned as JSON arrays for the deployment worker to split");
    }
}
