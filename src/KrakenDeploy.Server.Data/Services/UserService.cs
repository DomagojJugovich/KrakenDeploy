using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.Security;
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
    IDbContextFactory<KrakenDbContext> dbFactory,
    ILicenseGate licenseGate)
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

        // License quota gate. We count via UserManager.Users (no Space filter
        // applies to Identity rows) so the count is naturally global. Done
        // after the duplicate-email check so the operator gets the more
        // specific error first if both apply.
        var currentUsers = await userManager.Users.CountAsync(ct).ConfigureAwait(false);
        var refusal = licenseGate.CheckUserCreate(currentUsers);
        if (refusal is not null)
        {
            throw new LicenseLimitException(refusal);
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
    /// Creates a new service-account identity. Service accounts are
    /// <see cref="UserKind.ServiceAccount"/> users that authenticate ONLY
    /// via API keys (no password set, OIDC blocked at the registrar). The
    /// display name becomes the human-readable label; the username is a
    /// slug-derived "svc-{slug}" so it's distinguishable from human emails
    /// in audit rows and team-membership lists.
    /// </summary>
    public async Task<ApplicationUser> CreateServiceAccountAsync(
        string displayName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var slug = ProjectService.Slugify(displayName);
        if (string.IsNullOrEmpty(slug))
        {
            throw new InvalidOperationException(
                "Display name must contain at least one alphanumeric character.");
        }
        var userName = $"svc-{slug}";

        var existing = await userManager.FindByNameAsync(userName).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"A service account named '{displayName}' already exists.");
        }

        // Service accounts count against MaxUsers — they consume identity
        // rows and could be used to escape the cap otherwise.
        var currentUsers = await userManager.Users.CountAsync(ct).ConfigureAwait(false);
        var refusal = licenseGate.CheckUserCreate(currentUsers);
        if (refusal is not null)
        {
            throw new LicenseLimitException(refusal);
        }

        var user = new ApplicationUser
        {
            UserName       = userName,
            // Synthetic local-only email so display fallbacks ("u.Email")
            // still produce something usable. The `.kraken.local` suffix
            // is a deliberate non-deliverable domain so nobody emails it.
            Email          = $"{userName}@kraken.local",
            EmailConfirmed = true,
            Kind           = UserKind.ServiceAccount,
        };

        // CreateAsync without a password — UserManager.HasPasswordAsync
        // returns false, so the password sign-in flow naturally refuses
        // (returning "Invalid login attempt" with no lockout counter).
        // Pair this with the OIDC-registrar refuse-on-Kind check below.
        var result = await userManager.CreateAsync(user).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException(
                $"Failed to create service account: {errors}");
        }

        return user;
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
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var memberships = await db.TeamMembers
            .Where(m => m.UserId == id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.TeamMembers.RemoveRange(memberships);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // API keys authenticate AS this user — they must die with the account
        // (Users.razor's delete confirm explicitly promises this). Same
        // manual-cleanup convention as team memberships above.
        await db.ApiKeys
            .Where(k => k.UserId == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var result = await userManager.DeleteAsync(user).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>
    /// Enables or disables an account (A7/T1-13). Disabling refuses future
    /// sign-ins (checked in the login + OIDC paths) AND bumps the security stamp
    /// so any live session/circuit is revoked at the next revalidation interval.
    /// Idempotent; returns false if the user was not found.
    /// </summary>
    public async Task<bool> SetDisabledAsync(Guid id, bool disabled, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }
        if (user.IsDisabled == disabled)
        {
            return true; // no-op
        }

        user.IsDisabled = disabled;
        var update = await userManager.UpdateAsync(user).ConfigureAwait(false);
        if (!update.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to update user: " + string.Join(", ", update.Errors.Select(e => e.Description)));
        }

        // Revoke live sessions/circuits on disable. (Re-enabling doesn't need a
        // bump — the account simply becomes usable again on next sign-in.)
        if (disabled)
        {
            await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Admin password reset (A7/T1-13). Sets a freshly generated temporary
    /// password and returns it so the admin can convey it; the user should
    /// change it on next sign-in. Replacing the password hash bumps the security
    /// stamp, so the target's other active sessions are revoked at the next
    /// revalidation interval. Refuses service accounts (they have no password).
    /// Returns null if the user was not found.
    /// </summary>
    public async Task<string?> ResetPasswordAsync(Guid id, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }
        if (user.Kind == UserKind.ServiceAccount)
        {
            throw new InvalidOperationException(
                "Service accounts authenticate via API keys and have no password to reset.");
        }

        var tempPassword = GenerateTempPassword();

        // Remove-then-add (no token providers required). Both operations update
        // the password hash, which regenerates the security stamp -> live
        // sessions are revoked at the next revalidation interval.
        if (await userManager.HasPasswordAsync(user).ConfigureAwait(false))
        {
            var removed = await userManager.RemovePasswordAsync(user).ConfigureAwait(false);
            if (!removed.Succeeded)
            {
                throw new InvalidOperationException(
                    "Failed to clear password: " + string.Join(", ", removed.Errors.Select(e => e.Description)));
            }
        }

        var added = await userManager.AddPasswordAsync(user, tempPassword).ConfigureAwait(false);
        if (!added.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to set password: " + string.Join(", ", added.Errors.Select(e => e.Description)));
        }
        return tempPassword;
    }

    /// <summary>
    /// Edits a user's profile (WP5 item 4): the optional display name and the
    /// email. Mirrors <see cref="SetDisabledAsync"/>'s UserManager-based pattern.
    /// For humans (whose username equals the email) the username is kept in step
    /// with a changed email so the sign-in identity tracks the new address. All
    /// field changes are persisted in a SINGLE <see cref="UserManager{TUser}.UpdateAsync"/>
    /// so a failure can't leave the username and email out of sync. Service accounts
    /// keep their synthetic <c>@kraken.local</c> email and <c>svc-*</c> username —
    /// only their display name is editable. Returns <c>false</c> if the user was
    /// not found.
    /// </summary>
    public async Task<bool> UpdateProfileAsync(
        Guid id, string? displayName, string? email, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

        if (user.Kind != UserKind.ServiceAccount &&
            !string.IsNullOrWhiteSpace(email) &&
            !string.Equals(email.Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
        {
            var newEmail = email.Trim();

            var existing = await userManager.FindByEmailAsync(newEmail).ConfigureAwait(false);
            if (existing is not null && existing.Id != user.Id)
            {
                throw new InvalidOperationException($"A user with email '{newEmail}' already exists.");
            }

            // Humans sign in with username == email; keep them in step so the
            // account stays reachable at the new address. Set the normalized forms
            // directly and persist with the single UpdateAsync below — using
            // SetUserNameAsync + SetEmailAsync would write in two separate saves,
            // leaving the account inconsistent if the second one failed. The
            // UserValidator that runs inside UpdateAsync still enforces uniqueness.
            user.UserName = newEmail;
            user.NormalizedUserName = userManager.NormalizeName(newEmail);
            user.Email = newEmail;
            user.NormalizedEmail = userManager.NormalizeEmail(newEmail);
            // A new address must be re-confirmed; keep the account usable by
            // marking it confirmed (parity with InviteAsync, which seeds confirmed).
            user.EmailConfirmed = true;
        }

        var update = await userManager.UpdateAsync(user).ConfigureAwait(false);
        if (!update.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to update user: " + string.Join(", ", update.Errors.Select(e => e.Description)));
        }
        return true;
    }

    /// <summary>Persist the user's Radzen theme choice. No-op if user not found.</summary>
    public async Task UpdateThemeAsync(Guid userId, string? theme, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }
        user.Theme = theme;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns the IDs of teams the user explicitly belongs to.</summary>
    public async Task<List<Guid>> GetTeamIdsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.TeamId)
            .ToListAsync(ct);
    }

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
