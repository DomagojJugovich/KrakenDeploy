using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Locks <see cref="DeploymentStatusExtensions"/>'s two representations of terminality
/// against each other. The class exists because this classification "had already diverged
/// between call sites" once; exposing it a second way — as an array, for the query provider,
/// since EF cannot translate the method — reintroduces exactly that risk unless something
/// enforces the equivalence over EVERY enum member, including ones added later.
/// <para>
/// The consumer that makes this load-bearing is F5's agent swap gate: it asks
/// "is any non-terminal task assigned to this target?" as <c>!Terminal.Contains(status)</c>
/// and must fail CLOSED, because the agent reads a clear "idle" as licence to replace its
/// whole install directory and exit. A status missing from <c>Terminal</c> would merely make
/// it over-cautious; a status wrongly PRESENT would let a swap run mid-plan.
/// </para>
/// </summary>
public class DeploymentStatusClassificationTests
{
    [Fact]
    public void Terminal_array_and_IsTerminal_agree_on_every_status()
    {
        foreach (var status in Enum.GetValues<DeploymentStatus>())
        {
            DeploymentStatusExtensions.Terminal.Contains(status)
                .Should().Be(status.IsTerminal(),
                    $"the array and the predicate must classify {status} identically — a new " +
                    "status added to one and not the other silently changes the agent " +
                    "swap gate's fail-closed answer");
        }
    }

    [Fact]
    public void InFlightAfterClaim_is_the_non_terminal_states_except_Queued()
    {
        // The narrower F1 set. Pinned here too so nobody "simplifies" the swap gate to use
        // it as the complement of Terminal — it deliberately excludes Queued, and a Queued
        // task can be dispatched at any moment.
        var nonTerminalExceptQueued = Enum.GetValues<DeploymentStatus>()
            .Where(s => !s.IsTerminal() && s != DeploymentStatus.Queued)
            .ToArray();

        DeploymentStatusExtensions.InFlightAfterClaim
            .Should().BeEquivalentTo(nonTerminalExceptQueued);

        DeploymentStatusExtensions.InFlightAfterClaim
            .Should().NotContain(DeploymentStatus.Queued,
                "Queued is pre-claim: it holds no F1 slot, but it is NOT idle");
    }
}
