using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Configuration for an external authentication provider (OIDC / SAML / LDAP).
/// Coexists with the built-in local-account flow — local accounts always work
/// for bootstrap and break-glass admin even when one or more IdPs are
/// configured (see TASKS.md M10 auth section).
/// <para>
/// The login page renders a "Sign in with {Name}" button per enabled provider
/// in addition to (or instead of, if the admin has restricted local login)
/// the email + password form.
/// </para>
/// </summary>
public class IdentityProvider : AuditableEntity
{
    /// <summary>Display name shown on the "Sign in with …" button.</summary>
    public required string Name { get; set; }

    /// <summary>Provider category — drives default endpoint shapes and group-claim parsing.</summary>
    public IdentityProviderType Type { get; set; }

    /// <summary>OIDC issuer URL (e.g. <c>https://login.microsoftonline.com/{tid}/v2.0</c>).</summary>
    public string? Authority { get; set; }

    /// <summary>OIDC client_id registered with the provider.</summary>
    public string? ClientId { get; set; }

    /// <summary>OIDC client_secret encrypted with the server master key.</summary>
    public string? ClientSecretEncrypted { get; set; }

    /// <summary>Space-separated list of OAuth scopes (e.g. <c>"openid profile email groups"</c>).</summary>
    public string Scopes { get; set; } = "openid profile email";

    /// <summary>
    /// JSON path or claim name carrying group memberships in the IdP's token.
    /// Defaults: <c>"groups"</c> for Entra/Azure AD, <c>"groups"</c> for Okta,
    /// <c>"memberOf"</c> for ADFS / generic OIDC.
    /// </summary>
    public string GroupClaimName { get; set; } = "groups";

    /// <summary>
    /// When <c>true</c>, the first sign-in for an unknown email auto-creates an
    /// <c>ApplicationUser</c> with a linked external login. When <c>false</c>,
    /// only pre-invited users can sign in.
    /// </summary>
    public bool AutoProvisionUsers { get; set; } = true;

    /// <summary>
    /// Optional <see cref="Team"/> JIT-provisioned users are auto-added to.
    /// Combined with the team's role assignments, this lets admins drop a
    /// new corp employee straight into a usable working state.
    /// </summary>
    public Guid? DefaultTeamId { get; set; }
    public Team? DefaultTeam { get; set; }

    /// <summary>True = visible on the login page; false = disabled but config retained.</summary>
    public bool IsEnabled { get; set; } = true;

    // icon_url and sort_order were dropped in the 2026-07 schema cleanup: neither was
    // ever written (no dialog field, no service assignment), and login-page ordering
    // now sorts by Name (which is what the sort_order tie-break already fell back to).
}

/// <summary>Family of external auth provider — drives endpoint defaults and group-claim parsing.</summary>
public enum IdentityProviderType
{
    GenericOidc = 0,
    AzureAd     = 1,
    Okta        = 2,
    Google      = 3,
    Auth0       = 4,
    Ldap        = 10,
    ActiveDirectoryFederation = 11,
}
