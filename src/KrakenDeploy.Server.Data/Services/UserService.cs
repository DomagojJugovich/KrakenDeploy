using KrakenDeploy.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Application user management: list, invite (create with temp password),
/// delete. Wraps <see cref="UserManager{TUser}"/> so callers don't need to
/// know Identity internals.
/// </summary>
public class UserService(
    UserManager<ApplicationUser> userManager,
    KrakenDbContext db)
{
    public Task<List<ApplicationUser>> GetAllAsync(CancellationToken ct = default)
        => userManager.Users
            .OrderBy(u => u.Email)
            .ToListAsync(ct);

    public Task<ApplicationUser?> GetAsync(Guid id, CancellationToken ct = default)
        => userManager.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    /// <summary>
    /// Creates a new user with a generated temporary password.
    /// Returns the created user + the plain-text temp password so the admin
    /// can communicate it to the new user. The user should change it on first
    /// login (enforced by UI convention; not technically required here).
    /// </summary>
    public async Task<(ApplicationUser User, string TempPassword)> InviteAsync(
        string email,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var existing = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException($"A user with email '{email}' already exists.");
        }

        var tempPassword = GenerateTempPassword();

        var user = new ApplicationUser
        {
            UserName       = email,
            Email          = email,
            EmailConfirmed = true,
        };

        var result = await userManager.CreateAsync(user, tempPassword).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create user: {errors}");
        }

        return (user, tempPassword);
    }

    /// <summary>
    /// Deletes the user and removes all their team-membership rows.
    /// Returns false if the user was not found.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        // Remove team memberships first (no cascade set up for Identity rows).
        var memberships = await db.TeamMembers
            .Where(m => m.UserId == id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.TeamMembers.RemoveRange(memberships);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var result = await userManager.DeleteAsync(user).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>Returns the IDs of teams the user explicitly belongs to.</summary>
    public Task<List<Guid>> GetTeamIdsAsync(Guid userId, CancellationToken ct = default)
        => db.TeamMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.TeamId)
            .ToListAsync(ct);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string GenerateTempPassword()
    {
        // Satisfies the default policy: 10+ chars, upper, lower, digit,
        // no non-alphanumeric required (policy = false).
        var raw = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        // Splice in "A1" to guarantee upper+digit independent of base64 output.
        return raw[..10] + "A1";
    }
}
