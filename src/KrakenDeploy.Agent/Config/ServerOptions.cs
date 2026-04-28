namespace KrakenDeploy.Agent.Config;

/// <summary>
/// Configuration for the KrakenDeploy server this agent connects to.
/// Bound to the "Server" configuration section.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Base URL of the KrakenDeploy server (e.g. "https://deploy.example.com").</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// One-time registration token generated in the Targets wizard.
    /// Consumed on first successful registration and ignored thereafter
    /// (the server invalidates it server-side).
    /// </summary>
    public string? RegistrationToken { get; set; }
}
