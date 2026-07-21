using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Tags;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Behavioural regression coverage for the tier-1 cross-space IDOR remediation:
/// <c>DeploymentProcess</c>, <c>DeploymentStep</c>, <c>RunbookProcess</c>,
/// <c>RunbookStep</c>, <c>Variable</c> and <c>TenantTag</c> were promoted to
/// <see cref="Core.Domain.Common.ISpaceScoped"/> so the global query filter
/// covers them — tier 1 changed no service code, betting that the filter also
/// applies to the <c>FindAsync</c>-based by-id read/mutate methods.
/// <para>
/// These tests <em>drive the real services</em> (not just the DbContext) against
/// a row that lives in a second Space, from a default-Space context, and assert
/// the read returns null / the mutation is a no-op and the row is untouched.
/// They are the actual exploit vectors (cross-space DELETE/UPDATE by GUID with a
/// permission-only API gate) and prove <c>FindAsync</c> honours the filter on
/// this EF Core build — if it did not, the deletes below would succeed.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class CrossSpaceTier1ScopingTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // 32-byte dev key (base64) — mirrors VariableServiceTests.
    private const string DevMasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";

    // A Space distinct from WellKnown.DefaultSpaceId (the fixture's context
    // always resolves Default), and distinct from the tier-2 test's Space.
    private static readonly Guid OtherSpaceId = Guid.Parse("0000ffff-0000-0000-0000-0000cafecafe");

    // ── Variable (parent VariableSet is scoped; Variable itself was tier-1) ──

    [Fact]
    public async Task GetVariableAsync_does_not_return_other_space_variable()
    {
        var g = await SeedOtherSpaceGraphAsync();
        var svc = new VariableService(postgres, TestCrypto.Service(DevMasterKey), new AllowAllPermissionEvaluator());

        (await svc.GetVariableAsync(g.VariableId)).Should().BeNull(
            "a Variable in another Space must be invisible from the default Space");
    }

    [Fact]
    public async Task UpdateVariableAsync_cannot_modify_other_space_variable()
    {
        var g = await SeedOtherSpaceGraphAsync();
        var svc = new VariableService(postgres, TestCrypto.Service(DevMasterKey), new AllowAllPermissionEvaluator());

        var result = await svc.UpdateVariableAsync(
            g.VariableId, "hacked", "hacked", VariableType.Text, null, CallerAuthorization.System);

        result.Should().BeNull("the by-id update must not reach across Spaces");
        await AssertUnchangedAsync<Variable>(
            g.VariableId, v => v.Name.Should().Be("orig-var"));
    }

    [Fact]
    public async Task DeleteVariableAsync_cannot_delete_other_space_variable()
    {
        var g = await SeedOtherSpaceGraphAsync();
        var svc = new VariableService(postgres, TestCrypto.Service(DevMasterKey), new AllowAllPermissionEvaluator());

        (await svc.DeleteVariableAsync(g.VariableId, CallerAuthorization.System)).Should().BeFalse(
            "DeleteVariableAsync(FindAsync) must not delete another Space's variable");
        await AssertStillExistsAsync<Variable>(g.VariableId);
    }

    // ── Tag (extended tag sets — parent TagSet is scoped; Tag is Space-scoped) ──

    [Fact]
    public async Task UpdateTagAsync_cannot_modify_other_space_tag()
    {
        var g = await SeedOtherSpaceGraphAsync();
        var svc = new TagService(postgres);

        (await svc.UpdateTagAsync(g.TagId, "hacked", null, null)).Should().BeNull(
            "the tag update-by-id must not reach across Spaces");
        await AssertUnchangedAsync<Tag>(g.TagId, t => t.Name.Should().Be("t1-tag"));
    }

    [Fact]
    public async Task DeleteTagAsync_cannot_delete_other_space_tag()
    {
        var g = await SeedOtherSpaceGraphAsync();
        var svc = new TagService(postgres);

        (await svc.DeleteTagAsync(g.TagId)).Should().BeFalse(
            "the tag delete-by-id must not reach across Spaces");
        await AssertStillExistsAsync<Tag>(g.TagId);
    }

    // ── DeploymentStep / DeploymentProcess (tier-1) ──────────────────────────

    [Fact]
    public async Task GetProcessByIdAsync_does_not_return_other_space_process()
    {
        var g = await SeedOtherSpaceGraphAsync();
        var svc = new ProcessService(postgres, new AllowAllPermissionEvaluator());

        (await svc.GetProcessByIdAsync(g.DeploymentProcessId)).Should().BeNull();
    }

    [Fact]
    public async Task RemoveStepAsync_cannot_delete_other_space_deployment_step()
    {
        var g = await SeedOtherSpaceGraphAsync();
        var svc = new ProcessService(postgres, new AllowAllPermissionEvaluator());

        (await svc.RemoveStepAsync(g.DeploymentStepId, CallerAuthorization.System)).Should().BeFalse(
            "the process step delete-by-id must not reach across Spaces");
        await AssertStillExistsAsync<ProcessStep>(g.DeploymentStepId);
    }

    // ── RunbookStep (tier-1) — the DELETE /api/runbook-steps/{id} vector ─────

    [Fact]
    public async Task DeleteStepAsync_cannot_delete_other_space_runbook_step()
    {
        var g = await SeedOtherSpaceGraphAsync();
        var svc = new RunbookService(postgres,
            System.Threading.Channels.Channel.CreateUnbounded<KrakenDeploy.Server.Data.TenantWorkItem>(),
            TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            new AllowAllPermissionEvaluator());

        (await svc.DeleteStepAsync(g.RunbookStepId, CallerAuthorization.System)).Should().BeFalse(
            "DELETE /api/runbook-steps/{stepId} must not delete another Space's step");
        await AssertStillExistsAsync<ProcessStep>(g.RunbookStepId);
    }

    // ── Create-path hardening: GetOrCreate must not create for a foreign parent ─
    // The *Process row is created lazily by AddStep. Since the (now ISpaceScoped)
    // process query is Space-filtered, a foreign-Space parent id falls through to
    // the create branch — which must refuse rather than silently create a process
    // in the caller's Space pointing at a Project/Runbook they can't see.

    [Fact]
    public async Task ProcessAddStepAsync_throws_for_other_space_project_and_creates_no_process()
    {
        var projectId = await SeedOtherSpaceProjectAsync();
        var svc = new ProcessService(postgres, new AllowAllPermissionEvaluator());

        Func<Task> act = () => svc.AddStepAsync(
            projectId, "x", "Kraken.Script", "pkg", [], new Dictionary<string, string>(),
            CallerAuthorization.System);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "creating a process/step for a project in another Space must be refused");

        await using var raw = postgres.CreateContext();
        (await raw.Processes.IgnoreQueryFilters()
                .CountAsync(p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == projectId))
            .Should().Be(0, "no process may be created in the caller's Space for a foreign project");
    }

    [Fact]
    public async Task RunbookAddStepAsync_throws_for_other_space_runbook_and_creates_no_process()
    {
        var runbookId = await SeedOtherSpaceRunbookAsync();
        var svc = new RunbookService(postgres,
            System.Threading.Channels.Channel.CreateUnbounded<KrakenDeploy.Server.Data.TenantWorkItem>(),
            TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            new AllowAllPermissionEvaluator());

        Func<Task> act = () => svc.AddStepAsync(
            runbookId, "x", "Kraken.Script", "pkg", [], new Dictionary<string, string>(),
            CallerAuthorization.System);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "creating a process/step for a runbook in another Space must be refused");

        await using var raw = postgres.CreateContext();
        (await raw.Processes.IgnoreQueryFilters()
                .CountAsync(p => p.OwnerKind == ProcessOwnerKind.Runbook && p.OwnerId == runbookId))
            .Should().Be(0, "no process may be created in the caller's Space for a foreign runbook");
    }

    // ── Seeding + assertions ─────────────────────────────────────────────────

    private sealed record OtherSpaceGraph(
        Guid VariableId,
        Guid TagId,
        Guid DeploymentProcessId,
        Guid DeploymentStepId,
        Guid RunbookStepId);

    /// <summary>
    /// Seeds a full parent→child graph entirely in <see cref="OtherSpaceId"/>
    /// (explicit SpaceId — the interceptor preserves caller-set values), and
    /// returns the child ids the tests attack by GUID.
    /// </summary>
    private async Task<OtherSpaceGraph> SeedOtherSpaceGraphAsync()
    {
        await using var db = postgres.CreateContext();

        if (!await db.Spaces.IgnoreQueryFilters().AnyAsync(s => s.Id == OtherSpaceId))
        {
            db.Spaces.Add(new Space { Id = OtherSpaceId, Slug = "other-space-t1", Name = "Other Space T1" });
        }

        var project = new Project
        {
            SpaceId = OtherSpaceId, Name = "t1-proj", Slug = $"t1-proj-{Guid.NewGuid():N}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, OtherSpaceId),
        };
        var tenant = new Tenant
        {
            SpaceId = OtherSpaceId, Name = "t1-tenant", Slug = $"t1-tenant-{Guid.NewGuid():N}",
        };
        db.Projects.Add(project);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Variable in a project variable set.
        var set = new VariableSet { SpaceId = OtherSpaceId, ProjectId = project.Id };
        db.VariableSets.Add(set);
        await db.SaveChangesAsync();
        var variable = new Variable
        {
            SpaceId = OtherSpaceId, SetId = set.Id, Name = "orig-var", Value = "orig",
            Type = VariableType.Text, Scope = new VariableScope(),
        };
        db.Variables.Add(variable);

        // Tag in a Space-level tag set (extended tag sets model).
        var tagSet = new TagSet
        {
            SpaceId = OtherSpaceId, Name = $"t1-set-{Guid.NewGuid():N}",
            Scopes = [TaggableEntityKind.Tenant],
        };
        db.TagSets.Add(tagSet);
        await db.SaveChangesAsync();
        var tag = new Tag { SpaceId = OtherSpaceId, TagSetId = tagSet.Id, Name = "t1-tag" };
        db.Tags.Add(tag);

        // Deployment process + step.
        var dprocess = new Process { SpaceId = OtherSpaceId, OwnerKind = ProcessOwnerKind.Project, OwnerId = project.Id };
        db.Processes.Add(dprocess);
        await db.SaveChangesAsync();
        var dstep = new ProcessStep
        {
            SpaceId = OtherSpaceId, ProcessId = dprocess.Id, Name = "t1-step",
            StepType = "Kraken.Script", PackageId = "pkg",
        };
        db.ProcessSteps.Add(dstep);

        // Runbook process + step.
        var runbook = new Runbook { SpaceId = OtherSpaceId, ProjectId = project.Id, Name = "t1-runbook" };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();
        var rprocess = new Process { SpaceId = OtherSpaceId, OwnerKind = ProcessOwnerKind.Runbook, OwnerId = runbook.Id };
        db.Processes.Add(rprocess);
        await db.SaveChangesAsync();
        var rstep = new ProcessStep
        {
            SpaceId = OtherSpaceId, ProcessId = rprocess.Id, Name = "t1-rstep", StepType = "Kraken.Script",
        };
        db.ProcessSteps.Add(rstep);

        await db.SaveChangesAsync();

        return new OtherSpaceGraph(variable.Id, tag.Id, dprocess.Id, dstep.Id, rstep.Id);
    }

    /// <summary>A bare Project in <see cref="OtherSpaceId"/> (no process yet).</summary>
    private async Task<Guid> SeedOtherSpaceProjectAsync()
    {
        await using var db = postgres.CreateContext();
        await EnsureOtherSpaceAsync(db);
        var project = new Project
        {
            SpaceId = OtherSpaceId, Name = "t1-proj2", Slug = $"t1-proj2-{Guid.NewGuid():N}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, OtherSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>A Project + Runbook in <see cref="OtherSpaceId"/> (no process yet).</summary>
    private async Task<Guid> SeedOtherSpaceRunbookAsync()
    {
        await using var db = postgres.CreateContext();
        await EnsureOtherSpaceAsync(db);
        var project = new Project
        {
            SpaceId = OtherSpaceId, Name = "t1-proj3", Slug = $"t1-proj3-{Guid.NewGuid():N}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, OtherSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var runbook = new Runbook { SpaceId = OtherSpaceId, ProjectId = project.Id, Name = "t1-runbook2" };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();
        return runbook.Id;
    }

    private static async Task EnsureOtherSpaceAsync(KrakenDbContext db)
    {
        if (!await db.Spaces.IgnoreQueryFilters().AnyAsync(s => s.Id == OtherSpaceId))
        {
            db.Spaces.Add(new Space { Id = OtherSpaceId, Slug = "other-space-t1", Name = "Other Space T1" });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Asserts the row still exists cross-Space (mutation was a no-op).</summary>
    private async Task AssertStillExistsAsync<T>(Guid id) where T : class
    {
        await using var raw = postgres.CreateContext();
        var exists = await raw.Set<T>().IgnoreQueryFilters()
            .AnyAsync(e => EF.Property<Guid>(e, "Id") == id);
        exists.Should().BeTrue(
            $"{typeof(T).Name} {id} must remain after a cross-Space mutation attempt");
    }

    /// <summary>Loads the row cross-Space and runs an extra assertion on it.</summary>
    private async Task AssertUnchangedAsync<T>(Guid id, Action<T> assert) where T : class
    {
        await using var raw = postgres.CreateContext();
        var row = await raw.Set<T>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id);
        row.Should().NotBeNull();
        assert(row!);
    }
}
