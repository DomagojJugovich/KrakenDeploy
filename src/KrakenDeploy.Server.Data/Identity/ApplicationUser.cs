using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.AspNetCore.Identity;

namespace KrakenDeploy.Server.Data.Identity;

/// <summary>
/// Application user backed by ASP.NET Identity. Identity tables are stored in
/// <see cref="KrakenDbContext"/> with KrakenDeploy-friendly snake_case names
/// (see <see cref="KrakenDeploy.Server.Data.Configurations.IdentityConfiguration"/>).
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        Id = Guid.CreateVersion7();
    }

    /// <summary>
    /// Human vs. ServiceAccount discriminator. Defaults to <see cref="UserKind.Human"/>
    /// so existing rows (pre-migration) and new password / OIDC sign-ups are
    /// treated as people. Service accounts authenticate ONLY via API keys —
    /// the sign-in flow refuses password / OIDC for them.
    /// </summary>
    public UserKind Kind { get; set; } = UserKind.Human;

    /// <summary>
    /// The <see cref="KrakenDeploy.Server.Core.Domain.Security.IdentityProvider"/> used
    /// for the most recent OIDC sign-in.  Null for local (password) accounts.
    /// </summary>
    public Guid? LastOidcProviderId { get; set; }

    /// <summary>
    /// Pipe-separated external group claim values from the most recent OIDC sign-in
    /// (e.g. "Engineering|Deployers"). Stored here rather than in the cookie so they
    /// survive Identity security-stamp refreshes.  Null for local accounts.
    /// </summary>
    public string? ExternalGroups { get; set; }

    /// <summary>
    /// Radzen theme name persisted across sessions (e.g. "material", "material-dark").
    /// Null = use the application default.
    /// </summary>
    public string? Theme { get; set; }
}
