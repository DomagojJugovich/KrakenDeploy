using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for the Octopus-style "Update Variables" / release
/// variable-snapshot feature. Covers:
/// <list type="bullet">
///   <item><c>ReleaseService.CreateAsync</c> freezes the project's variable
///         set into the release at creation time.</item>
///   <item><c>ReleaseService.UpdateVariablesAsync</c> re-snapshots after
///         later variable edits.</item>
///   <item><c>VariableService.ResolveFromSnapshotAsync</c> applies the
///         same scope-resolution rules to a frozen snapshot as
///         <c>ResolveAsync</c> does to live variables.</item>
///   <item>Sensitive values stay encrypted in the snapshot and decrypt
///         correctly at resolve time.</item>
///   <item>Pre-snapshot releases (<c>VariableSnapshotUpdatedUtc IS NULL</c>)
///         keep the empty snapshot — the worker is responsible for the
///         live-resolve fallback in that case.</item>
/// </list>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ReleaseVariableSnapshotTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // 32-byte dev key: base64 of "KrakenDeployDevMasterKey32Bytes!"
    private const string DevMasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";

    private static VariableService NewVarService(IDbContextFactory<KrakenDbContext> f)
        => new(f, TestCrypto.Service(DevMasterKey));

    // ── ReleaseService.CreateAsync ────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_freezes_the_projects_variables_into_the_release()
    {
        var (project, env, _) = await SeedProjectWithEnvAsync();

        // Seed the project's variable set: one unscoped + one env-scoped.
        var vars = NewVarService(postgres);
        await vars.CreateVariableAsync(project.Id, "Greeting", "Hello",   VariableType.Text, scope: new VariableScope());
        await vars.CreateVariableAsync(project.Id, "Db.Host",  "prod-db", VariableType.Text, scope: new VariableScope { EnvironmentId = env.Id });
        await SeedSimpleProcessAsync(project.Id);

        // Cut the release.
        var releaseSvc = new ReleaseService(postgres);
        var release    = await releaseSvc.CreateAsync(project.Id, "1.0.0");

        // Snapshot exists + timestamped + carries both variables verbatim.
        release.VariableSnapshotUpdatedUtc.Should().NotBeNull();
        release.VariableSnapshot.Should().HaveCount(2);
        release.VariableSnapshot.Should().Contain(v => v.Name == "Greeting" && v.Value == "Hello"
            && v.Scope.EnvironmentId == null);
        release.VariableSnapshot.Should().Contain(v => v.Name == "Db.Host" && v.Value == "prod-db"
            && v.Scope.EnvironmentId == env.Id);
    }

    [Fact]
    public async Task CreateAsync_snapshots_an_empty_list_when_project_has_no_variables()
    {
        var (project, _, _) = await SeedProjectWithEnvAsync();
        await SeedSimpleProcessAsync(project.Id);

        var releaseSvc = new ReleaseService(postgres);
        var release    = await releaseSvc.CreateAsync(project.Id, "1.0.0");

        release.VariableSnapshot.Should().BeEmpty();
        release.VariableSnapshotUpdatedUtc.Should().NotBeNull(
            "even an empty snapshot is an authoritative 'pinned-empty' state — " +
            "the deployment worker uses null timestamp to fall back to live, " +
            "non-null with empty list = explicitly empty");
    }

    // ── ReleaseService.UpdateVariablesAsync ───────────────────────────────

    [Fact]
    public async Task UpdateVariablesAsync_re_snapshots_after_later_edits()
    {
        var (project, env, _) = await SeedProjectWithEnvAsync();
        var vars = NewVarService(postgres);
        var liveVar = await vars.CreateVariableAsync(project.Id, "Db.Host", "old-db", VariableType.Text,
            scope: new VariableScope { EnvironmentId = env.Id });
        await SeedSimpleProcessAsync(project.Id);

        // Cut release with the old value.
        var releaseSvc = new ReleaseService(postgres);
        var release    = await releaseSvc.CreateAsync(project.Id, "1.0.0");
        release.VariableSnapshot.Single(v => v.Name == "Db.Host").Value.Should().Be("old-db");
        var firstStamp = release.VariableSnapshotUpdatedUtc;

        // Variable changes after the release was cut — old snapshot must stay frozen.
        await vars.UpdateVariableAsync(liveVar.Id, "Db.Host", "new-db", VariableType.Text,
            scope: new VariableScope { EnvironmentId = env.Id });
        await using (var db = postgres.CreateContext())
        {
            var reloaded = await db.Releases.FirstAsync(r => r.Id == release.Id);
            reloaded.VariableSnapshot.Single(v => v.Name == "Db.Host").Value
                .Should().Be("old-db", "the snapshot did not drift");
        }

        // "Update Variables" → snapshot re-pulls and the timestamp bumps.
        await Task.Delay(10); // ensure the new UpdatedUtc differs from firstStamp
        var refreshed = await releaseSvc.UpdateVariablesAsync(release.Id);
        refreshed.VariableSnapshot.Single(v => v.Name == "Db.Host").Value.Should().Be("new-db");
        refreshed.VariableSnapshotUpdatedUtc.Should().BeAfter(firstStamp!.Value);
    }

    [Fact]
    public async Task UpdateVariablesAsync_throws_when_release_does_not_exist()
    {
        var svc = new ReleaseService(postgres);
        await svc.Invoking(s => s.UpdateVariablesAsync(Guid.NewGuid()))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ── VariableService.ResolveFromSnapshotAsync ──────────────────────────

    [Fact]
    public async Task ResolveFromSnapshotAsync_applies_scope_resolution_like_live_resolver()
    {
        var (project, env, target) = await SeedProjectWithEnvAsync(targetRoles: ["web"]);
        var vars = NewVarService(postgres);

        // Three variables sharing a name, with progressively more specific scopes.
        // Octopus scope-specificity: a target tag / role is MORE specific than an
        // environment, so for a deployment to (env, target, role=web) the
        // role-scoped value wins.
        await vars.CreateVariableAsync(project.Id, "Db.Host", "default-host", VariableType.Text,
            scope: new VariableScope());
        await vars.CreateVariableAsync(project.Id, "Db.Host", "env-host", VariableType.Text,
            scope: new VariableScope { EnvironmentId = env.Id });
        await vars.CreateVariableAsync(project.Id, "Db.Host", "role-host", VariableType.Text,
            scope: new VariableScope { Roles = ["web"] });
        await SeedSimpleProcessAsync(project.Id);

        var release = await new ReleaseService(postgres).CreateAsync(project.Id, "1.0.0");

        var resolved = await vars.ResolveFromSnapshotAsync(
            release.VariableSnapshot,
            environmentId: env.Id,
            targetId: target.Id,
            targetRoles: ["web"]);

        // role/tag (more specific) > env > unscoped; role wins.
        resolved["Db.Host"].Should().Be("role-host");
    }

    [Fact]
    public async Task ResolveFromSnapshotAsync_decrypts_sensitive_values_from_the_snapshot()
    {
        var (project, env, target) = await SeedProjectWithEnvAsync();
        var vars = NewVarService(postgres);
        await vars.CreateVariableAsync(project.Id, "ApiKey", "super-secret", VariableType.Sensitive,
            scope: new VariableScope());
        await SeedSimpleProcessAsync(project.Id);

        var release = await new ReleaseService(postgres).CreateAsync(project.Id, "1.0.0");

        // Snapshot stores ciphertext (NOT the plaintext).
        var snap = release.VariableSnapshot.Single(v => v.Name == "ApiKey");
        snap.Type.Should().Be(VariableType.Sensitive);
        snap.Value.Should().NotBe("super-secret", "value is the encrypted ciphertext, not plaintext");

        // Resolver decrypts using the same key, returns plaintext.
        var resolved = await vars.ResolveFromSnapshotAsync(
            release.VariableSnapshot,
            environmentId: env.Id, targetId: target.Id, targetRoles: []);
        resolved["ApiKey"].Should().Be("super-secret");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<(Project project, DeploymentEnvironment env, DeploymentTarget target)>
        SeedProjectWithEnvAsync(string[]? targetRoles = null)
    {
        await using var db = postgres.CreateContext();
        var slug = $"rvs-{Guid.NewGuid():N}";
        var project = new Project { Slug = slug, Name = slug };
        var env     = new DeploymentEnvironment { Slug = $"env-{Guid.NewGuid():N}", Name = "Production" };
        var target  = new DeploymentTarget
        {
            Name          = $"tgt-{Guid.NewGuid():N}",
            Roles         = [.. (targetRoles ?? [])],
            TransportMode = TransportMode.Reverse,
        };
        db.Projects.Add(project);
        db.Environments.Add(env);
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();
        return (project, env, target);
    }

    /// <summary>
    /// ReleaseService.CreateAsync requires at least one step to exist on the
    /// project's process. We add a single Manual step so the path runs end-to-end
    /// without needing a real package.
    /// </summary>
    private async Task SeedSimpleProcessAsync(Guid projectId)
    {
        await using var db = postgres.CreateContext();
        var process = new Process { OwnerKind = ProcessOwnerKind.Project, OwnerId = projectId };
        db.Processes.Add(process);
        await db.SaveChangesAsync();

        db.ProcessSteps.Add(new ProcessStep
        {
            ProcessId   = process.Id,
            Name        = "Approve",
            StepType    = "Octopus.Manual",
            PackageId   = "",
            TargetRoles = [],
            Config      = [],
            SortOrder   = 0,
        });
        await db.SaveChangesAsync();
    }
}
