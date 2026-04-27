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
}
