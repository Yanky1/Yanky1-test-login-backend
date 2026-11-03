using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;
using Solvelt.Api.Security;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Services;

public record LoginBody(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken,
    Guid SellerId, string Email, string VendorName, string AccountStatus, string[] Roles,
    Guid? ActiveAccountId, List<AccountItem> Accounts);

public class AuthFlows(AppDbContext db, UserManager<AppUser> users, IOptions<SessionSettings> sess,
    IOptions<AppOptions> app, TimeProvider time, ITurnstileVerifier turnstile, TeamService team, AccountContextService accounts)
{
    public const string RefreshCookie = "sl_rt";
    public const string CsrfCookie = "sl_csrf";

    public static string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();
    public static string? Ua(HttpContext c) => c.Request.Headers.UserAgent.ToString() is { Length: > 0 } u ? u : null;

    public Task<bool> CaptchaOkAsync(HttpContext c, string? token) => turnstile.VerifyAsync(token, Ip(c), c.RequestAborted);

    public async Task<LoginBody> BuildLoginAsync(AppUser user, IssuedSession s, HttpContext ctx)
    {
        var roles = (await users.GetRolesAsync(user)).ToArray();
        var access = s.ActiveAccountId is { } a ? await accounts.ResolveAsync(user.Id, a) : null;
        var info = await SessionEndpoints.Describe(user, access, accounts);
        SetCookies(ctx, s.RefreshToken);
        // sellerId / vendorName describe the ACTIVE account (empty while the user still has to choose one, or has none).
        // `accountStatus` keeps its original meaning (sign-in state, DECISIONS #9); each account's approval status is in accounts[].accountStatus.
        return new LoginBody("Bearer", s.AccessToken, s.ExpiresIn, s.RefreshToken,
            access?.SellerId ?? Guid.Empty, user.Email ?? "", info.VendorName,
            user.EmailConfirmed ? "Active" : "PendingEmailVerification", roles, access?.SellerId, info.Accounts);
    }

    /// <summary>Sign-in gate: admins and users without any account may sign in; a user whose every account is Rejected/Suspended may not.</summary>
    public async Task<bool> CanSignInAsync(AppUser user)
    {
        if (await users.IsInRoleAsync(user, "Admin")) return true;
        var list = await accounts.ListAsync(user.Id);
        return list.Count == 0 || list.Any(a => AccountContextService.Usable(Enum.Parse<SellerStatus>(a.AccountStatus)));
    }

    public void SetCookies(HttpContext ctx, string refreshToken)
    {
        var exp = time.GetUtcNow().AddDays(sess.Value.RefreshTokenDays);
        ctx.Response.Cookies.Append(RefreshCookie, refreshToken, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = sess.Value.RefreshCookiePath, Expires = exp
        });
        ctx.Response.Cookies.Append(CsrfCookie, Crypto.RandomToken(24), new CookieOptions
        {
            HttpOnly = false, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", Expires = exp
        });
    }

    public void ClearCookies(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = sess.Value.RefreshCookiePath, Secure = true, SameSite = SameSiteMode.Strict, HttpOnly = true });
        ctx.Response.Cookies.Delete(CsrfCookie, new CookieOptions { Path = "/", Secure = true, SameSite = SameSiteMode.Strict });
    }

    /// <summary>Double-submit token + Origin allowlist check for cookie-authenticated requests.</summary>
    public bool CsrfOk(HttpContext c)
    {
        var origin = c.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !app.Value.CorsOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return false;
        var header = c.Request.Headers["X-CSRF-Token"].ToString();
        var cookie = c.Request.Cookies[CsrfCookie];
        return header.Length > 0 && cookie is not null && Crypto.FixedEquals(header, cookie);
    }

    public async Task<int> SignInMethodCountAsync(AppUser user)
    {
        var logins = (await users.GetLoginsAsync(user)).Count;
        var passkeys = (await users.GetPasskeysAsync(user)).Count;
        return (user.HasPassword ? 1 : 0) + logins + passkeys;
    }

    public async Task<AppUser> CreateSellerUserAsync(string email, string? password, bool emailConfirmed, bool termsAccepted, Guid? id = null)
    {
        var user = new AppUser
        {
            Id = id ?? Guid.Empty, UserName = email, Email = email, EmailConfirmed = emailConfirmed, CreatedAt = time.UtcNow(),
            HasPassword = password is not null
        };
        var res = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
        if (!res.Succeeded) throw new AccountCreationException(res.Errors.Select(e => e.Description).ToArray());
        await users.AddToRoleAsync(user, "Seller");
        var now = time.UtcNow();
        team.NewAccount(user.Id, emailConfirmed ? 3 : 2, termsAccepted ? await Solvelt.Api.Admin.PlatformSettings.TermsAsync(db, app.Value.TermsVersion) : null, termsAccepted ? now : null);
        await db.SaveChangesAsync();
        return user;
    }
}

public class AccountCreationException(string[] errors) : Exception("Account creation failed")
{
    public string[] Errors { get; } = errors;
}
