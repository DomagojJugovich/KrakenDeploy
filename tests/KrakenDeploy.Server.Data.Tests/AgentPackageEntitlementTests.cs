using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Finding #6 — per-target package-download ENTITLEMENT. The two gRPC delivery
/// services (package + step-package) used to serve any artifact to any
/// authenticated agent by id. <see cref="AgentPackageEntitlement"/> restricts a
/// target to packages/step-packages that some deployment dispatched to it
/// references. These tests drive the predicate directly against Postgres (the
/// gRPC plumbing is thin glue over this decision), proving an entitled target is
/// allowed (primary AND referenced packages), an unrelated package is denied, and
/// a target with no deployment is entitled to nothing.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AgentPackageEntitlementTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Package_download_is_entitled_only_for_referenced_packages()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await using var db = harness.CreateContext();

        (await AgentPackageEntitlement.TargetMayDownloadPackageAsync(db, g.Target.Id, g.PrimaryPkg))
            .Should().BeTrue("the target's deployment references the primary package");
        (await AgentPackageEntitlement.TargetMayDownloadPackageAsync(db, g.Target.Id, g.RefPkg))
            .Should().BeTrue("the target's deployment references it via PackageReferences");
        (await AgentPackageEntitlement.TargetMayDownloadPackageAsync(
                db, g.Target.Id, $"unrelated-{Guid.NewGuid():N}"))
            .Should().BeFalse("no deployment of the target references an arbitrary package");
        (await AgentPackageEntitlement.TargetMayDownloadPackageAsync(db, g.Foreign.Id, g.PrimaryPkg))
            .Should().BeFalse("a target with no deployment is entitled to nothing");
    }

    [Fact]
    public async Task StepPackage_download_is_entitled_only_for_referenced_step_packages()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await using var db = harness.CreateContext();

        (await AgentPackageEntitlement.TargetMayDownloadStepPackageAsync(db, g.Target.Id, g.StepPkg))
            .Should().BeTrue("the target's deployment references this step package");
        (await AgentPackageEntitlement.TargetMayDownloadStepPackageAsync(
                db, g.Target.Id, $"unrelated-{Guid.NewGuid():N}"))
            .Should().BeFalse("no deployment of the target references an arbitrary step package");
        (await AgentPackageEntitlement.TargetMayDownloadStepPackageAsync(db, g.Foreign.Id, g.StepPkg))
            .Should().BeFalse("a target with no deployment is entitled to nothing");
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Package_download_entitlement_handles_foreach_templated_reference()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await using var db = harness.CreateContext();

        // A referenced package id like "tmpl-<tag>-#{item}" is Octostache-
        // substituted at dispatch; the resolved id must be entitled via its
        // literal prefix, without granting unrelated ids.
        (await AgentPackageEntitlement.TargetMayDownloadPackageAsync(db, g.Target.Id, g.TmplPrefix + "prod"))
            .Should().BeTrue("a ForEach-templated referenced package resolves to a prefixed id at dispatch");
        (await AgentPackageEntitlement.TargetMayDownloadPackageAsync(
                db, g.Target.Id, $"unrelated-{Guid.NewGuid():N}"))
            .Should().BeFalse("the templated entitlement must not grant unrelated package ids");
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private sealed record Graph(
        DeploymentTarget Target, DeploymentTarget Foreign,
        string PrimaryPkg, string RefPkg, string StepPkg, string TmplPrefix);

    private static async Task<Graph> SeedAsync(OrchestratorTestHarness harness)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var primaryPkg = $"pkg-primary-{tag}";
        var refPkg = $"pkg-ref-{tag}";
        var stepPkg = $"step-{tag}";

        var project = await harness.SeedProjectAsync($"ent-proj-{tag}");
        var env = await harness.SeedEnvironmentAsync($"ent-env-{tag}");
        var target = (await harness.SeedTargetsAsync($"ent-target-{tag}"))[0];
        var foreign = (await harness.SeedTargetsAsync($"ent-foreign-{tag}"))[0];

        // A literal referenced package plus a ForEach-templated one whose id is
        // Octostache-substituted at dispatch (so the resolved id starts with this
        // literal prefix but is not present verbatim in the snapshot).
        var tmplPrefix = $"tmpl-{tag}-";
        var refsJson = JsonSerializer.Serialize(
            new List<PackageReference>
            {
                new() { Name = $"ref-{tag}", PackageId = refPkg },
                new() { Name = $"tmpl-{tag}", PackageId = $"{tmplPrefix}#{{item}}" },
            },
            WebJson);

        var step = new StepSnapshot
        {
            Id = Guid.NewGuid(),
            Name = "deploy",
            StepType = "Kraken.Script",
            PackageId = primaryPkg,
            PackageVersion = "1.0.0",
            StepPackageName = stepPkg,
            StepPackageVersion = "2.0.0",
            Config = new Dictionary<string, string>
            {
                [KrakenScriptConfigKeys.PackageReferences] = refsJson,
            },
            SortOrder = 0,
        };

        Guid releaseId;
        await using (var db = harness.CreateContext())
        {
            var release = new Release
            {
                SpaceId = WellKnown.DefaultSpaceId,
                ProjectId = project.Id,
                Version = $"1.0.0-{tag}",
                ProcessSnapshot = [step],
                VariableSnapshot = [],
                VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
            };
            db.Releases.Add(release);
            await db.SaveChangesAsync();
            releaseId = release.Id;
        }

        // Dispatches the release to `target` (sets TargetId + an assignment row);
        // `foreign` gets no deployment.
        await harness.CreateDeploymentAsync(releaseId, env.Id, [target]);

        return new Graph(target, foreign, primaryPkg, refPkg, stepPkg, tmplPrefix);
    }
}
