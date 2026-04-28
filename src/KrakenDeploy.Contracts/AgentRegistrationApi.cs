namespace KrakenDeploy.Contracts;

/// <summary>Request body for <c>POST /api/agents/register</c>.</summary>
public sealed record RegisterAgentRequest(string Token);

/// <summary>Response body for <c>POST /api/agents/register</c>.</summary>
public sealed record RegisterAgentResponse(Guid AgentId, string AgentJwt);
