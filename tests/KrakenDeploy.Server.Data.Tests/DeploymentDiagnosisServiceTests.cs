using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using KrakenDeploy.Server.Data.Services.Ai.Curators;
using KrakenDeploy.Server.Data.Services.Ai.Diagnosis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.C — tests for the autonomous diagnosis service against the Postgres
/// fixture, with a fake <see cref="IKrakenAi"/>. Pins: a successful
/// structured-output diagnosis persists + audits + parses confidence and
/// relevant log lines; re-diagnosis upserts; AI-unavailable
/// (disabled / feature-off / budget) never throws + never persists.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DeploymentDiagnosisServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.DeploymentDiagnoses.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.TaskStepOutcomes.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.TaskLogLive.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Deployments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Releases.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Environments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Diagnose_persists_structured_result_and_audits()
    {
        var depId = await SeedFailedDeploymentAsync();
        var ai = new FakeKrakenAi(new DiagnosisResult
        {
            ProbableCause = "The service failed to start because port 8080 was already bound.",
            Confidence    = "High",
            SuggestedFix  = "Stop the conflicting process or change the bind port.",
            RelevantLogLines = [new DiagnosisLogLine { Sequence = 2, Text = "ERROR: port in use" }],
        });
        var audit = new SpyAuditLog();
        var service = NewService(ai, audit);

        await service.DiagnoseAsync(depId);

        await using var db = postgres.CreateContext();
        var row = await db.DeploymentDiagnoses.FirstOrDefaultAsync(x => x.DeploymentId == depId);
        row.Should().NotBeNull();
        row!.Confidence.Should().Be(DiagnosisConfidence.High);
        row.ProbableCause.Should().Contain("port 8080");
        row.SpaceId.Should().Be(WellKnown.DefaultSpaceId);

        var lines = JsonSerializer.Deserialize<JsonElement>(row.RelevantLogLinesJson);
        lines.GetArrayLength().Should().Be(1);
        lines[0].GetProperty("sequence").GetInt32().Should().Be(2);

        audit.Events.Should().Contain(e => e == AuditEventType.DiagnosisCompleted);
    }

    [Fact]
    public async Task Diagnose_upserts_on_second_run()
    {
        var depId = await SeedFailedDeploymentAsync();
        var audit = new SpyAuditLog();

        await NewService(new FakeKrakenAi(Result("first", "Low")), audit).DiagnoseAsync(depId);
        await NewService(new FakeKrakenAi(Result("second", "High")), audit).DiagnoseAsync(depId);

        await using var db = postgres.CreateContext();
        var rows = await db.DeploymentDiagnoses.IgnoreQueryFilters()
            .Where(x => x.DeploymentId == depId).ToListAsync();
        rows.Should().ContainSingle(because: "the unique index + upsert keep one row per deployment");
        rows[0].ProbableCause.Should().Be("second");
        rows[0].Confidence.Should().Be(DiagnosisConfidence.High);
    }

    [Theory]
    [InlineData(FakeFailureMode.Disabled)]
    [InlineData(FakeFailureMode.FeatureDisabled)]
    [InlineData(FakeFailureMode.BudgetExceeded)]
    [InlineData(FakeFailureMode.Transient)]
    public async Task Diagnose_tolerates_ai_unavailable_without_throwing_or_persisting(FakeFailureMode mode)
    {
        var depId = await SeedFailedDeploymentAsync();
        var service = NewService(new FakeKrakenAi(mode), new SpyAuditLog());

        var act = async () => await service.DiagnoseAsync(depId);
        await act.Should().NotThrowAsync(
            because: "a missing/over-budget/erroring AI must never surface as a failure");

        await using var db = postgres.CreateContext();
        (await db.DeploymentDiagnoses.IgnoreQueryFilters()
            .AnyAsync(x => x.DeploymentId == depId)).Should().BeFalse();
    }

    [Fact]
    public async Task Diagnose_unknown_deployment_is_a_silent_noop()
    {
        var service = NewService(new FakeKrakenAi(Result("x", "Low")), new SpyAuditLog());
        var act = async () => await service.DiagnoseAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DiagnosisResult Result(string cause, string confidence)
        => new() { ProbableCause = cause, Confidence = confidence, SuggestedFix = "" };

    private DeploymentDiagnosisService NewService(IKrakenAi ai, IAuditLog audit)
    {
        var curators = new StepConfigCuratorRegistry(
            new IStepConfigCurator[] { new ScriptStepConfigCurator() },
            new DefaultStepConfigCurator());
        var encryption = TestCrypto.Service(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var assembler = new DiagnosisContextAssembler(
            postgres,
            new DeploymentContextBuilder(postgres),
            new DeploymentDiffBuilder(postgres),
            new TargetHealthBuilder(postgres),
            curators,
            encryption,
            NullLogger<DiagnosisContextAssembler>.Instance);
        return new DeploymentDiagnosisService(
            postgres, assembler, ai, audit,
            NullLogger<DeploymentDiagnosisService>.Instance);
    }

    private async Task<Guid> SeedFailedDeploymentAsync()
    {
        await using var db = postgres.CreateContext();
        var project = new Project { SpaceId = WellKnown.DefaultSpaceId, Name = "P", Slug = "p" };
        var env = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId, Name = "prod", Slug = "prod", SortOrder = 1,
        };
        db.Projects.Add(project);
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, Version = "1.0",
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
            ProcessSnapshot =
            {
                new StepSnapshot
                {
                    Id = Guid.NewGuid(), Name = "Start service", StepType = "Octopus.Script",
                    SortOrder = 0,
                    Config = new Dictionary<string, string>
                    {
                        ["Octopus.Action.Script.ScriptBody"] = "Start-Service Argosy",
                    },
                },
            },
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        var deployment = new Deployment
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id,
            ReleaseId = release.Id, EnvironmentId = env.Id,
            Status = DeploymentStatus.Failed,
            StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        db.TaskStepOutcomes.Add(new TaskStepOutcome
        {
            TaskId = deployment.Id, StepIndex = 0, StepName = "Start service",
            Outcome = StepOutcomeKind.Failed, Required = true, AttemptCount = 1,
            ErrorMessage = "service failed to start", CompletedUtc = DateTimeOffset.UtcNow,
        });
        db.TaskLogLive.AddRange(
            new TaskLogLiveEntry { TaskId = deployment.Id, StepIndex = 0, TargetId = null, Sequence = 0, Timestamp = DateTimeOffset.UtcNow, Level = "info", Message = "starting" },
            new TaskLogLiveEntry { TaskId = deployment.Id, StepIndex = 0, TargetId = null, Sequence = 1, Timestamp = DateTimeOffset.UtcNow, Level = "info", Message = "binding port 8080" },
            new TaskLogLiveEntry { TaskId = deployment.Id, StepIndex = 0, TargetId = null, Sequence = 2, Timestamp = DateTimeOffset.UtcNow, Level = "error", Message = "ERROR: port in use" });
        await db.SaveChangesAsync();
        return deployment.Id;
    }

    public enum FakeFailureMode { None, Disabled, FeatureDisabled, BudgetExceeded, Transient }

    private sealed class FakeKrakenAi : IKrakenAi
    {
        private readonly DiagnosisResult? _result;
        private readonly FakeFailureMode _mode;

        public FakeKrakenAi(DiagnosisResult result) { _result = result; _mode = FakeFailureMode.None; }
        public FakeKrakenAi(FakeFailureMode mode) { _mode = mode; }

        public Task<TResult> CompleteAsync<TResult>(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            where TResult : class
        {
            switch (_mode)
            {
                case FakeFailureMode.Disabled: throw new KrakenAiDisabledException("provider disabled");
                case FakeFailureMode.FeatureDisabled: throw new KrakenAiFeatureDisabledException("Diagnosis");
                case FakeFailureMode.BudgetExceeded: throw new KrakenAiBudgetExceededException(10m, 5m);
                case FakeFailureMode.Transient: throw new InvalidOperationException("transient LLM error");
                default: return Task.FromResult((TResult)(object)_result!);
            }
        }

        public Task<KrakenAiCompletion> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class SpyAuditLog : IAuditLog
    {
        public List<string> Events { get; } = [];
        public Task RecordAsync(
            string eventType, string? subjectType = null, string? subjectId = null,
            string? subjectName = null, string? details = null, Guid? userId = null,
            string? userDisplay = null, CancellationToken ct = default)
        {
            Events.Add(eventType);
            return Task.CompletedTask;
        }
    }
}
