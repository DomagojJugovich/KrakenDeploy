using System.Collections.Concurrent;
using FluentAssertions;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for M11.E.7 — <see cref="AdhocDispatcher"/>: the structural
/// enforcer of the frozen-target-set invariant (M11.E.15a / M11.E.17). Uses
/// the real <see cref="PendingAdhocRegistry"/> + <see cref="InMemoryAgentConnectionRegistry"/>;
/// a fake <see cref="IAdhocAgentPusher"/> records every push and resolves the
/// registry slot with a canned result so the dispatcher's WhenAll completes
/// without needing a real agent in the loop.
/// </summary>
public sealed class AdhocDispatcherTests
{
    private static readonly string[] ExpectedConnAB = ["conn-a", "conn-b"];

    private static AdhocSession SessionWithTargets(params Guid[] targetIds)
        => new()
        {
            Id                  = Guid.NewGuid(),
            Prompt              = "test prompt",
            FrozenTargetSetJson = "[" + string.Join(",", targetIds.Select(g => $"\"{g}\"")) + "]",
            CreatedByDisplay    = "ops@test",
        };

    private static AdhocIteration SignedIteration(string script = "Get-Date", string sig = "AAAA")
        => new()
        {
            Id              = Guid.NewGuid(),
            IterNumber      = 1,
            CreatedUtc      = DateTimeOffset.UtcNow,
            GeneratedScript = script,
            ScriptSignature = sig,
        };

    [Fact]
    public async Task Dispatch_throws_when_iteration_has_no_signature()
    {
        var connections = new InMemoryAgentConnectionRegistry();
        var pending = new PendingAdhocRegistry();
        var dispatcher = new AdhocDispatcher(
            connections, pending, new RecordingPusher(connections, pending),
            NullLogger<AdhocDispatcher>.Instance);

        var session = SessionWithTargets(Guid.NewGuid());
        var iteration = SignedIteration();
        iteration.ScriptSignature = null;

        var act = async () => await dispatcher.DispatchAsync(session, iteration, Guid.Empty, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no signature*");
    }

    [Fact]
    public async Task Dispatch_returns_empty_when_frozen_set_is_empty()
    {
        var connections = new InMemoryAgentConnectionRegistry();
        var pending = new PendingAdhocRegistry();
        var dispatcher = new AdhocDispatcher(
            connections, pending, new RecordingPusher(connections, pending),
            NullLogger<AdhocDispatcher>.Instance);

        var results = await dispatcher.DispatchAsync(
            SessionWithTargets(), SignedIteration(), Guid.Empty, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatch_fans_out_to_exactly_the_frozen_target_set_no_more_no_less()
    {
        // M11.E.17 invariant — the dispatcher MUST NOT route to any target id
        // that isn't in FrozenTargetSetJson, even when more targets are
        // registered as connected. The frozen set is the entire dispatch
        // policy; there's no input that lets a caller widen it.
        var inSetA = Guid.NewGuid();
        var inSetB = Guid.NewGuid();
        var outOfSet = Guid.NewGuid();

        var connections = new InMemoryAgentConnectionRegistry();
        connections.Add("conn-a", inSetA);
        connections.Add("conn-b", inSetB);
        connections.Add("conn-extra", outOfSet); // intentionally NOT in the frozen set

        var pending = new PendingAdhocRegistry();
        var pusher = new RecordingPusher(connections, pending);
        var dispatcher = new AdhocDispatcher(connections, pending, pusher,
            NullLogger<AdhocDispatcher>.Instance);

        var results = await dispatcher.DispatchAsync(
            SessionWithTargets(inSetA, inSetB), SignedIteration(), Guid.Empty, CancellationToken.None);

        results.Should().HaveCount(2);
        var expectedConnections = ExpectedConnAB;
        pusher.PushedConnections.Should().BeEquivalentTo(expectedConnections);
        pusher.PushedConnections.Should().NotContain("conn-extra",
            "the dispatcher MUST NOT push to a target outside FrozenTargetSetJson");
    }

    [Fact]
    public async Task Dispatch_short_circuits_offline_target_with_AgentError()
    {
        var online = Guid.NewGuid();
        var offline = Guid.NewGuid();

        var connections = new InMemoryAgentConnectionRegistry();
        connections.Add("conn-online", online); // offline has no connection

        var pending = new PendingAdhocRegistry();
        var pusher = new RecordingPusher(connections, pending);
        var dispatcher = new AdhocDispatcher(connections, pending, pusher,
            NullLogger<AdhocDispatcher>.Instance);

        var results = await dispatcher.DispatchAsync(
            SessionWithTargets(online, offline), SignedIteration(), Guid.Empty, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Where(r => r.Result.AgentError is not null).Should().ContainSingle(
            "the offline target gets an immediate AgentError; the online target reports cleanly");
        results.Single(r => r.Result.AgentError is not null).TargetId.Should().Be(offline);
        pusher.PushedConnections.Should().ContainSingle().And.Contain("conn-online");
    }

    [Fact]
    public async Task Dispatch_collates_per_target_results_with_correct_session_and_iter_binding()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var connections = new InMemoryAgentConnectionRegistry();
        connections.Add("ca", a);
        connections.Add("cb", b);

        var pending = new PendingAdhocRegistry();
        var pusher = new RecordingPusher(connections, pending,
            resultBy: (cmd, _) => new AdhocScriptResult(
                cmd.SessionId, cmd.IterNumber, ExitCode: 0,
                Stdout: $"out-{cmd.IterNumber}", Stderr: "", AgentError: null));

        var dispatcher = new AdhocDispatcher(connections, pending, pusher,
            NullLogger<AdhocDispatcher>.Instance);
        var session = SessionWithTargets(a, b);
        var iteration = SignedIteration(script: "Get-Date", sig: "sig-xyz");

        var results = await dispatcher.DispatchAsync(session, iteration, Guid.Empty, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r =>
        {
            r.Result.SessionId.Should().Be(session.Id);
            r.Result.IterNumber.Should().Be(iteration.IterNumber);
            r.Result.Success.Should().BeTrue();
        });
        results.Select(r => r.TargetId).Should().BeEquivalentTo(new[] { a, b });
    }

    [Fact]
    public async Task Dispatch_resolves_with_AgentError_when_push_throws()
    {
        var t = Guid.NewGuid();
        var connections = new InMemoryAgentConnectionRegistry();
        connections.Add("c", t);

        var dispatcher = new AdhocDispatcher(connections, new PendingAdhocRegistry(),
            new ThrowingPusher(), NullLogger<AdhocDispatcher>.Instance);

        var results = await dispatcher.DispatchAsync(
            SessionWithTargets(t), SignedIteration(), Guid.Empty, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].TargetId.Should().Be(t);
        results[0].Result.AgentError.Should().NotBeNull().And.Contain("Push failed");
    }

    [Fact]
    public async Task Dispatch_throws_on_malformed_frozen_target_json()
    {
        var connections = new InMemoryAgentConnectionRegistry();
        var pending = new PendingAdhocRegistry();
        var dispatcher = new AdhocDispatcher(
            connections, pending, new RecordingPusher(connections, pending),
            NullLogger<AdhocDispatcher>.Instance);

        var session = new AdhocSession
        {
            Id                  = Guid.NewGuid(),
            Prompt              = "x",
            FrozenTargetSetJson = "not-json{",
            CreatedByDisplay    = "ops@test",
        };

        var act = async () => await dispatcher.DispatchAsync(
            session, SignedIteration(), Guid.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*FrozenTargetSetJson*");
    }

    [Fact]
    public async Task Dispatch_blocks_target_whose_connection_belongs_to_another_account()
    {
        // P3-8 Phase 5 — cross-account guard. A live connection recorded under account A
        // must not receive a script dispatched under account B.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var target   = Guid.NewGuid();

        var connections = new InMemoryAgentConnectionRegistry();
        connections.Add("conn-a", target, accountA); // connection belongs to account A

        var pending = new PendingAdhocRegistry();
        var pusher = new RecordingPusher(connections, pending);
        var dispatcher = new AdhocDispatcher(connections, pending, pusher,
            NullLogger<AdhocDispatcher>.Instance);

        var results = await dispatcher.DispatchAsync(
            SessionWithTargets(target), SignedIteration(), accountB, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].TargetId.Should().Be(target);
        results[0].Result.AgentError.Should().NotBeNull().And.Contain("Cross-account");
        pusher.PushedConnections.Should().BeEmpty(
            "a cross-account target must never receive the script");
    }

    [Fact]
    public async Task Dispatch_allows_target_when_dispatch_account_matches_connection()
    {
        var account = Guid.NewGuid();
        var target  = Guid.NewGuid();

        var connections = new InMemoryAgentConnectionRegistry();
        connections.Add("conn-a", target, account);

        var pending = new PendingAdhocRegistry();
        var pusher = new RecordingPusher(connections, pending);
        var dispatcher = new AdhocDispatcher(connections, pending, pusher,
            NullLogger<AdhocDispatcher>.Instance);

        var results = await dispatcher.DispatchAsync(
            SessionWithTargets(target), SignedIteration(), account, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Result.AgentError.Should().BeNull();
        pusher.PushedConnections.Should().ContainSingle().And.Contain("conn-a");
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Records every push the dispatcher makes and resolves the matching
    /// pending-adhoc registry slot with a canned result, simulating the agent
    /// reporting back. Resolves <c>connectionId → targetId</c> via the same
    /// <see cref="IAgentConnectionRegistry"/> the dispatcher uses, so the
    /// test wiring stays minimal.
    /// </summary>
    private sealed class RecordingPusher(
        IAgentConnectionRegistry connections,
        IPendingAdhocRegistry pending,
        Func<AdhocScriptCommand, Guid, AdhocScriptResult>? resultBy = null) : IAdhocAgentPusher
    {
        private readonly ConcurrentBag<string> _pushed = [];

        public IReadOnlyCollection<string> PushedConnections => _pushed.ToArray();

        public Task PushAsync(string connectionId, AdhocScriptCommand command, CancellationToken ct)
        {
            _pushed.Add(connectionId);

            var targetId = connections.GetTargetId(connectionId)
                ?? throw new InvalidOperationException(
                    $"Test fake: connection '{connectionId}' was pushed to but has no " +
                    "target id registered — register via connections.Add(conn, target).");

            var result = resultBy?.Invoke(command, targetId)
                ?? new AdhocScriptResult(
                    command.SessionId, command.IterNumber, ExitCode: 0,
                    Stdout: "", Stderr: "", AgentError: null);

            pending.TryResolve(command.SessionId, command.IterNumber, targetId, result);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPusher : IAdhocAgentPusher
    {
        public Task PushAsync(string connectionId, AdhocScriptCommand command, CancellationToken ct)
            => Task.FromException(new InvalidOperationException("simulated push failure"));
    }
}
