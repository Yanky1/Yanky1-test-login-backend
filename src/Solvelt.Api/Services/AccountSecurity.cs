using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Services;

/// <summary>
/// Clears every credential an *unverified* account may carry before somebody else (the real mailbox owner) claims it:
/// passkeys, external logins, TOTP key + recovery codes, optionally the password; revokes sessions and rotates the security stamp.
/// Prevents account pre-hijacking (attacker pre-registers a victim's email and plants a credential).
/// </summary>
public class AccountSecurityService(UserManager<AppUser> users, SessionService sessions, AppDbContext db, IAuditLog audit)
{
    private const string StoreProvider = "[AspNetUserStore]";

    public async Task WipeCredentialsAsync(AppUser user, bool removePassword)
    {
        foreach (var p in await users.GetPasskeysAsync(user)) await users.RemovePasskeyAsync(user, p.CredentialId);
        foreach (var l in await users.GetLoginsAsync(user)) await users.RemoveLoginAsync(user, l.LoginProvider, l.ProviderKey);
        if (removePassword && await users.HasPasswordAsync(user)) await users.RemovePasswordAsync(user);
        if (removePassword) user.HasPassword = false;
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.RemoveAuthenticationTokenAsync(user, StoreProvider, "AuthenticatorKey");
        await users.RemoveAuthenticationTokenAsync(user, StoreProvider, "RecoveryCodes");
        await users.UpdateAsync(user);
        await sessions.RevokeAllAsync(user.Id, "account-claimed");
        await users.UpdateSecurityStampAsync(user);
        await audit.WriteAsync("account.credentials_wiped", user.Id, user.Email);
    }

    /// <summary>Removes an unverified account entirely (a fresh sign-up for the same address replaces it).</summary>
    public async Task DeleteUnverifiedAsync(AppUser user)
    {
        await sessions.RevokeAllAsync(user.Id, "account-replaced");
        await db.Memberships.Where(m => m.UserId == user.Id).ExecuteDeleteAsync();
        await db.Sellers.Where(s => s.UserId == user.Id).ExecuteDeleteAsync();
        await db.OneTimeCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync();
        await users.DeleteAsync(user);
        await audit.WriteAsync("account.unverified_replaced", null, user.Email);
    }
}
