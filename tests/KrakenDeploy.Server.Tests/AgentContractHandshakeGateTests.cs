using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// The wire-contract gate on the SignalR handshake. Why the check lives here rather than in
/// a hub method is documented once, in <c>docs/agent-wire-contract.md</c>; these tests pin
/// the middleware's own contract.
/// <para>
/// End-to-end refusal of a real SignalR client — including that the 426 survives a real
/// negotiate, and that the client can read NEITHER the body nor the response header — is
/// covered by <c>MultiAccountAgentTransportE2ETests</c>. A <see cref="DefaultHttpContext"/>
/// cannot see either of those, which is why the response-header assertion that used to live
/// here could never catch the fact that no agent ever receives it.
/// </para>
/// </summary>
public class AgentContractHandshakeGateTests
{
    [Fact]
    public async Task A_matching_version_passes_through()
    {
        var h = new Harness(sentVersion: AgentContract.CurrentVersion);

        await h.InvokeAsync();

        h.ReachedHub.Should().BeTrue("a matching agent must reach the hub");
        h.Context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        h.Audit.Events.Should().BeEmpty("nothing was refused, so nothing is audited");
        h.Logger.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("3")]            // the previous declared version
    [InlineData("5")]            // a NEWER one — a rolled-back server must refuse too
    [InlineData("")]             // absent: an agent predating the header
    [InlineData("not-a-number")] // garbled: not evidence of compatibility
    [InlineData("+4")]           // a sign is not a version number (NumberStyles.None)
    public async Task A_skewed_or_missing_version_is_refused_with_426(string sent)
    {
        var h = new Harness(rawVersion: sent);

        await h.InvokeAsync();

        h.ReachedHub.Should().BeFalse("the connection must never reach the hub");
        h.Context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired,
            "426 is the accurate status — authenticated and well-formed, wrong protocol " +
            "version — and it must not be 401/403, which mean 're-enroll this agent' and " +
            "route the agent's reconnect policy to a different lane");
    }

    [Fact]
    public async Task An_endpoint_without_the_marker_is_never_inspected()
    {
        // The gate keys off RequiresAgentContract metadata, NOT off the path. That is the
        // fail-closed property: a renamed, versioned or proxy-rewritten route carries its
        // metadata with it, whereas a stale path match silently admits every agent. The path
        // here is deliberately the real hub path with no marker — under the old
        // path-matching gate this request would have been refused.
        var h = new Harness(rawVersion: null, withMarker: false);
        h.Context.Request.Path = "/hubs/agent/negotiate";

        await h.InvokeAsync();

        h.ReachedHub.Should().BeTrue();
        h.Audit.Events.Should().BeEmpty();
        h.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_duplicated_header_is_refused_and_blamed_on_the_intermediary()
    {
        // StringValues.ToString() joins with ", ", so two "X-KD-Contract: 4" headers read as
        // "4, 4" and fail to parse — a v4 agent refused by a v4 server. This topology has a
        // Caddy front and a YARP router, and YARP's RequestHeader transform APPENDS unless
        // Set is used. Refusing is correct (the server cannot know which value is real), but
        // telling the operator to upgrade a correct agent is not.
        var h = new Harness(targetId: Guid.NewGuid());
        h.Context.Request.Headers.Append(
            AgentContract.VersionHeader,
            AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture));
        h.Context.Request.Headers.Append(
            AgentContract.VersionHeader,
            AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture));

        await h.InvokeAsync();

        h.ReachedHub.Should().BeFalse();
        h.Context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);
        var detail = h.Audit.Events.Should().ContainSingle().Subject.Details!;
        detail.Should().Contain("duplicated", "the cause must be distinguishable from a skew");
        h.Body.Should().Contain("intermediary")
            .And.NotContain("Update the agent binary",
                "the agent binary may be entirely correct — the proxy transform is the fault");
    }

    [Fact]
    public async Task The_refusal_body_names_both_versions()
    {
        // Asserted because nothing did: deleting the WriteAsync left every test green. The
        // body is unreachable to the SignalR client (HttpConnection.NegotiateAsync calls
        // EnsureSuccessStatusCode before reading it), but it is what an operator sees when
        // they reproduce the refusal with curl — which is the only way to see it at all.
        var h = new Harness(sentVersion: 3);

        await h.InvokeAsync();

        h.Body.Should()
            .Contain($"v{AgentContract.CurrentVersion}")
            .And.Contain("v3");
        h.Context.Response.ContentType.Should().StartWith("text/plain",
            "an untyped body renders as a download prompt in a browser and as bytes in curl");
    }

    [Fact]
    public async Task The_refusal_always_logs_a_warning_naming_the_target()
    {
        // Also asserted because nothing did: every test used NullLogger, so deleting both
        // LogWarning calls passed. The server log is the ONLY place both version numbers
        // appear together, because the agent cannot read the response.
        var targetId = Guid.NewGuid();
        var h = new Harness(sentVersion: 3, targetId: targetId);

        await h.InvokeAsync();

        var warning = h.Logger.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain(targetId.ToString()).And.Contain("REFUSED");
    }

    [Fact]
    public async Task A_refusal_is_audited_against_the_target_as_a_system_event()
    {
        var targetId = Guid.NewGuid();
        var h = new Harness(sentVersion: 3, targetId: targetId);

        await h.InvokeAsync();

        var entry = h.Audit.Events.Should().ContainSingle().Subject;
        entry.EventType.Should().Be(AuditEventType.AgentContractVersionRejected);
        entry.SubjectType.Should().Be("DeploymentTarget",
            "the audit grid, the CSV/JSON export and the per-entity Events tab all key on it");
        entry.SubjectId.Should().Be(targetId.ToString(),
            "an operator needs to know WHICH agent is skewed — this is the only reason the " +
            "gate is mounted after authentication rather than before it");
        entry.Details.Should().Contain(
            AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture));

        // The principal on this path is an AGENT: its NameIdentifier is a DeploymentTarget
        // id. AuditLogService falls back to that when attribution is omitted, which stamps
        // UserId with a GUID that resolves to no user and renders as "Unknown".
        entry.UserId.Should().BeNull();
        entry.UserDisplay.Should().Be("System");
    }

    [Fact]
    public async Task An_oversized_header_value_is_truncated_before_it_is_recorded()
    {
        // Kestrel accepts a 32 KB header by default, AuditEntry.Details reaches the webhook
        // and e-mail transports, and AiInspectTransport interpolates it into an LLM prompt.
        // A compromised target holding a valid agent JWT must not be able to author that.
        var h = new Harness(rawVersion: new string('A', 30_000), targetId: Guid.NewGuid());

        await h.InvokeAsync();

        var detail = h.Audit.Events.Should().ContainSingle().Subject.Details!;
        detail.Length.Should().BeLessThan(200);
        detail.Should().Contain("30000 chars", "the operator still learns it was oversized");
        h.Body.Length.Should().BeLessThan(500);
    }

    [Fact]
    public async Task Repeat_refusals_of_the_same_skew_are_reported_once_per_window()
    {
        // A refusal is a per-target STATE, not an event stream. Without this, a fleet-wide
        // skew after a server upgrade is a sustained audit-INSERT and log flood — and the
        // subscription poller forwards every one of those rows off-premises.
        var clock = new StubClock();
        var h = new Harness(sentVersion: 3, targetId: Guid.NewGuid(), clock: clock);

        await h.InvokeAsync();
        await h.InvokeAsync();
        await h.InvokeAsync();

        h.Audit.Events.Should().HaveCount(1);
        h.Logger.Warnings.Should().HaveCount(1);
        h.Refusals.Should().Be(3, "the 426 itself is never throttled — only its reporting");

        clock.Advance(AgentContractHandshakeGate.RefusalReportInterval + TimeSpan.FromSeconds(1));
        await h.InvokeAsync();

        h.Audit.Events.Should().HaveCount(2, "the window elapsed, so the state re-reports");
    }

    [Fact]
    public async Task A_changed_skew_value_reports_immediately()
    {
        // Keyed on (target, presented value): an agent refused for v3 that is now refused
        // for v2 is a NEW situation an operator needs to see, not a repeat hiding behind the
        // previous value's window.
        var clock = new StubClock();
        var h = new Harness(sentVersion: 3, targetId: Guid.NewGuid(), clock: clock);

        await h.InvokeAsync();
        h.SetSentVersion(2);
        await h.InvokeAsync();

        h.Audit.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failed_audit_write_still_refuses_cleanly()
    {
        // The audit needs a resolved tenant database and this gate runs early, so recording
        // can fail for reasons that have nothing to do with the refusal. It must not turn
        // "upgrade your agent" into an opaque 500: a missing row is recoverable, an
        // unactionable response is not.
        var h = new Harness(sentVersion: 3, targetId: Guid.NewGuid());
        var throwing = new ThrowingAuditLog();

        await h.InvokeAsync(throwing);

        throwing.Attempted.Should().BeTrue();
        h.Context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);
        h.Body.Should().Contain("Update the agent binary");
    }

    [Fact]
    public async Task An_aborted_request_still_produces_a_complete_refusal()
    {
        // The refusal used to be written on context.RequestAborted, and the only catch was
        // filtered `when (ex is not OperationCanceledException)`. An agent that dropped the
        // transport mid-write therefore threw an OCE out of the middleware, into
        // UseSerilogRequestLogging (one Error per refusal) and UseExceptionHandler, which
        // then tried to render onto an already-aborted response.
        var h = new Harness(sentVersion: 3, targetId: Guid.NewGuid());
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        h.Context.RequestAborted = aborted.Token;

        await h.InvokeAsync();

        h.Context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);
        h.Audit.Events.Should().HaveCount(1, "the row must not be lost to the client's abort");
        h.Body.Should().NotBeEmpty();
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        internal DefaultHttpContext Context { get; } = new();
        internal RecordingAuditLog Audit { get; } = new();
        internal RecordingLogger Logger { get; } = new();
        internal bool ReachedHub { get; private set; }
        internal int Refusals { get; private set; }

        private readonly AgentContractHandshakeGate _gate;

        internal Harness(
            int? sentVersion = null,
            Guid? targetId = null,
            string? rawVersion = null,
            bool withMarker = true,
            TimeProvider? clock = null)
        {
            Context.Request.Path = "/hubs/agent/negotiate";
            Context.Response.Body = new MemoryStream();

            if (withMarker)
            {
                Context.SetEndpoint(new Endpoint(
                    _ => Task.CompletedTask,
                    new EndpointMetadataCollection(new RequiresAgentContract()),
                    "test:/hubs/agent"));
            }

            var value = rawVersion ?? sentVersion?.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(value))
            {
                Context.Request.Headers[AgentContract.VersionHeader] = value;
            }

            if (targetId is { } id)
            {
                Context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, id.ToString())], "AgentJwt"));
            }

            // ONE gate instance across a harness's invocations: the refusal-report throttle
            // is per-middleware-instance state, exactly as it is in the real pipeline
            // (UseMiddleware constructs the middleware once per pipeline build).
            _gate = new AgentContractHandshakeGate(
                _ => { ReachedHub = true; return Task.CompletedTask; },
                clock ?? TimeProvider.System,
                Logger);
        }

        internal void SetSentVersion(int version) =>
            Context.Request.Headers[AgentContract.VersionHeader] =
                version.ToString(CultureInfo.InvariantCulture);

        internal async Task InvokeAsync(IAuditLog? auditLog = null)
        {
            Context.Response.StatusCode = StatusCodes.Status200OK;
            await _gate.InvokeAsync(Context, auditLog ?? Audit);
            if (Context.Response.StatusCode == StatusCodes.Status426UpgradeRequired)
            {
                Refusals++;
            }
        }

        internal string Body =>
            System.Text.Encoding.UTF8.GetString(((MemoryStream)Context.Response.Body).ToArray());
    }

    /// <summary>
    /// Drives the gate's throttle, which measures with <c>GetTimestamp</c> /
    /// <c>GetElapsedTime</c> rather than <c>GetUtcNow</c> — a wall-clock window on a
    /// domain-joined host is disarmed by a <c>w32tm</c> step.
    /// </summary>
    private sealed class StubClock : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        internal void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }

    private sealed class RecordingLogger : ILogger<AgentContractHandshakeGate>
    {
        internal List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        internal List<Recorded> Events { get; } = [];

        public Task RecordAsync(
            string eventType, string? subjectType = null, string? subjectId = null,
            string? subjectName = null, string? details = null, Guid? userId = null,
            string? userDisplay = null, CancellationToken ct = default)
        {
            Events.Add(new Recorded(
                eventType, subjectType, subjectId, subjectName, details, userId, userDisplay));
            return Task.CompletedTask;
        }

        internal sealed record Recorded(
            string EventType, string? SubjectType, string? SubjectId, string? SubjectName,
            string? Details, Guid? UserId, string? UserDisplay);
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
