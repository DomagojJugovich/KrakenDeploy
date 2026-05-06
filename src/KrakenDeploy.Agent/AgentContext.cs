using KrakenDeploy.Agent.Identity;

namespace KrakenDeploy.Agent;

/// <summary>
/// Singleton that propagates the resolved agent identity across hosted services.
/// <see cref="RegistrationHostedService"/> calls <see cref="SetIdentity"/> once it has
/// loaded or created the identity; all other services await <see cref="IdentityReady"/>
/// before starting their work.
/// </summary>
public sealed class AgentContext
{
    private readonly TaskCompletionSource _identityReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A <see cref="Task"/> that completes when the agent identity is available.
    /// Await with a <see cref="CancellationToken"/> to respect shutdown:
    /// <code>await context.IdentityReady.WaitAsync(stoppingToken);</code>
    /// </summary>
    public Task IdentityReady => _identityReady.Task;

    /// <summary>The agent identity. Non-null after <see cref="IdentityReady"/> completes.</summary>
    public AgentIdentity? Identity { get; private set; }

    /// <summary>
    /// Transport mode assigned by the server during registration.
    /// Defaults to <c>Reverse</c> for existing identities that predate this field.
    /// </summary>
    public string TransportMode { get; private set; } = "Reverse";

    /// <summary>
    /// Called by <see cref="RegistrationHostedService"/> after a successful load or register.
    /// Subsequent calls are silently ignored.
    /// </summary>
    internal void SetIdentity(AgentIdentity identity, string transportMode = "Reverse")
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        TransportMode = transportMode;
        _identityReady.TrySetResult();
    }
}
