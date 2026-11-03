using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Services;

namespace Solvelt.Api.Endpoints;

public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public record ChangeEmailRequest(string? NewEmail, string? CurrentPassword, string? StepUpCode, string? TotpCode);
public record TwoFactorCodeRequest(string? Code, string? CurrentPassword);

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/account").RequireAuthorization();

        a.MapGet("/me", async (ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            var seller = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user.Id);
            return Results.Ok(new
            {
                email = user.Email, emailConfirmed = user.EmailConfirmed, hasPassword = user.HasPassword,
                twoFactorEnabled = user.TwoFactorEnabled,
                passkeyCount = (await users.GetPasskeysAsync(user)).Count,
                logins = (await users.GetLoginsAsync(user)).Select(l => l.LoginProvider),
                roles = await users.GetRolesAsync(user),
                sellerId = seller?.Id, sellerStatus = seller?.Status.ToString(), vendorName = seller?.VendorName
            });
        });

        a.MapPost("/change-password", async (ChangePasswordRequest r, ClaimsPrincipal p, UserManager<AppUser> users, SessionService sessions,
            Mailer mailer, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (!user.HasPassword) return Results.Problem("This account has no password. Use \"Forgot password\" to set one.", statusCode: 400);
            if (string.IsNullOrEmpty(r.CurrentPassword) || string.IsNullOrEmpty(r.NewPassword)) return Validation.Invalid("newPassword", "Required.");
            var pw = PasswordRulesValidator.Describe(r.NewPassword, user.Email);
            if (!await users.CheckPasswordAsync(user, r.CurrentPassword))
                return Results.Problem("Your current password isn't correct.", statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "wrong_password" });
            if (pw.Count > 0) return Validation.Invalid("newPassword", pw.ToArray());
            var res = await users.ChangePasswordAsync(user, r.CurrentPassword, r.NewPassword);
            if (!res.Succeeded) return Validation.Invalid("newPassword", res.Errors.Select(e => e.Description).ToArray());
            await sessions.RevokeAllAsync(user.Id, "password-changed", except: p.SessionId());
            await mailer.SendAsync(Templates.PasswordChanged(user.Email!));
            await audit.WriteAsync("password.changed", user.Id, user.Email);
            return Results.NoContent();
        }).RequireRateLimiting("sensitive");

        a.MapPost("/email/change", async (ChangeEmailRequest r, ClaimsPrincipal p, UserManager<AppUser> users, OneTimeCodeService codes,
            Mailer mailer, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (!Validation.IsEmail(r.NewEmail)) return Validation.Invalid("newEmail", "Enter a valid email address.");
            if (user.HasPassword && (string.IsNullOrEmpty(r.CurrentPassword) || !await users.CheckPasswordAsync(user, r.CurrentPassword)))
                return Results.Problem("Your current password isn't correct.", statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "wrong_password" });
            if (!user.HasPassword)
            {
                // Passkey-only / Google-only accounts have no password to re-enter: step up with a code mailed to the CURRENT address
                // (plus a TOTP code when 2FA is on), so a stolen access token alone cannot move the account to another mailbox.
                if (string.IsNullOrWhiteSpace(r.StepUpCode))
                {
                    var su = await codes.IssueAsync(user.Id, "email-stepup");
                    if (su.Code is not null) await mailer.SendAsync(Templates.StepUpCode(user.Email!, su.Code));
                    return Results.Json(new { stepUpRequired = true, message = "We sent a confirmation code to your current email address." }, statusCode: 202);
                }
                var (so, _) = await codes.VerifyAsync(user.Id, "email-stepup", r.StepUpCode);
                var totpOk = !user.TwoFactorEnabled || (!string.IsNullOrWhiteSpace(r.TotpCode) &&
                    await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.TotpCode.Replace(" ", "")));
                if (so != OneTimeCodeService.VerifyOutcome.Ok || !totpOk)
                    return Results.Problem("That confirmation code isn't right.", statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "stepup_failed" });
            }
            var newEmail = Crypto.NormalizeEmail(r.NewEmail!);
            // Generic response whether or not the address is taken (no enumeration).
            if (await users.FindByEmailAsync(newEmail) is null)
            {
                var issued = await codes.IssueAsync(user.Id, "change-email", newEmail);
                if (issued.Code is not null) await mailer.SendAsync(Templates.ChangeEmailCode(newEmail, issued.Code));
                await audit.WriteAsync("email.change_requested", user.Id, user.Email);
            }
            return Results.Accepted(value: new { message = "If the address can be used, we sent a code to it." });
        }).RequireAuthorization("VerifiedEmail").RequireRateLimiting("sensitive");

        a.MapPost("/email/confirm", async (CodeRequest r, ClaimsPrincipal p, UserManager<AppUser> users, OneTimeCodeService codes,
            SessionService sessions, Mailer mailer, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(r.Code)) return Results.Problem("Enter the code.", statusCode: 400);
            var (outcome, row) = await codes.VerifyAsync(user.Id, "change-email", r.Code);
            if (outcome != OneTimeCodeService.VerifyOutcome.Ok || row?.Target is null)
                return Results.Problem(outcome == OneTimeCodeService.VerifyOutcome.Expired ? "That code has expired." : "That code isn't right.",
                    statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = outcome.ToString().ToLowerInvariant() });
            var old = user.Email!;
            if (await users.FindByEmailAsync(row.Target) is not null) return Results.Problem("That address can't be used.", statusCode: 400);
            await users.SetEmailAsync(user, row.Target);
            await users.SetUserNameAsync(user, row.Target);
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await mailer.SendAsync(Templates.EmailChangedNotice(old, row.Target));
            await audit.WriteAsync("email.changed", user.Id, row.Target, "old=" + AuditLog.Mask(old));
            var token = await sessions.IssueAccessAsync(user, p.SessionId());
            return Results.Ok(new { tokenType = "Bearer", accessToken = token, expiresIn = sessions.AccessSeconds, email = user.Email });
        }).RequireAuthorization("VerifiedEmail").RequireRateLimiting("sensitive");

        // ---- TOTP 2FA
        a.MapPost("/2fa/setup", async (ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (user.TwoFactorEnabled) return Results.Problem("Two-factor authentication is already enabled.", statusCode: 409);
            await users.ResetAuthenticatorKeyAsync(user);
            var key = (await users.GetAuthenticatorKeyAsync(user))!;
            var uri = $"otpauth://totp/Solvelt:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer=Solvelt&digits=6";
            return Results.Ok(new { sharedKey = key, otpAuthUri = uri });
        }).RequireAuthorization("VerifiedEmail");

        a.MapPost("/2fa/enable", async (TwoFactorCodeRequest r, ClaimsPrincipal p, UserManager<AppUser> users, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(r.Code) ||
                !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.Code.Replace(" ", "")))
                return Results.Problem("That code isn't right.", statusCode: 400);
            await users.SetTwoFactorEnabledAsync(user, true);
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            await audit.WriteAsync("2fa.enabled", user.Id, user.Email);
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireAuthorization("VerifiedEmail").RequireRateLimiting("sensitive");

        a.MapPost("/2fa/disable", async (TwoFactorCodeRequest r, ClaimsPrincipal p, UserManager<AppUser> users, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (!user.TwoFactorEnabled) return Results.NoContent();
            var pwOk = !user.HasPassword || (!string.IsNullOrEmpty(r.CurrentPassword) && await users.CheckPasswordAsync(user, r.CurrentPassword));
            var codeOk = !string.IsNullOrWhiteSpace(r.Code) &&
                await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.Code.Replace(" ", ""));
            if (!pwOk || !codeOk) return Results.Problem("Password or code isn't right.", statusCode: 400);
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user);
            await audit.WriteAsync("2fa.disabled", user.Id, user.Email);
            return Results.NoContent();
        }).RequireAuthorization("VerifiedEmail").RequireRateLimiting("sensitive");

        a.MapPost("/2fa/recovery-codes", async (TwoFactorCodeRequest r, ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null || !user.TwoFactorEnabled) return Results.Problem("Two-factor authentication isn't enabled.", statusCode: 400);
            if (string.IsNullOrWhiteSpace(r.Code) ||
                !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.Code.Replace(" ", "")))
                return Results.Problem("That code isn't right.", statusCode: 400);
            return Results.Ok(new { recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) });
        }).RequireAuthorization("VerifiedEmail").RequireRateLimiting("sensitive");

        // ---- Sessions
        a.MapGet("/sessions", async (ClaimsPrincipal p, AppDbContext db, SessionService sessions) =>
        {
            var id = p.UserId(); var cur = p.SessionId();
            var list = await db.Sessions.AsNoTracking().Where(s => s.UserId == id && s.RevokedAt == null)
                .OrderByDescending(s => s.LastActivityAt).Take(50).ToListAsync();
            return Results.Ok(list.Where(s => !sessions.IsIdle(s)).Select(s => new
            {
                s.Id, s.CreatedAt, s.LastActivityAt, s.UserAgent, s.IpAddress, s.Method, current = s.Id == cur
            }));
        });

        a.MapDelete("/sessions/{sid:guid}", async (Guid sid, ClaimsPrincipal p, AppDbContext db, SessionService sessions) =>
        {
            var id = p.UserId();
            if (!await db.Sessions.AnyAsync(s => s.Id == sid && s.UserId == id)) return Results.NotFound();
            await sessions.RevokeSessionAsync(sid, "revoked-by-user");
            return Results.NoContent();
        });

        // ---- Linked logins
        a.MapDelete("/logins/{provider}", async (string provider, ClaimsPrincipal p, UserManager<AppUser> users, AuthFlows flows, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            var login = (await users.GetLoginsAsync(user)).FirstOrDefault(l => l.LoginProvider.Equals(provider, StringComparison.OrdinalIgnoreCase));
            if (login is null) return Results.NotFound();
            if (await flows.SignInMethodCountAsync(user) <= 1)
                return Results.Problem("You can't remove your last sign-in method.", statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "last_method" });
            await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
            await audit.WriteAsync("login.unlinked", user.Id, user.Email, provider);
            return Results.NoContent();
        });
    }
}
