namespace KrakenDeploy.Contracts;

/// <summary>Request body for <c>POST /api/agents/register</c>.</summary>
public sealed record RegisterAgentRequest(string Token);

/// <summary>Response body for <c>POST /api/agents/register</c>.</summary>
public sealed record RegisterAgentResponse(Guid AgentId, string AgentJwt, string TransportMode);

/// <summary>
/// Custom claim names carried by the agent bearer token. Shared between the
/// issuer (<c>AgentJwtService</c>, in the server app) and the validator
/// (<c>AgentTokenValidator</c>, in the data layer) so the wire contract has a
/// single source of truth.
/// </summary>
public static class AgentTokenClaims
{
    /// <summary>
    /// A8/T1-12: the target's <c>AgentTokenVersion</c> at issue time. The server
    /// rejects the token if this no longer equals the target's current version
    /// (per-target revocation without deleting the target or rotating the key).
    /// </summary>
    public const string TokenVersion = "atv";
}
