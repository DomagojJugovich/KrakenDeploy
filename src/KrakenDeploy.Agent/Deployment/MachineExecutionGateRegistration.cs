using KrakenDeploy.Agent.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// F5 — DI registration for <see cref="MachineExecutionGate"/>, extracted from
/// <c>Program.cs</c> so it is REACHABLE FROM TESTS. The agent's composition root lives in
/// top-level statements inside a local function, so a test could only ever re-create the
/// registration by hand — and a hand-copied replica cannot fail when the real one changes.
/// <c>Program.cs</c> claimed the lifetime was "pinned by MachineExecutionGateSharingTests"
/// while that test asserted its own copy, so switching the real registration to
/// <c>AddTransient</c> (which silently disables serialization between all three consumers)
/// would have left the suite green.
/// </summary>
internal static class MachineExecutionGateRegistration
{
    /// <summary>
    /// Registers the machine execution gate as a process-wide SINGLETON — required, because
    /// it is shared by the deployment path (F2), the ad-hoc path (F2) and the self-upgrade
    /// swap window (F5), and a per-consumer instance would mean none of them excludes the
    /// others. Also reports a clamped <c>Agent:MaxConcurrentSharedWork</c>: the gate clamps
    /// to <c>[1, MaxAllowedSharedHolders]</c> deliberately (a 0 would make every shared
    /// acquisition unsatisfiable, an absurdly large one reinstates the unbounded fan-out the
    /// cap exists to stop), but clamping SILENTLY left an operator who asked for 200 running
    /// at 64, and one who typo'd 0 running fully serialized, with nothing in any log and no
    /// surface reporting the effective value.
    /// </summary>
    internal static IServiceCollection AddMachineExecutionGate(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var requested = sp.GetRequiredService<IOptions<AgentConfig>>()
                .Value.MaxConcurrentSharedWork;
            var gate = new MachineExecutionGate { MaxSharedHolders = requested };

            if (gate.MaxSharedHolders != requested)
            {
                sp.GetRequiredService<ILogger<MachineExecutionGate>>().LogWarning(
                    "Agent:MaxConcurrentSharedWork = {Requested} is outside the supported " +
                    "range [1, {Ceiling}]. The effective cap on co-running approved ad-hoc " +
                    "scripts on this machine is {Effective}; further ones queue.",
                    requested, MachineExecutionGate.MaxAllowedSharedHolders,
                    gate.MaxSharedHolders);
            }

            return gate;
        });

        return services;
    }
}
