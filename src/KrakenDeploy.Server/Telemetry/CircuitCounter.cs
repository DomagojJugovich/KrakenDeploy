using Microsoft.AspNetCore.Components.Server.Circuits;

namespace KrakenDeploy.Server.Telemetry;

/// <summary>
/// Counts live Blazor Server circuits on this instance — the "active circuit
/// count" a slot reports for blue-green draining
/// (docs/blue-green-slot-deployment.md §5/§9). Registered once as a singleton
/// and surfaced to the circuit infrastructure via the <see cref="CircuitHandler"/>
/// service registration, so every circuit shares this one counter.
/// </summary>
public sealed class CircuitCounter : CircuitHandler
{
    private int _count;

    public int ActiveCircuits => Volatile.Read(ref _count);

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Decrement(ref _count);
        return Task.CompletedTask;
    }
}
