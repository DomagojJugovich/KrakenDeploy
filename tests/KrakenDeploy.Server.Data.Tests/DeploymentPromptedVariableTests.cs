using System.Security.Cryptography;
using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DeploymentPromptedVariableTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private readonly byte[] _dek = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);

    [Fact]
    public async Task CreateAsync_rejects_unknown_and_missing_required_values()
    {
        var graph = await SeedGraphAsync();
        var service = NewService();

        var missing = () => service.CreateAsync(
            graph.ReleaseId, graph.EnvironmentId, graph.TargetId,
            TaskInitiator.Scheduled("prompt-test"), CallerAuthorization.System);
        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Required prompted variables*Approval code*");

        var unknown = () => service.CreateAsync(
            graph.ReleaseId, graph.EnvironmentId, graph.TargetId,
            TaskInitiator.Scheduled("prompt-test"), CallerAuthorization.System,
            promptedValues: new Dictionary<string, string> { ["Unknown"] = "value" });
        await unknown.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unknown prompted variable*Unknown*");
    }

    [Fact]
    public async Task CreateAsync_encrypts_sensitive_values_and_stamps_each_tenant_task()
    {
        var graph = await SeedGraphAsync();
        Guid firstTenantId;
        Guid secondTenantId;
        await using (var db = postgres.CreateContext())
        {
            var first = new Tenant { Name = "Tenant A", Slug = $"a-{Guid.NewGuid():N}" };
            var second = new Tenant { Name = "Tenant B", Slug = $"b-{Guid.NewGuid():N}" };
            db.Tenants.AddRange(first, second);
            await db.SaveChangesAsync();
            firstTenantId = first.Id;
            secondTenantId = second.Id;
        }

        var supplied = new Dictionary<string, string>
        {
            ["ApprovalCode"] = "approved",
            ["ApiToken"] = "never-store-me-in-plaintext",
        };
        var service = NewService();
        var firstDeployment = await service.CreateAsync(
            graph.ReleaseId, graph.EnvironmentId, graph.TargetId,
            TaskInitiator.Scheduled("prompt-test"), CallerAuthorization.System,
            tenantId: firstTenantId, promptedValues: supplied);
        var secondDeployment = await service.CreateAsync(
            graph.ReleaseId, graph.EnvironmentId, graph.TargetId,
            TaskInitiator.Scheduled("prompt-test"), CallerAuthorization.System,
            tenantId: secondTenantId, promptedValues: supplied);

        await using var verify = postgres.CreateContext();
        var payloads = await verify.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == firstDeployment.Id || t.Id == secondDeployment.Id)
            .Select(t => t.FormValues!)
            .ToListAsync();
        payloads.Should().HaveCount(2);
        payloads.Should().OnlyContain(p => !p.Contains("never-store-me-in-plaintext", StringComparison.Ordinal));
        var crypto = TestCrypto.Service(Convert.ToBase64String(_dek));
        payloads.Select(p => PromptedVariableFormValuesCodec.Deserialize(p, crypto))
            .Should().OnlyContain(v =>
                v["ApprovalCode"] == "approved" &&
                v["ApiToken"] == "never-store-me-in-plaintext");
        var taskIds = new[] { firstDeployment.Id.ToString(), secondDeployment.Id.ToString() };
        var auditJson = await verify.AuditEntries
            .Where(a => taskIds.Contains(a.SubjectId!))
            .Select(a => new { a.BeforeJson, a.AfterJson })
            .ToListAsync();
        auditJson.Should().NotContain(row =>
            (row.BeforeJson ?? "").Contains("never-store-me-in-plaintext", StringComparison.Ordinal) ||
            (row.AfterJson ?? "").Contains("never-store-me-in-plaintext", StringComparison.Ordinal));
    }

    private DeploymentService NewService() => new(
        postgres,
        Channel.CreateUnbounded<TenantWorkItem>(),
        TimeProvider.System,
        new DisabledAccountContext(),
        new PermissionEvaluator(postgres, TimeProvider.System),
        encryption: TestCrypto.Service(Convert.ToBase64String(_dek)));

    private async Task<(Guid ReleaseId, Guid EnvironmentId, Guid TargetId)> SeedGraphAsync()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"pv-{Guid.NewGuid():N}"[..16]);
        var environment = await harness.SeedEnvironmentAsync($"pe-{Guid.NewGuid():N}"[..16]);
        var target = (await harness.SeedTargetsAsync($"pt-{Guid.NewGuid():N}"[..16]))[0];
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));

        await using var db = postgres.CreateContext();
        var stored = await db.Releases.SingleAsync(r => r.Id == release.Id);
        stored.VariableSnapshot =
        [
            new VariableSnapshot
            {
                Name = "ApprovalCode",
                Value = "default",
                IsPrompted = true,
                PromptLabel = "Approval code",
                PromptRequired = true,
                Layer = VariableSnapshot.ProjectLayer,
            },
            new VariableSnapshot
            {
                Name = "ApiToken",
                Value = AesGcmCipher.Encrypt(_dek, "default-token"),
                Type = VariableType.Sensitive,
                IsPrompted = true,
                PromptLabel = "API token",
                Layer = VariableSnapshot.ProjectLayer,
            },
        ];
        await db.SaveChangesAsync();
        return (release.Id, environment.Id, target.Id);
    }
}
