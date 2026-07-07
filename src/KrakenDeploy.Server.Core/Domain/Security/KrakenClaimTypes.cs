namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// KrakenDeploy-specific claim types stamped by the API-key authentication
/// handler (M13.C.4). Kept in Core so the permission evaluator (Server.Data)
/// and the MCP surface can read them without referencing the Server project.
/// </summary>
public static class KrakenClaimTypes
{
    /// <summary>The authenticating <c>ApiKey.Id</c> — present iff the request
    /// authenticated via <c>X-Api-Key</c>. Diagnostics + per-key gates.</summary>
    public const string ApiKeyId = "kraken:apikey_id";

    /// <summary>The key's single-Space restriction (<c>ApiKey.SpaceId</c>),
    /// present only when the key is restricted. <c>PermissionEvaluator</c>
    /// fails every permission check whose scope falls outside this Space —
    /// including system-wide checks — regardless of the owner's wider grants.</summary>
    public const string ApiKeySpace = "kraken:apikey_space";
}

/// <summary>
/// Authentication scheme names shared across projects (the MCP gate
/// middleware must trigger the API-key scheme by name without referencing
/// the Server project, which owns the handler).
/// </summary>
public static class KrakenAuthSchemes
{
    /// <summary>The per-user X-Api-Key scheme
    /// (<c>ApiKeyAuthenticationHandler</c> in the Server project).</summary>
    public const string ApiKey = "ApiKey";
}
