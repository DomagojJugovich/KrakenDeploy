using FluentAssertions;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for M11.E.7 — <see cref="PendingAdhocRegistry"/>.
/// </summary>
public sealed class PendingAdhocRegistryTests
{
    [Fact]
    public async Task Register_then_resolve_completes_the_TCS_with_the_result()
    {
        var registry = new PendingAdhocRegistry();
        var sessionId = Guid.NewGuid();
        var targetId  = Guid.NewGuid();
        var tcs = new TaskCompletionSource<AdhocScriptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        registry.Register(sessionId, iterNumber: 1, targetId, tcs);

        var result = new AdhocScriptResult(sessionId, 1, ExitCode: 0,
            Stdout: "ok", Stderr: "", AgentError: null);
        var resolved = registry.TryResolve(sessionId, 1, targetId, result);

        resolved.Should().BeTrue();
        (await tcs.Task).Should().BeSameAs(result);
    }

    [Fact]
    public void TryResolve_returns_false_when_no_slot_was_registered()
    {
        var registry = new PendingAdhocRegistry();
        var result = new AdhocScriptResult(Guid.NewGuid(), 1, 0, "", "", null);
        registry.TryResolve(Guid.NewGuid(), 1, Guid.NewGuid(), result).Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_resolves_a_pending_TCS_with_AgentError()
    {
        var registry = new PendingAdhocRegistry();
        var sessionId = Guid.NewGuid();
        var targetId  = Guid.NewGuid();
        var tcs = new TaskCompletionSource<AdhocScriptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        registry.Register(sessionId, 1, targetId, tcs);
        registry.Cancel(sessionId, 1, targetId, "test-reason");

        var result = await tcs.Task;
        result.AgentError.Should().Be("test-reason");
        result.ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task Iter_number_is_part_of_the_slot_key()
    {
        // Distinct iterations on the same (session, target) are independent
        // slots — resolving iter 2 must not satisfy a pending iter 1.
        var registry = new PendingAdhocRegistry();
        var sessionId = Guid.NewGuid();
        var targetId  = Guid.NewGuid();

        var tcsIter1 = new TaskCompletionSource<AdhocScriptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(sessionId, 1, targetId, tcsIter1);

        var iter2Result = new AdhocScriptResult(sessionId, 2, 0, "", "", null);
        registry.TryResolve(sessionId, 2, targetId, iter2Result).Should().BeFalse();
        tcsIter1.Task.IsCompleted.Should().BeFalse(
            "iter 1's slot must not be satisfied by an iter 2 result");

        registry.Cancel(sessionId, 1, targetId, "cleanup");
        await tcsIter1.Task; // cleanup
    }
}
