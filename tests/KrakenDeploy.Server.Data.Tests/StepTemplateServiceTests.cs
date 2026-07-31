using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// SC8: first direct coverage for <see cref="StepTemplateService"/> — the
/// preset store's CRUD and the Octopus-Library JSON import/upsert that both
/// catalog feeds and the manual import paths route through. (Cross-Space
/// scoping is covered separately in CrossSpaceParentScopingTests.)
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepTemplateServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private StepTemplateService NewSvc() =>
        new(postgres, new AllowAllPermissionEvaluator());

    [Fact]
    public async Task Create_roundtrips_every_field()
    {
        var svc  = NewSvc();
        var name = UniqueName();

        var created = await svc.CreateAsync(
            name, "Kraken.Script", "does things",
            properties: new() { ["Octopus.Action.Script.ScriptBody"] = "echo hi" },
            parameters:
            [
                new StepTemplateParameter
                {
                    Name = "P1", Label = "Param 1", ControlType = "SingleLineText",
                },
            ],
            category: "script", author: "tester");

        var fetched = await svc.GetAsync(created.Id);
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be(name);
        fetched.ActionType.Should().Be("Kraken.Script");
        fetched.Properties.Should().ContainKey("Octopus.Action.Script.ScriptBody");
        fetched.Parameters.Should().ContainSingle().Which.Name.Should().Be("P1");
        fetched.Category.Should().Be("script");
        fetched.Author.Should().Be("tester");
        fetched.Source.Should().Be(StepTemplateSource.UserAuthored);
    }

    [Fact]
    public async Task Update_replaces_fields_and_bumps_version()
    {
        var svc     = NewSvc();
        var created = await svc.CreateAsync(
            UniqueName(), "Kraken.Script", null, null, null);
        var versionBefore = created.Version;

        var updated = await svc.UpdateAsync(
            created.Id, created.Name + " v2", "new description",
            properties: new() { ["k"] = "v" },
            parameters: [],
            caller: CallerAuthorization.System,
            category: "other");

        updated.Should().NotBeNull();
        updated!.Name.Should().EndWith(" v2");
        updated.Description.Should().Be("new description");
        updated.Category.Should().Be("other");
        updated.Version.Should().Be(versionBefore + 1,
            "consumers rely on the version bump to notice preset drift");
    }

    [Fact]
    public async Task Delete_removes_the_row_and_reports_missing_honestly()
    {
        var svc     = NewSvc();
        var created = await svc.CreateAsync(UniqueName(), "Kraken.Script", null, null, null);

        (await svc.DeleteAsync(created.Id)).Should().BeTrue();
        (await svc.GetAsync(created.Id)).Should().BeNull();
        (await svc.DeleteAsync(created.Id)).Should().BeFalse("second delete finds nothing");
    }

    [Fact]
    public async Task ImportFromJson_upserts_by_community_template_id()
    {
        var svc = NewSvc();
        var communityId = Guid.NewGuid().ToString();

        var v1 = await svc.ImportFromJsonAsync(
            TemplateJson(communityId, name: "Original name", version: 3),
            importSource: "test", source: StepTemplateSource.CommunityLibrary);
        v1.Source.Should().Be(StepTemplateSource.CommunityLibrary);
        v1.Name.Should().Be("Original name");

        var v2 = await svc.ImportFromJsonAsync(
            TemplateJson(communityId, name: "Renamed upstream", version: 4),
            importSource: "test", source: StepTemplateSource.CommunityLibrary);

        v2.Id.Should().Be(v1.Id, "re-import must update in place, not duplicate");
        v2.Name.Should().Be("Renamed upstream");

        await using var db = postgres.CreateContext();
        (await db.StepTemplates.AsNoTracking()
                .CountAsync(t => t.CommunityTemplateId == communityId))
            .Should().Be(1);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string UniqueName() => "tmpl-" + Guid.NewGuid().ToString("N")[..12];

    private static string TemplateJson(string communityId, string name, int version) =>
        $$"""
        {
          "Id": "{{communityId}}",
          "Name": "{{name}}",
          "ActionType": "Octopus.Script",
          "Version": {{version}},
          "Category": "script",
          "Author": "tester",
          "Description": "import test",
          "Properties": { "Octopus.Action.Script.ScriptBody": "echo hi" },
          "Parameters": [
            {
              "Name": "P1", "Label": "Param 1", "HelpText": "h",
              "DefaultValue": "d",
              "DisplaySettings": { "Octopus.ControlType": "SingleLineText" }
            }
          ]
        }
        """;
}
