using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration coverage for the composite-FK Space-hardening wave. Proves the DB
/// now refuses a child/join row whose Space differs from its parent's — the
/// single-column <c>space_id → spaces</c> FK could not (both Spaces exist, so it
/// was satisfied); only the composite <c>(space_id, parent_id) → parent(space_id,
/// id)</c> FK catches it. Also proves the Postgres 15+ column-list
/// <c>ON DELETE SET NULL (col)</c> only nulls the reference, not the NOT-NULL
/// <c>space_id</c>.
/// <para>
/// The interceptor preserves a caller-set <c>SpaceId</c>, so each test deliberately
/// stamps the mismatch at INSERT time (never by re-parenting an existing row, which
/// the interceptor blocks).
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class CrossSpaceFkConstraintTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // ── child → parent composite FK ───────────────────────────────────────────

    [Fact]
    public async Task Variable_in_space_B_referencing_set_in_space_A_is_rejected()
    {
        var spaceA = await NewSpaceAsync();
        var spaceB = await NewSpaceAsync();
        var setId = await SeedLibrarySetAsync(spaceA);

        await using var db = postgres.CreateContext();
        db.Variables.Add(new Variable
        {
            SpaceId = spaceB, SetId = setId, Name = "v", Value = "x", Type = VariableType.Text,
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the composite FK (space_id, set_id) → variable_sets(space_id, id) has no " +
            "(spaceB, setId) row — the set lives in spaceA");
    }

    [Fact]
    public async Task Variable_in_the_same_space_as_its_set_is_accepted()
    {
        // Negative control: proves the FK rejects only the Space mismatch, not the shape.
        var spaceA = await NewSpaceAsync();
        var setId = await SeedLibrarySetAsync(spaceA);

        await using var db = postgres.CreateContext();
        db.Variables.Add(new Variable
        {
            SpaceId = spaceA, SetId = setId, Name = "v", Value = "x", Type = VariableType.Text,
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Channel_in_space_B_referencing_project_in_space_A_is_rejected()
    {
        var spaceA = await NewSpaceAsync();
        var spaceB = await NewSpaceAsync();
        var projectId = await SeedProjectAsync(spaceA);

        await using var db = postgres.CreateContext();
        db.Channels.Add(new Channel { SpaceId = spaceB, ProjectId = projectId, Name = "X" });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the composite FK (space_id, project_id) → projects(space_id, id) rejects a " +
            "channel whose Space differs from its project's");
    }

    [Fact]
    public async Task Task_artifact_in_space_B_referencing_task_in_space_A_is_rejected()
    {
        var spaceA = await NewSpaceAsync();
        var spaceB = await NewSpaceAsync();
        var taskId = await SeedDeploymentAsync(spaceA);

        await using var db = postgres.CreateContext();
        db.TaskArtifacts.Add(new TaskArtifact
        {
            SpaceId = spaceB, TaskId = taskId, StepName = "step", FileName = "a.log",
            ContentType = "text/plain", SizeBytes = 1, StoredPath = "/x", CollectedUtc = DateTimeOffset.UtcNow,
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the composite FK (space_id, task_id) → server_tasks(space_id, id) rejects a " +
            "task child whose Space differs from its task's");
    }

    // ── explicit join that gained space_id ─────────────────────────────────────

    [Fact]
    public async Task Project_variable_set_link_in_space_B_is_rejected()
    {
        var spaceA = await NewSpaceAsync();
        var spaceB = await NewSpaceAsync();
        var projectId = await SeedProjectAsync(spaceA);
        var setId = await SeedLibrarySetAsync(spaceA);

        await using var db = postgres.CreateContext();
        db.ProjectVariableSetLinks.Add(new ProjectVariableSetLink
        {
            SpaceId = spaceB, ProjectId = projectId, VariableSetId = setId, SortOrder = 0,
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "both composite FKs point at spaceB, but the project and set live in spaceA");
    }

    // ── implicit-m2m-converted join: BOTH sides enforced ───────────────────────

    [Fact]
    public async Task Target_tenant_pairing_across_spaces_is_rejected_on_the_tenant_side()
    {
        // target in A, tenant in B; stamp the join with A (the target's Space). The
        // (space_id, target_id) FK is satisfied, but (space_id, tenant_id) is not —
        // proving BOTH composite FKs are enforced, not just one.
        var spaceA = await NewSpaceAsync();
        var spaceB = await NewSpaceAsync();
        var targetId = await SeedTargetAsync(spaceA);
        var tenantId = await SeedTenantAsync(spaceB);

        await using var db = postgres.CreateContext();
        db.TargetTenants.Add(new TargetTenant { SpaceId = spaceA, TargetId = targetId, TenantId = tenantId });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the tenant lives in spaceB, so (spaceA, tenantId) → tenants(space_id, id) has no match");
    }

    [Fact]
    public async Task Target_tenant_pairing_within_one_space_is_accepted()
    {
        var spaceA = await NewSpaceAsync();
        var targetId = await SeedTargetAsync(spaceA);
        var tenantId = await SeedTenantAsync(spaceA);

        await using var db = postgres.CreateContext();
        db.TargetTenants.Add(new TargetTenant { SpaceId = spaceA, TargetId = targetId, TenantId = tenantId });

        var act = () => db.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    // ── column-list ON DELETE SET NULL (col) ───────────────────────────────────

    [Fact]
    public async Task Deleting_a_channel_nulls_the_release_reference_and_keeps_space_id()
    {
        // The composite (space_id, channel_id) → channels FK uses the PG15+
        // column-list SET NULL so only channel_id is nulled. A plain SET NULL would
        // try to null the NOT-NULL space_id and throw — this test would then fail
        // on the delete.
        var spaceA = await NewSpaceAsync();
        var projectId = await SeedProjectAsync(spaceA);
        var channelId = await SeedChannelAsync(spaceA, projectId);

        Guid releaseId;
        await using (var db = postgres.CreateContext())
        {
            var release = new Release
            {
                SpaceId = spaceA, ProjectId = projectId, ChannelId = channelId, Version = "1.0.0",
                ProcessSnapshot = [], VariableSnapshot = [], VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
            };
            db.Releases.Add(release);
            await db.SaveChangesAsync();
            releaseId = release.Id;
        }

        await using (var db = postgres.CreateContext())
        {
            var channel = await db.Channels.IgnoreQueryFilters().FirstAsync(c => c.Id == channelId);
            db.Channels.Remove(channel);
            var act = () => db.SaveChangesAsync();
            await act.Should().NotThrowAsync(
                "column-list SET NULL nulls only channel_id, never the NOT-NULL space_id");
        }

        await using (var db = postgres.CreateContext())
        {
            var release = await db.Releases.IgnoreQueryFilters().FirstAsync(r => r.Id == releaseId);
            release.ChannelId.Should().BeNull("the channel was deleted, so its reference is nulled");
            release.SpaceId.Should().Be(spaceA, "space_id must survive the SET NULL untouched");
        }
    }

    // ── seed helpers ────────────────────────────────────────────────────────────

    private async Task<Guid> NewSpaceAsync()
    {
        var id = Guid.NewGuid();
        await using var db = postgres.CreateContext();
        db.Spaces.Add(new Space { Id = id, Slug = $"xsf-{id:N}", Name = "Cross-Space FK" });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedProjectAsync(Guid spaceId)
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            SpaceId = spaceId,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
            Name = "Project", Slug = $"proj-{Guid.NewGuid():N}",
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<Guid> SeedLibrarySetAsync(Guid spaceId)
    {
        await using var db = postgres.CreateContext();
        var set = new VariableSet { SpaceId = spaceId, Kind = VariableSetKind.Library, Name = $"lib-{Guid.NewGuid():N}" };
        db.VariableSets.Add(set);
        await db.SaveChangesAsync();
        return set.Id;
    }

    private async Task<Guid> SeedChannelAsync(Guid spaceId, Guid projectId)
    {
        await using var db = postgres.CreateContext();
        var channel = new Channel { SpaceId = spaceId, ProjectId = projectId, Name = $"ch-{Guid.NewGuid():N}", IsDefault = false };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private async Task<Guid> SeedTargetAsync(Guid spaceId)
    {
        await using var db = postgres.CreateContext();
        var target = new DeploymentTarget
        {
            SpaceId = spaceId, Name = $"tgt-{Guid.NewGuid():N}", Roles = ["web"],
            TransportMode = TransportMode.Reverse, Status = TargetStatus.Unknown,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();
        return target.Id;
    }

    private async Task<Guid> SeedTenantAsync(Guid spaceId)
    {
        await using var db = postgres.CreateContext();
        var tenant = new Tenant { SpaceId = spaceId, Slug = $"tn-{Guid.NewGuid():N}", Name = "Tenant" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> SeedDeploymentAsync(Guid spaceId)
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            SpaceId = spaceId,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
            Name = "Dep Project", Slug = $"dep-{Guid.NewGuid():N}",
        };
        var env = new DeploymentEnvironment { SpaceId = spaceId, Name = "env", Slug = $"env-{Guid.NewGuid():N}", SortOrder = 1 };
        db.Projects.Add(project);
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId = spaceId, ProjectId = project.Id, Version = "1.0.0",
            ProcessSnapshot = [], VariableSnapshot = [], VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        var deployment = new Deployment
        {
            SpaceId = spaceId, ProjectId = project.Id, ReleaseId = release.Id, EnvironmentId = env.Id,
            Status = DeploymentStatus.Succeeded,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();
        return deployment.Id;
    }
}
