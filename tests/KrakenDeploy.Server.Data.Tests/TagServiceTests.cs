using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Tags;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration coverage for the extended tag sets model
/// (docs/extended-tag-sets-plan.md): <see cref="TagService"/> validation
/// (scope membership, cardinality, type-change rules, confirm-then-cascade
/// scope removal), the DB-level partial unique index as the last line
/// against cardinality violations, the polymorphic-cleanup interceptor, and
/// the canonical-string query feeding <c>Octopus.Deployment.Tenant.Tags</c>.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class TagServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private TagService NewSvc() => new(postgres);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..30];

    // ── Select-type application semantics ───────────────────────────────────

    [Fact]
    public async Task MultiSelect_apply_is_replace_per_set()
    {
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("multi"), null, TagSetType.MultiSelect, [TaggableEntityKind.Tenant], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        var b = await svc.CreateTagAsync(set.Id, "b", null, null);
        var entityId = Guid.NewGuid();

        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Tenant, entityId, [a.Id, b.Id]);
        (await svc.GetForEntityAsync(TaggableEntityKind.Tenant, entityId))
            .Should().HaveCount(2);

        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Tenant, entityId, [b.Id]);
        var remaining = await svc.GetForEntityAsync(TaggableEntityKind.Tenant, entityId);
        remaining.Should().ContainSingle().Which.TagId.Should().Be(b.Id);
    }

    [Fact]
    public async Task SingleSelect_rejects_more_than_one_tag()
    {
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("single"), null, TagSetType.SingleSelect, [TaggableEntityKind.Tenant], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        var b = await svc.CreateTagAsync(set.Id, "b", null, null);

        var act = () => svc.SetAppliedTagsAsync(
            set.Id, TaggableEntityKind.Tenant, Guid.NewGuid(), [a.Id, b.Id]);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*single-select*");
    }

    [Fact]
    public async Task Partial_unique_index_is_the_last_line_against_single_select_duplicates()
    {
        // Bypass the service entirely — two SingleSelect rows for the same
        // (set, entity) must be refused by the DB itself.
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("idx"), null, TagSetType.SingleSelect, [TaggableEntityKind.Tenant], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        var b = await svc.CreateTagAsync(set.Id, "b", null, null);
        var entityId = Guid.NewGuid();

        await using var db = postgres.CreateContext();
        db.TagApplications.Add(new TagApplication
        {
            SpaceId = WellKnown.DefaultSpaceId, TagSetId = set.Id, TagId = a.Id,
            EntityKind = TaggableEntityKind.Tenant, EntityId = entityId,
            SetType = TagSetType.SingleSelect,
        });
        await db.SaveChangesAsync();

        db.TagApplications.Add(new TagApplication
        {
            SpaceId = WellKnown.DefaultSpaceId, TagSetId = set.Id, TagId = b.Id,
            EntityKind = TaggableEntityKind.Tenant, EntityId = entityId,
            SetType = TagSetType.SingleSelect,
        });
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the partial unique index enforces cardinality even for writers that skip the service");
    }

    [Fact]
    public async Task Applying_to_a_kind_outside_the_sets_scopes_is_rejected()
    {
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("scoped"), null, TagSetType.MultiSelect, [TaggableEntityKind.Tenant], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);

        var act = () => svc.SetAppliedTagsAsync(
            set.Id, TaggableEntityKind.Project, Guid.NewGuid(), [a.Id]);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not scoped to Project*");
    }

    // ── FreeText semantics ──────────────────────────────────────────────────

    [Fact]
    public async Task FreeText_sets_one_value_per_entity_and_clears_on_null()
    {
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("free"), null, TagSetType.FreeText, [TaggableEntityKind.Tenant], 0);
        var entityId = Guid.NewGuid();

        await svc.SetFreeTextValueAsync(set.Id, TaggableEntityKind.Tenant, entityId, "HR-South");
        (await svc.GetForEntityAsync(TaggableEntityKind.Tenant, entityId))
            .Should().ContainSingle().Which.FreeTextValue.Should().Be("HR-South");

        // Overwrite keeps one row.
        await svc.SetFreeTextValueAsync(set.Id, TaggableEntityKind.Tenant, entityId, "HR-North");
        (await svc.GetForEntityAsync(TaggableEntityKind.Tenant, entityId))
            .Should().ContainSingle().Which.FreeTextValue.Should().Be("HR-North");

        // Null clears.
        await svc.SetFreeTextValueAsync(set.Id, TaggableEntityKind.Tenant, entityId, null);
        (await svc.GetForEntityAsync(TaggableEntityKind.Tenant, entityId)).Should().BeEmpty();
    }

    [Fact]
    public async Task FreeText_sets_refuse_predefined_tags_and_select_application()
    {
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("free2"), null, TagSetType.FreeText, [TaggableEntityKind.Tenant], 0);

        var addTag = () => svc.CreateTagAsync(set.Id, "nope", null, null);
        await addTag.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*free-text*");

        var apply = () => svc.SetAppliedTagsAsync(
            set.Id, TaggableEntityKind.Tenant, Guid.NewGuid(), []);
        await apply.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*free-text*");
    }

    // ── Set mutation rules ──────────────────────────────────────────────────

    [Fact]
    public async Task Scope_removal_with_applications_requires_force_then_cascades()
    {
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("cascade"), null, TagSetType.MultiSelect,
            [TaggableEntityKind.Tenant, TaggableEntityKind.Project], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        var tenantEntity  = Guid.NewGuid();
        var projectEntity = Guid.NewGuid();
        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Tenant, tenantEntity, [a.Id]);
        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Project, projectEntity, [a.Id]);

        // Without force → refused with the affected count.
        var act = () => svc.UpdateSetAsync(
            set.Id, set.Name, null, TagSetType.MultiSelect, [TaggableEntityKind.Tenant], 0);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Confirm to proceed*");

        // With force → project applications cascade, tenant's survive.
        await svc.UpdateSetAsync(
            set.Id, set.Name, null, TagSetType.MultiSelect, [TaggableEntityKind.Tenant], 0,
            force: true);
        (await svc.GetForEntityAsync(TaggableEntityKind.Project, projectEntity)).Should().BeEmpty();
        (await svc.GetForEntityAsync(TaggableEntityKind.Tenant, tenantEntity)).Should().ContainSingle();
    }

    [Fact]
    public async Task Type_change_is_blocked_until_compliant_then_restamps_rows()
    {
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("retype"), null, TagSetType.MultiSelect, [TaggableEntityKind.Tenant], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        var b = await svc.CreateTagAsync(set.Id, "b", null, null);
        var entityId = Guid.NewGuid();
        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Tenant, entityId, [a.Id, b.Id]);

        // Two tags on one entity → Multi→Single refused.
        var act = () => svc.UpdateSetAsync(
            set.Id, set.Name, null, TagSetType.SingleSelect, [TaggableEntityKind.Tenant], 0);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*more than one tag*");

        // Reduce to one → allowed, and the denormalized SetType is restamped.
        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Tenant, entityId, [a.Id]);
        await svc.UpdateSetAsync(
            set.Id, set.Name, null, TagSetType.SingleSelect, [TaggableEntityKind.Tenant], 0);

        await using var db = postgres.CreateContext();
        var row = await db.TagApplications.SingleAsync(x => x.TagSetId == set.Id);
        row.SetType.Should().Be(TagSetType.SingleSelect,
            "the partial unique index reads the denormalized SetType");
    }

    [Fact]
    public async Task Select_to_FreeText_conversion_is_blocked_while_any_applications_exist()
    {
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("convert"), null, TagSetType.MultiSelect, [TaggableEntityKind.Tenant], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Tenant, Guid.NewGuid(), [a.Id]);

        var act = () => svc.UpdateSetAsync(
            set.Id, set.Name, null, TagSetType.FreeText, [TaggableEntityKind.Tenant], 0);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*FreeText*");
    }

    // ── Polymorphic cleanup interceptor ─────────────────────────────────────

    [Fact]
    public async Task Deleting_a_tagged_entity_removes_its_applications_in_the_same_save()
    {
        var svc = NewSvc();
        var tenantSvc = new TenantService(postgres);
        var tenant = await tenantSvc.CreateAsync(
            Unique("tagged-tenant"), Unique("tagged-tenant"), null);

        var set = await svc.CreateSetAsync(
            Unique("cleanup"), null, TagSetType.MultiSelect, [TaggableEntityKind.Tenant], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Tenant, tenant.Id, [a.Id]);

        (await tenantSvc.DeleteAsync(tenant.Id)).Should().BeTrue();

        await using var db = postgres.CreateContext();
        (await db.TagApplications.IgnoreQueryFilters()
                .AnyAsync(x => x.EntityId == tenant.Id))
            .Should().BeFalse("TagApplicationCleanupInterceptor removes the orphans in the same save");
    }

    [Fact]
    public async Task Deleting_a_project_cleans_its_runbooks_tag_applications_via_db_cascade()
    {
        // Regression: the cleanup interceptor only sees tracker-Deleted entities;
        // a project's runbooks are DB-cascade-deleted (never tracked), so their
        // polymorphic tag_applications must be resolved + removed explicitly.
        var svc = NewSvc();
        Guid projectId, runbookId;
        await using (var db = postgres.CreateContext())
        {
            var project = new KrakenDeploy.Server.Core.Domain.Projects.Project
            {
                SpaceId = WellKnown.DefaultSpaceId,
                ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
                Name = Unique("rb-proj"), Slug = Unique("rb-proj"),
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var runbook = new KrakenDeploy.Server.Core.Domain.Runbooks.Runbook
            {
                SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, Name = "rb",
            };
            db.Runbooks.Add(runbook);
            await db.SaveChangesAsync();
            projectId = project.Id;
            runbookId = runbook.Id;
        }

        var set = await svc.CreateSetAsync(
            Unique("rb-set"), null, TagSetType.MultiSelect, [TaggableEntityKind.Runbook], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Runbook, runbookId, [a.Id]);

        var projectSvc = new ProjectService(postgres);
        (await projectSvc.DeleteAsync(projectId)).Should().BeTrue();

        await using var check = postgres.CreateContext();
        (await check.TagApplications.IgnoreQueryFilters()
                .AnyAsync(x => x.EntityId == runbookId))
            .Should().BeFalse(
                "the runbook cascade-deleted with its project must not leave orphaned tag applications");
    }

    [Fact]
    public async Task Combined_force_scope_removal_and_type_change_in_one_call_is_not_falsely_rejected()
    {
        // Regression: the type-change validation queried the DB where the
        // scope-removal cascade's rows still existed, over-counting and
        // falsely rejecting a compliant post-save state.
        var svc = NewSvc();
        var set = await svc.CreateSetAsync(
            Unique("combo"), null, TagSetType.MultiSelect,
            [TaggableEntityKind.Tenant, TaggableEntityKind.Project], 0);
        var a = await svc.CreateTagAsync(set.Id, "a", null, null);
        var b = await svc.CreateTagAsync(set.Id, "b", null, null);
        // A project entity carries TWO tags (would violate SingleSelect) — but
        // the Project scope is being removed in the same call, so those rows go.
        await svc.SetAppliedTagsAsync(set.Id, TaggableEntityKind.Project, Guid.NewGuid(), [a.Id, b.Id]);

        var act = () => svc.UpdateSetAsync(
            set.Id, set.Name, null, TagSetType.SingleSelect, [TaggableEntityKind.Tenant], 0,
            force: true);
        await act.Should().NotThrowAsync(
            "the project's soon-to-be-cascaded applications must not block the Multi→Single change");

        var reloaded = await svc.GetSetAsync(set.Id);
        reloaded!.Type.Should().Be(TagSetType.SingleSelect);
        reloaded.Scopes.Should().Equal(TaggableEntityKind.Tenant);
    }

    // ── Canonical strings (Octopus.Deployment.Tenant.Tags) ──────────────────

    [Fact]
    public async Task Tenant_canonicals_cover_select_tags_and_free_text_values()
    {
        var svc = NewSvc();
        var tenantId = Guid.NewGuid();

        var hosting = await svc.CreateSetAsync(
            Unique("canon-host"), null, TagSetType.MultiSelect, [TaggableEntityKind.Tenant], 0);
        var dbk = await svc.CreateTagAsync(hosting.Id, "DBK", null, null);
        await svc.SetAppliedTagsAsync(hosting.Id, TaggableEntityKind.Tenant, tenantId, [dbk.Id]);

        var region = await svc.CreateSetAsync(
            Unique("canon-region"), null, TagSetType.FreeText, [TaggableEntityKind.Tenant], 1);
        await svc.SetFreeTextValueAsync(region.Id, TaggableEntityKind.Tenant, tenantId, "HR-South");

        await using var db = postgres.CreateContext();
        var canonicals = await TagService.GetTenantTagCanonicalsAsync(db, tenantId);

        canonicals.Should().Equal($"{hosting.Name}/DBK", $"{region.Name}/HR-South");
    }
}
