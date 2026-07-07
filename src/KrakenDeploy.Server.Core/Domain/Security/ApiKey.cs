using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// A per-user API credential for the <c>X-Api-Key</c> header (CLI, MCP,
/// REST). The raw token is shown exactly once at creation; only its
/// SHA-256 hash is persisted — same contract as agent registration tokens
/// (<c>TargetRegistrationService</c>) and the enrollment design
/// (docs/design-agent-enrollment-cert-auth.md §2/§10).
/// <para>
/// The key authenticates AS its owning user: the auth handler stamps the
/// owner's id as <c>ClaimTypes.NameIdentifier</c>, so authorization flows
/// through the owner's team/role grants via <c>IPermissionEvaluator</c> —
/// a key never carries rights its owner lacks. Deliberately NOT
/// <see cref="ISpaceScoped"/>: keys are platform-level rows (like
/// <c>AuditEntry</c>); <see cref="SpaceId"/> is an optional RESTRICTION,
/// not a tenancy filter.
/// </para>
/// </summary>
public class ApiKey : AuditableEntity
{
    /// <summary>Owning user (<c>ApplicationUser.Id</c>). Plain Guid + index,
    /// no FK — house convention for domain→Identity references (see
    /// <c>TeamMember</c>); cleanup happens in <c>UserService.DeleteAsync</c>.</summary>
    public Guid UserId { get; set; }

    /// <summary>Operator-facing purpose label ("CI pipeline", "Claude MCP").
    /// Unique per user so the list stays navigable.</summary>
    public string Name { get; set; } = "";

    /// <summary>Display hint — the non-secret leading portion of the token
    /// (e.g. <c>kd-4F2A9C1B</c>). Shown as <c>kd-4F2A9C1B•••••••</c> in
    /// grids so operators can match a configured key to a row without the
    /// secret ever being reproducible.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>Lowercase-hex SHA-256 of the FULL raw token. Unique — the
    /// auth handler looks the key up by recomputed hash, mirroring
    /// <c>DeploymentTarget.RegistrationKeyHash</c>.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>What the key may be used for. <see cref="ApiKeyScope.Full"/>
    /// today; <see cref="ApiKeyScope.Enroll"/> is reserved for the agent
    /// enrollment flow.</summary>
    public ApiKeyScope Scope { get; set; } = ApiKeyScope.Full;

    /// <summary>Optional single-Space restriction. Null = the key acts
    /// wherever its owner has access (Octopus behavior). Non-null = requests
    /// authenticated by this key are denied outside this Space regardless of
    /// the owner's wider grants — blast-radius control for CI keys.</summary>
    public Guid? SpaceId { get; set; }

    /// <summary>Optional expiry. Null = does not expire. An expired key
    /// fails authentication with a distinct log line (never deleted
    /// automatically — the row stays for audit).</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>Last successful authentication, written throttled (at most
    /// once per few minutes) to keep the hot path off the DB.</summary>
    public DateTimeOffset? LastUsedUtc { get; set; }

    /// <summary>Set when an operator revokes the key. Revocation is
    /// immediate and permanent; the row is kept for audit.</summary>
    public DateTimeOffset? RevokedUtc { get; set; }

    /// <summary>True when the key can still authenticate at <paramref name="now"/>.</summary>
    public bool IsActive(DateTimeOffset now) =>
        RevokedUtc is null && (ExpiresUtc is null || ExpiresUtc > now);
}

/// <summary>
/// What an <see cref="ApiKey"/> may be used for.
/// </summary>
public enum ApiKeyScope
{
    /// <summary>Full API access as the owning principal (CLI, MCP, REST).</summary>
    Full = 0,

    /// <summary>Agent-enrollment only — reserved for the proof-of-possession
    /// enrollment flow (docs/design-agent-enrollment-cert-auth.md §4): the
    /// key may create/enroll targets but is refused on every other surface.
    /// Not issuable from the UI until that flow ships.</summary>
    Enroll = 1,
}
