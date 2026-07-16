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

    /// <summary>
    /// A8/T1-12: dev-only override permitting a cleartext <c>http://</c>
    /// <see cref="Url"/> (which also enables cleartext HTTP/2 for the gRPC
    /// channels). Defaults false — https is required. Set true ONLY for local
    /// development against an http server; NEVER in production.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// B8 — optional dedicated base URL for the agent's gRPC channels
    /// (package / step-package download, artifact upload). Needed when
    /// <see cref="Url"/> is CLEARTEXT http: without TLS there is no ALPN, and
    /// Kestrel cannot serve HTTP/1.1 (REST + SignalR) and HTTP/2 (gRPC) on
    /// one plaintext port — the server exposes a second, Http2-only h2c
    /// endpoint and this points at it (e.g. <c>http://server:5081</c>).
    /// Irrelevant over https (ALPN negotiates both on one port). Defaults to
    /// <see cref="Url"/>.
    /// </summary>
    public string? GrpcUrl { get; set; }
}
