using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// What the server records when it refuses an agent's wire contract on the handshake, asserted
/// against a REAL database and the real <see cref="AuditLogService"/>.
/// <para>
/// Both halves matter and neither had coverage. Moving the contract check onto the handshake
/// silently dropped four side effects the in-hub refusal had, and the audit row itself had no
/// persisted-to-a-database test anywhere — only fakes, which cannot catch the attribution
/// fallback that stamped a DeploymentTarget id into <c>AuditEntry.UserId</c>.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AgentContractRefusalRecorderTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_refusal_marks_the_target_offline_and_pushes_the_change()
    {
        // The side effect an operator notices. Without it the WHOLE FLEET reads Online after a
        // contract-bumping server upgrade until AgentLastSeenOfflineJob catches it — a 3-minute
        // threshold on a 5-minute cron, so up to ~8 minutes — and that job does not call the
        // status publisher, so an open dashboard stays green until someone reloads. An operator
        // mid-upgrade reading a green fleet concludes the upgrade went fine.
        var target = await SeedTargetAsync(TargetStatus.Online);
        var pushes = new RecordingNotifier();

        await BuildRecorder(pushes).RecordAsync(target.Id, "v3");

        await using var db = postgres.CreateContext();
        var reloaded = await db.DeploymentTargets.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == target.Id);
        reloaded.Status.Should().Be(TargetStatus.Offline,
            "a refused agent cannot be dispatched to, so it must not read Online");

        pushes.Pushed.Should().ContainSingle().Which.Should()
            .Be((target.Id, TargetStatus.Offline));
    }

    [Fact]
    public async Task A_retired_target_is_not_downgraded_and_an_offline_one_is_not_rewritten()
    {
        // Disabled means RETIRED (soft-decommissioned) and is deliberate state — the
        // retired-registration path is careful not to downgrade it and neither may this.
        // Offline needs no write at all: the refusal repeats for as long as the skew lasts, and
        // rewriting the same value on every window would be pure churn on the fleet's rows.
        foreach (var status in new[] { TargetStatus.Disabled, TargetStatus.Offline })
        {
            var target = await SeedTargetAsync(status);
            var pushes = new RecordingNotifier();

            await BuildRecorder(pushes).RecordAsync(target.Id, "v3");

            await using var db = postgres.CreateContext();
            var reloaded = await db.DeploymentTargets.IgnoreQueryFilters()
                .FirstAsync(t => t.Id == target.Id);
            reloaded.Status.Should().Be(status);
            pushes.Pushed.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task The_audit_row_names_the_target_and_is_attributed_to_the_system()
    {
        // The first DB-persisted assertion on this row. It catches what a fake cannot:
        // AuditLogService falls back to the ambient HTTP principal's NameIdentifier when no
        // attribution is passed, and on this path that principal is the AGENT — so UserId got a
        // DeploymentTarget GUID and UserDisplay rendered "Unknown". The HTTP context here
        // carries exactly such a principal, so the fallback is live for this test.
        var target = await SeedTargetAsync(TargetStatus.Online);

        await BuildRecorder(agentPrincipalTargetId: target.Id).RecordAsync(target.Id, "absent");

        await using var db = postgres.CreateContext();
        var row = await db.AuditEntries.AsNoTracking()
            .Where(e => e.EventType == AuditEventType.AgentContractVersionRejected
                        && e.SubjectId == target.Id.ToString())
            .OrderByDescending(e => e.OccurredUtc)
            .FirstAsync();

        row.SubjectType.Should().Be("DeploymentTarget");
        row.SubjectName.Should().Be(target.Name,
            "without it the audit grid, the CSV/JSON export and the notification e-mails " +
            "identify the refused agent by bare GUID");
        row.Details.Should().Contain("SentContract=absent")
            .And.Contain($"RequiredContract={AgentContract.CurrentVersion}");
        row.UserId.Should().BeNull("the agent is not a user");
        row.UserDisplay.Should().Be("System");
    }

    [Fact]
    public async Task An_unknown_target_still_records_the_refusal()
    {
        // A stale credential for a deleted target. There is nothing to mark Offline, but the
        // refusal must still be visible — an operator chasing a mystery agent needs the row.
        var unknown = Guid.CreateVersion7();

        await BuildRecorder().RecordAsync(unknown, "v2");

        await using var db = postgres.CreateContext();
        var row = await db.AuditEntries.AsNoTracking()
            .FirstAsync(e => e.SubjectId == unknown.ToString());
        row.SubjectName.Should().BeNull();
        row.EventType.Should().Be(AuditEventType.AgentContractVersionRejected);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<DeploymentTarget> SeedTargetAsync(TargetStatus status)
    {
        await using var db = postgres.CreateContext();
        var target = new DeploymentTarget
        {
            Name = $"refusal-{Guid.NewGuid():N}"[..24],
            Roles = ["web"],
            TransportMode = TransportMode.Reverse,
            Status = status,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();
        return target;
    }

    private AgentContractRefusalRecorder BuildRecorder(
        RecordingNotifier? notifier = null, Guid? agentPrincipalTargetId = null)
    {
        var http = new HttpContextAccessor();
        if (agentPrincipalTargetId is { } id)
        {
            var context = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(
                            System.Security.Claims.ClaimTypes.NameIdentifier, id.ToString())],
                        "AgentJwt")),
            };
            http.HttpContext = context;
        }

        return new AgentContractRefusalRecorder(
            postgres,
            new TargetStatusPublisher(
                notifier ?? new RecordingNotifier(),
                new NullUiHub(),
                NullLogger<TargetStatusPublisher>.Instance),
            new Accounts.DisabledAccountContext(),
            new AuditLogService(
                postgres, http, new FixedSpaceContext(), TimeProvider.System),
            TimeProvider.System,
            NullLogger<AgentContractRefusalRecorder>.Instance);
    }

    /// <summary>The external half of the status push is not what these tests are about —
    /// TargetStatusPublisher already swallows its failures — so it sinks.</summary>
    private sealed class NullUiHub : Microsoft.AspNetCore.SignalR.IHubContext<UiHub, IUiHubClient>
    {
        public Microsoft.AspNetCore.SignalR.IHubClients<IUiHubClient> Clients { get; } = new Sink();

        public Microsoft.AspNetCore.SignalR.IGroupManager Groups
            => throw new NotSupportedException();

        private sealed class Sink : Microsoft.AspNetCore.SignalR.IHubClients<IUiHubClient>
        {
            private readonly IUiHubClient _client = new Client();
            public IUiHubClient All => _client;
            public IUiHubClient AllExcept(IReadOnlyList<string> excluded) => _client;
            public IUiHubClient Client(string connectionId) => _client;
            public IUiHubClient Clients(IReadOnlyList<string> connectionIds) => _client;
            public IUiHubClient Group(string groupName) => _client;
            public IUiHubClient GroupExcept(string groupName, IReadOnlyList<string> excluded) => _client;
            public IUiHubClient Groups(IEnumerable<string> groupNames) => _client;
            public IUiHubClient Groups(IReadOnlyList<string> groupNames) => _client;
            public IUiHubClient User(string userId) => _client;
            public IUiHubClient Users(IEnumerable<string> userIds) => _client;
            public IUiHubClient Users(IReadOnlyList<string> userIds) => _client;
        }

        private sealed class Client : IUiHubClient
        {
            public Task TargetStatusChangedAsync(
                Guid targetId, string status, DateTimeOffset? lastSeenUtc) => Task.CompletedTask;

            public Task DeploymentLogAppendedAsync(
                Guid deploymentId, int sequence, DateTimeOffset timestamp,
                string level, string message) => Task.CompletedTask;

            public Task DeploymentStatusChangedAsync(Guid deploymentId, string status)
                => Task.CompletedTask;
        }
    }

    private sealed class RecordingNotifier : ITargetStatusNotifier
    {
        public event Action<Guid, TargetStatus, DateTimeOffset?>? TargetStatusChanged;

        internal List<(Guid TargetId, TargetStatus Status)> Pushed { get; } = [];

        public void Publish(Guid targetId, TargetStatus status, DateTimeOffset? lastSeenUtc)
        {
            Pushed.Add((targetId, status));
            TargetStatusChanged?.Invoke(targetId, status, lastSeenUtc);
        }
    }

    private sealed class FixedSpaceContext : Core.Domain.Spaces.ISpaceContext
    {
        public Guid CurrentSpaceId => Core.Domain.Common.WellKnown.DefaultSpaceId;
        public string CurrentSpaceSlug => "default";
        public IDisposable WithSpace(Guid newSpaceId) => new NoOp();
        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }
}
