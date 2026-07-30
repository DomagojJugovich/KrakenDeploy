using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Ai;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Mcp.Tools;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Ai.Adhoc;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.E.10 — tests for the <see cref="AdhocTools"/> MCP tools. Pins the
/// permission gate (no AdhocActionsExecute → 403-shaped McpException),
/// happy-path initiation (returns a proposed script + approval URL and
/// ApprovalPending=true — the human MUST still approve in the UI), and the
/// gate-rejection translation.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AdhocMcpToolTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.AdhocSessions.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.DeploymentTargets.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Run_throws_McpException_when_principal_lacks_AdhocActionsExecute()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(grantPermission: false);

        var act = async () => await AdhocTools.RunAdhocActionAsync(
            harness.Sessions, harness.Permissions, harness.HttpContext, harness.Audit,
            "list services", "readonly", [target.Id], default);

        var ex = await act.Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("AdhocActionsExecute");
    }

    [Fact]
    public async Task Run_throws_McpException_when_no_authenticated_principal()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(grantPermission: true, anonymous: true);

        var act = async () => await AdhocTools.RunAdhocActionAsync(
            harness.Sessions, harness.Permissions, harness.HttpContext, harness.Audit,
            "list services", "readonly", [target.Id], default);

        await act.Should().ThrowAsync<McpException>()
            .WithMessage("*no authenticated principal*");
    }

    [Fact]
    public async Task Run_happy_path_creates_session_returns_proposed_script_and_ApprovalPending_true()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(grantPermission: true);

        var result = await AdhocTools.RunAdhocActionAsync(
            harness.Sessions, harness.Permissions, harness.HttpContext, harness.Audit,
            "list services", "readonly", [target.Id], default);

        result.SessionId.Should().NotBe(Guid.Empty);
        result.IterationId.Should().NotBe(Guid.Empty);
        result.ApprovalUrl.Should().Be($"/adhoc/{result.SessionId:D}");
        result.Mode.Should().Be("Readonly");
        result.ProposedScript.Should().Be("Get-Service");
        result.ApprovalPending.Should().BeTrue(
            "the MCP tool MUST NOT auto-approve; the operator approves in the UI");
        result.HumanApprovalRequiredNote.Should().Contain("operator");
        result.FrozenTargetIds.Should().BeEquivalentTo(new[] { target.Id });
    }

    [Fact]
    public async Task Run_translates_gate_rejection_into_McpException_with_violation_summary()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(
            grantPermission: true,
            // LLM hands back a script the gate rejects.
            generation: new AdhocGenerationResult { GeneratedScript = "Invoke-Expression $x" });

        var act = async () => await AdhocTools.RunAdhocActionAsync(
            harness.Sessions, harness.Permissions, harness.HttpContext, harness.Audit,
            "do anything", "mutating", [target.Id], default);

        var ex = await act.Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("gate rejected");
    }

    [Fact]
    public async Task Run_invalid_mode_string_throws_McpException()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(grantPermission: true);

        var act = async () => await AdhocTools.RunAdhocActionAsync(
            harness.Sessions, harness.Permissions, harness.HttpContext, harness.Audit,
            "list services", "yolo", [target.Id], default);

        await act.Should().ThrowAsync<McpException>()
            .WithMessage("*Invalid mode*");
    }

    [Fact]
    public async Task GetSession_returns_state_with_iteration_details()
    {
        var target = await SeedTargetAsync("web-01");
        var harness = NewHarness(grantPermission: true);

        var init = await AdhocTools.RunAdhocActionAsync(
            harness.Sessions, harness.Permissions, harness.HttpContext, harness.Audit,
            "list services", "readonly", [target.Id], default);

        var detail = await AdhocTools.GetAdhocSessionAsync(
            harness.Sessions, harness.Permissions, harness.HttpContext, harness.Audit,
            init.SessionId, default);

        detail.SessionId.Should().Be(init.SessionId);
        detail.Status.Should().Be(nameof(AdhocSessionStatus.Active));
        detail.Iterations.Should().ContainSingle();
        detail.Iterations[0].IterationId.Should().Be(init.IterationId);
        detail.Iterations[0].Status.Should().Be(nameof(AdhocIterationStatus.PendingApproval));
        detail.Iterations[0].Verdict.Should().BeNull("verdict not yet evaluated");
    }

    [Fact]
    public async Task GetSession_unknown_id_throws_McpException()
    {
        var harness = NewHarness(grantPermission: true);

        var act = async () => await AdhocTools.GetAdhocSessionAsync(
            harness.Sessions, harness.Permissions, harness.HttpContext, harness.Audit,
            Guid.NewGuid(), default);

        await act.Should().ThrowAsync<McpException>()
            .WithMessage("*No ad-hoc session*");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<DeploymentTarget> SeedTargetAsync(string name)
    {
        await using var db = postgres.CreateContext();
        var t = new DeploymentTarget
        {
            Name = name, OperatingSystem = "Windows Server 2022",
            Roles = ["web"], Status = TargetStatus.Online,
        };
        db.DeploymentTargets.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    private sealed record Harness(
        AdhocSessionService Sessions,
        IPermissionEvaluator Permissions,
        IHttpContextAccessor HttpContext,
        IAuditLog Audit);

    private Harness NewHarness(
        bool grantPermission,
        AdhocGenerationResult? generation = null,
        bool anonymous = false)
    {
        var krakenAi = new SequencedKrakenAi(
            (object?)generation ?? new AdhocGenerationResult { GeneratedScript = "Get-Service" },
            new IterationVerdict { Verdict = "AllSucceeded", Narrative = "ok" });

        var genSvc = new AdhocGenerationService(krakenAi, NullLogger<AdhocGenerationService>.Instance);
        var verdictSvc = new AdhocVerdictService(krakenAi, NullLogger<AdhocVerdictService>.Instance);

        using var src = RSA.Create(2048);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Adhoc:SigningKey"] = src.ExportRSAPrivateKeyPem(),
            }).Build();

        var keyProvider = new AdhocSigningKeyProvider(config);
        var audit = new RecordingAuditLog(postgres);
        var sessionService = new AdhocSessionService(
            postgres,
            new SettingsService(postgres.ScopeFactory, TimeProvider.System),
            genSvc, verdictSvc, keyProvider,
            new FakeAdhocDispatcher(),
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            audit, config, TimeProvider.System,
            NullLogger<AdhocSessionService>.Instance);

        var perms = new FakePermissionEvaluator(grantPermission);
        var http = new HttpContextAccessor { HttpContext = BuildContext(anonymous) };

        return new Harness(sessionService, perms, http, audit);
    }

    private static DefaultHttpContext BuildContext(bool anonymous)
    {
        var ctx = new DefaultHttpContext();
        if (anonymous)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
        else
        {
            var id = Guid.NewGuid();
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, id.ToString()),
                new Claim(ClaimTypes.Name, "mcp-test@laus.hr"),
            ], authenticationType: "test"));
        }
        return ctx;
    }

    private sealed class FakePermissionEvaluator(bool allow) : IPermissionEvaluator
    {
        public Task<bool> HasPermissionAsync(
            ClaimsPrincipal user, Permission permission,
            PermissionScope scope = default, bool bypassCache = false,
            bool strictScope = false, CancellationToken ct = default)
            => Task.FromResult(allow);

        public Task<IReadOnlySet<Permission>> GetPermissionsAsync(
            ClaimsPrincipal user, PermissionScope scope = default,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<Permission>>(
                allow ? new HashSet<Permission> { Permission.AdhocActionsExecute }
                      : new HashSet<Permission>());

        public Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
            ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public Task<IReadOnlySet<Guid>> GetUserTeamIdsAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }

    private sealed class SequencedKrakenAi(params object?[] responses) : IKrakenAi
    {
        private int _idx;
        public Task<TResult> CompleteAsync<TResult>(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            where TResult : class
            => Task.FromResult((TResult)responses[_idx++]!);

        public Task<KrakenAiCompletion> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeAdhocDispatcher : IAdhocDispatcher
    {
        public Task<IReadOnlyList<AdhocPerTargetResult>> DispatchAsync(
            AdhocSession session, AdhocIteration iteration, Guid dispatchAccountId,
            IReadOnlyDictionary<Guid, bool> allowParallelByTarget,
            CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult<IReadOnlyList<AdhocPerTargetResult>>([]);
    }

    private sealed class RecordingAuditLog(PostgresFixture postgres) : IAuditLog
    {
        public async Task RecordAsync(
            string eventType,
            string? subjectType = null, string? subjectId = null,
            string? subjectName = null, string? details = null,
            Guid? userId = null, string? userDisplay = null,
            CancellationToken ct = default)
        {
            await using var db = postgres.CreateContext();
            db.AuditEntries.Add(new AuditEntry
            {
                EventType   = eventType,
                SubjectType = subjectType,
                SubjectId   = subjectId,
                SubjectName = subjectName,
                Details     = details,
                OccurredUtc = DateTimeOffset.UtcNow,
                SpaceId     = WellKnown.DefaultSpaceId,
                UserId      = userId,
                UserDisplay = userDisplay ?? "test",
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
