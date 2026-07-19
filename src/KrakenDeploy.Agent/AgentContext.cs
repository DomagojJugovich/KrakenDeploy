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

    private readonly TaskCompletionSource _registrationAccepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A <see cref="Task"/> that completes when the agent identity is available.
    /// Await with a <see cref="CancellationToken"/> to respect shutdown:
    /// <code>await context.IdentityReady.WaitAsync(stoppingToken);</code>
    /// </summary>
    public Task IdentityReady => _identityReady.Task;

    /// <summary>
    /// C6 — completes the first time the server ACCEPTS this agent's registration
    /// (contract version matched; the target is in the dispatch registry). This
    /// is the agent's post-boot health signal: <c>AgentUpdateService</c> awaits it
    /// with a timeout after a self-upgrade restart to decide whether the new
    /// binary is healthy (commit) or must be rolled back. It completes ONCE — a
    /// later reconnect does not reset it, and a refused registration never
    /// completes it (so a contract-skewed upgrade fails the health gate).
    /// </summary>
    public Task RegistrationAccepted => _registrationAccepted.Task;

    /// <summary>The agent identity. Non-null after <see cref="IdentityReady"/> completes.</summary>
    public AgentIdentity? Identity { get; private set; }

    /// <summary>
    /// Transport mode assigned by the server during registration.
    /// Defaults to <c>Reverse</c> for existing identities that predate this field.
    /// </summary>
    public string TransportMode { get; private set; } = "Reverse";

    /// <summary>
    /// Called by <see cref="RegistrationHostedService"/> after a successful load or
    /// register, and by <c>TokenRefreshHostedService</c> when the sliding refresh
    /// (A8) replaces the bearer token. Each call swaps <see cref="Identity"/> (an
    /// atomic reference write — consumers read the token through lazy accessors,
    /// and with no token rotation both old and new remain valid during the swap);
    /// the <see cref="IdentityReady"/> signal only completes once.
    /// </summary>
    internal void SetIdentity(AgentIdentity identity, string transportMode = "Reverse")
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        TransportMode = transportMode;
        _identityReady.TrySetResult();
    }

    /// <summary>
    /// C6 — called by <c>ServerLinkHostedService</c> the first time the server
    /// accepts a registration. Idempotent: only the first call matters (the
    /// health gate needs to know the agent reached a healthy state once).
    /// </summary>
    internal void SignalRegistrationAccepted() => _registrationAccepted.TrySetResult();
}
