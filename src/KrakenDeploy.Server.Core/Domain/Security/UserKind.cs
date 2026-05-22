namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Classifies an <c>ApplicationUser</c> row by how the identity is meant
/// to authenticate. The discriminator is read by sign-in flows and audit
/// rendering — it does NOT carry permissions by itself (those come from
/// team membership + role assignments, same as for humans).
/// </summary>
public enum UserKind
{
    /// <summary>
    /// A real person. May sign in via local password, OIDC, or both.
    /// Default for any user created via the Invite flow or OIDC JIT
    /// provisioning. Failed password attempts trigger lockout; the email
    /// claim is honoured for display.
    /// </summary>
    Human = 0,

    /// <summary>
    /// A bot, automation, or scheduled-job identity. Authenticates ONLY
    /// via API keys (no password, no SSO claim mapping — granting
    /// interactive sign-in to a service account is a misconfiguration
    /// worth blocking at the boundary).
    /// <para>
    /// Audit rows attributed to service accounts render with a "service"
    /// chip so operators can distinguish "Hangfire ran the retention job"
    /// from "someone logged in and clicked the button".
    /// </para>
    /// </summary>
    ServiceAccount = 1,
}
