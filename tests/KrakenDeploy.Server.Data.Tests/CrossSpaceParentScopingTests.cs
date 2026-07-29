using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Cross-space (Space isolation) regression coverage for the TOP-LEVEL
/// aggregates that were already <see cref="Core.Domain.Common.ISpaceScoped"/>
/// before the tier-1/tier-2 child remediation: <c>Project</c>, <c>Tenant</c>,
/// <c>Channel</c>, <c>DeploymentEnvironment</c>, <c>StepTemplate</c>,
/// <c>DeploymentTarget</c> and <c>Package</c>.
/// <para>
/// These services resolve by id with <c>FindAsync</c>. On EF Core 10 +
/// Npgsql, <c>FindAsync</c> applies the global query filter for an UNtracked
/// entity (the services use a fresh <c>DbContext</c> per call), so a by-id
/// read/mutate from one Space cannot reach a row in another. These tests pin
/// that behaviour from the <em>service</em> layer (the real IDOR vector: a
/// cross-Space GET / DELETE by GUID behind a permission-only API gate) so a
/// future change — e.g. an accidental <c>IgnoreQueryFilters()</c> — fails CI
/// rather than silently re-opening the leak. Companion to
/// <see cref="CrossSpaceTier1ScopingTests"/> (which covers the child entities).
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class CrossSpaceParentScopingTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // Distinct from the fixture's Default Space and from the tier-1 test's Space.
    private static readonly Guid OtherSpaceId = Guid.Parse("0000ffff-0000-0000-0000-0000deadbeef");

    // ── Project ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectService_does_not_read_or_delete_other_space()
    {
        var g = await SeedAsync();
        var svc = new ProjectService(postgres);

        (await svc.GetAsync(g.ProjectId)).Should().BeNull();
        (await svc.DeleteAsync(g.ProjectId)).Should().BeFalse();
        await AssertStillExistsAsync<Project>(g.ProjectId);
    }

    // ── Tenant ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantService_does_not_read_or_delete_other_space()
    {
        var g = await SeedAsync();
        var svc = new TenantService(postgres, new AllowAllPermissionEvaluator());

        (await svc.GetAsync(g.TenantId)).Should().BeNull();
        (await svc.DeleteAsync(g.TenantId, CallerAuthorization.System)).Should().BeFalse();
        await AssertStillExistsAsync<Tenant>(g.TenantId);
    }

    // ── Channel ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChannelService_does_not_read_or_delete_other_space()
    {
        var g = await SeedAsync();
        var svc = new ChannelService(postgres);

        (await svc.GetAsync(g.ChannelId)).Should().BeNull();
        (await svc.DeleteAsync(g.ChannelId)).Should().BeFalse();
        await AssertStillExistsAsync<Channel>(g.ChannelId);
    }

    // ── Environment (Delete-only service surface) ────────────────────────────

    [Fact]
    public async Task EnvironmentService_cannot_delete_other_space()
    {
        var g = await SeedAsync();
        var svc = new EnvironmentService(postgres);

        (await svc.DeleteAsync(g.EnvironmentId)).Should().BeFalse();
        await AssertStillExistsAsync<DeploymentEnvironment>(g.EnvironmentId);
    }

    // ── StepTemplate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StepTemplateService_does_not_read_or_delete_other_space()
    {
        var g = await SeedAsync();
        var svc = new StepTemplateService(postgres, new AllowAllPermissionEvaluator());

        (await svc.GetAsync(g.StepTemplateId)).Should().BeNull();
        (await svc.DeleteAsync(g.StepTemplateId)).Should().BeFalse();
        await AssertStillExistsAsync<StepTemplate>(g.StepTemplateId);
    }

    // ── Target ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TargetService_does_not_read_other_space()
    {
        var g = await SeedAsync();
        var svc = new TargetService(postgres, new AllowAllPermissionEvaluator());

        (await svc.GetAsync(g.TargetId)).Should().BeNull();
    }

    // ── Package (the GET /api/packages/{id}/download IDOR vector) ────────────

    [Fact]
    public async Task PackageService_does_not_read_open_or_delete_other_space()
    {
        var g = await SeedAsync();
        var svc = new PackageService(
            postgres,
            new LocalPackageStore(Path.GetTempPath(), new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext()),
            TimeProvider.System);

        (await svc.GetAsync(g.PackageId)).Should().BeNull(
            "a package in another Space must be invisible by id");

        // OpenStreamAsync throws 'not found' because FindAsync is filtered to the
        // caller's Space — i.e. the download endpoint cannot stream another
        // Space's package bytes.
        await FluentActions.Awaiting(() => svc.OpenStreamAsync(g.PackageId))
            .Should().ThrowAsync<InvalidOperationException>();

        (await svc.DeleteAsync(g.PackageId)).Should().BeFalse();
        await AssertStillExistsAsync<Package>(g.PackageId);
    }

    // ── Seeding + assertions ─────────────────────────────────────────────────

    private sealed record ParentGraph(
        Guid ProjectId, Guid TenantId, Guid ChannelId, Guid EnvironmentId,
        Guid StepTemplateId, Guid TargetId, Guid PackageId);

    /// <summary>Seeds one of every top-level aggregate entirely in
    /// <see cref="OtherSpaceId"/> with unique slugs/names (the class shares one
    /// fixture, so fixed names would collide on the unique indexes).</summary>
    private async Task<ParentGraph> SeedAsync()
    {
        await using var db = postgres.CreateContext();

        if (!await db.Spaces.IgnoreQueryFilters().AnyAsync(s => s.Id == OtherSpaceId))
        {
            db.Spaces.Add(new Space
            {
                Id = OtherSpaceId, Slug = "other-space-parent", Name = "Other Space Parent",
            });
        }

        var u = Guid.NewGuid().ToString("N");
        var project = new Project
        {
            SpaceId = OtherSpaceId, Name = $"p-{u}", Slug = $"p-{u}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, OtherSpaceId),
        };
        var tenant = new Tenant { SpaceId = OtherSpaceId, Name = $"t-{u}", Slug = $"t-{u}" };
        var env = new DeploymentEnvironment
        {
            SpaceId = OtherSpaceId, Name = $"e-{u}", Slug = $"e-{u}", SortOrder = 1,
        };
        var template = new StepTemplate
        {
            SpaceId = OtherSpaceId, Name = $"st-{u}", ActionType = "Kraken.Script",
        };
        var target = new DeploymentTarget
        {
            SpaceId = OtherSpaceId, Name = $"tgt-{u}", Roles = ["web"],
            TransportMode = TransportMode.Reverse, Status = TargetStatus.Online,
        };
        var package = new Package
        {
            SpaceId = OtherSpaceId, PackageId = $"pkg-{u}", Version = "1.0.0",
            FileName = "pkg.zip", StoredPath = $"{u}/pkg.zip", SizeBytes = 1,
        };

        db.Projects.Add(project);
        db.Tenants.Add(tenant);
        db.Environments.Add(env);
        db.StepTemplates.Add(template);
        db.DeploymentTargets.Add(target);
        db.Packages.Add(package);
        await db.SaveChangesAsync();

        var channel = new Channel { SpaceId = OtherSpaceId, ProjectId = project.Id, Name = $"ch-{u}" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        return new ParentGraph(
            project.Id, tenant.Id, channel.Id, env.Id, template.Id, target.Id, package.Id);
    }

    private async Task AssertStillExistsAsync<T>(Guid id) where T : class
    {
        await using var raw = postgres.CreateContext();
        var exists = await raw.Set<T>().IgnoreQueryFilters()
            .AnyAsync(e => EF.Property<Guid>(e, "Id") == id);
        exists.Should().BeTrue(
            $"{typeof(T).Name} {id} must survive a cross-Space mutation attempt");
    }
}
