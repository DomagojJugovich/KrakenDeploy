using System.Globalization;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// The wire-contract gate on the SignalR handshake. Refusing here rather than inside
/// <c>AgentHub.RegisterAsync</c> is what lets "connected" mean "verified and dispatchable":
/// a hub method cannot run until the connection exists, so a check living there forced the
/// server to admit a connection it could not yet trust, and everything downstream — the
/// dispatch predicate, the offline mark, the mid-wave disconnect monitor — had to be taught
/// about the resulting half-state.
/// <para>
/// End-to-end refusal of a real SignalR client is covered by
/// <c>MultiAccountAgentTransportE2ETests.Agent_with_a_skewed_contract_version_is_refused</c>.
/// These tests pin the middleware's own contract, including the response an agent has to be
/// able to act on.
/// </para>
/// </summary>
public class AgentContractHandshakeGateTests
{
    [Fact]
    public async Task A_matching_version_passes_through()
    {
        var (context, gate, audit) = Build(sentVersion: AgentContract.CurrentVersion);

        var reached = false;
        await new AgentContractHandshakeGate(
            _ => { reached = true; return Task.CompletedTask; },
            NullLogger<AgentContractHandshakeGate>.Instance)
            .InvokeAsync(context, audit);

        reached.Should().BeTrue("a matching agent must reach the hub");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        audit.Events.Should().BeEmpty("nothing was refused, so nothing is audited");
        _ = gate;
    }

    [Theory]
    [InlineData("2")]            // an older declared version
    [InlineData("4")]            // a NEWER one — a rolled-back server must refuse too
    [InlineData("")]             // absent: an agent predating the header
    [InlineData("not-a-number")] // garbled: not evidence of compatibility
    public async Task A_skewed_or_missing_version_is_refused_with_426(string sent)
    {
        var (context, _, audit) = Build(rawVersion: sent);

        var reached = false;
        await new AgentContractHandshakeGate(
            _ => { reached = true; return Task.CompletedTask; },
            NullLogger<AgentContractHandshakeGate>.Instance)
            .InvokeAsync(context, audit);

        reached.Should().BeFalse("the connection must never reach the hub");
        context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired,
            "426 is the accurate status — authenticated and well-formed, wrong protocol " +
            "version — and it must not be 401/403, which mean 're-enroll this agent' and " +
            "route the agent's reconnect policy to a different lane");
        context.Response.Headers[AgentContract.ServerVersionHeader].ToString()
            .Should().Be(AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                "the agent's log should be able to name both versions, not just its own");
    }

    [Fact]
    public async Task A_refusal_is_audited_against_the_target_from_the_jwt()
    {
        var targetId = Guid.NewGuid();
        var (context, _, audit) = Build(sentVersion: AgentContract.CurrentVersion - 1, targetId);

        await new AgentContractHandshakeGate(
            _ => Task.CompletedTask, NullLogger<AgentContractHandshakeGate>.Instance)
            .InvokeAsync(context, audit);

        var entry = audit.Events.Should().ContainSingle().Subject;
        entry.EventType.Should().Be(AuditEventType.AgentContractVersionRejected);
        entry.SubjectId.Should().Be(targetId.ToString(),
            "an operator needs to know WHICH agent is skewed — this is the only reason the " +
            "gate is mounted after authentication rather than before it");
        entry.Details.Should().Contain(
            AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_failed_audit_write_still_refuses_cleanly()
    {
        // The audit needs a resolved tenant database and this gate runs early, so recording
        // can fail for reasons that have nothing to do with the refusal. It must not turn
        // "upgrade your agent" into an opaque 500: a missing row is recoverable, an
        // unactionable response is not.
        var (context, _, _) = Build(sentVersion: AgentContract.CurrentVersion - 1, Guid.NewGuid());
        var throwing = new ThrowingAuditLog();

        await new AgentContractHandshakeGate(
            _ => Task.CompletedTask, NullLogger<AgentContractHandshakeGate>.Instance)
            .InvokeAsync(context, throwing);

        throwing.Attempted.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);
    }

    [Fact]
    public async Task A_non_hub_path_is_never_inspected()
    {
        // The gate is path-scoped so it cannot accidentally police the UI hub or the REST
        // surface, neither of which speaks the agent contract.
        var (context, _, audit) = Build(rawVersion: null);
        context.Request.Path = "/hubs/ui";

        var reached = false;
        await new AgentContractHandshakeGate(
            _ => { reached = true; return Task.CompletedTask; },
            NullLogger<AgentContractHandshakeGate>.Instance)
            .InvokeAsync(context, audit);

        reached.Should().BeTrue();
        audit.Events.Should().BeEmpty();
    }

    private static (DefaultHttpContext Context, object Gate, RecordingAuditLog Audit) Build(
        int? sentVersion = null, Guid? targetId = null, string? rawVersion = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/agent/negotiate";
        context.Response.Body = new MemoryStream();

        var value = rawVersion
            ?? sentVersion?.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(value))
        {
            context.Request.Headers[AgentContract.VersionHeader] = value;
        }

        if (targetId is { } id)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id.ToString())], "AgentJwt"));
        }

        return (context, new object(), new RecordingAuditLog());
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        internal List<(string EventType, string? SubjectId, string? Details)> Events { get; } = [];

        public Task RecordAsync(
            string eventType, string? subjectType = null, string? subjectId = null,
            string? subjectName = null, string? details = null, Guid? userId = null,
            string? userDisplay = null, CancellationToken ct = default)
        {
            Events.Add((eventType, subjectId, details));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAuditLog : IAuditLog
    {
        internal bool Attempted { get; private set; }

        public Task RecordAsync(
            string eventType, string? subjectType = null, string? subjectId = null,
            string? subjectName = null, string? details = null, Guid? userId = null,
            string? userDisplay = null, CancellationToken ct = default)
        {
            Attempted = true;
            throw new InvalidOperationException("no tenant database resolved");
        }
    }
}
