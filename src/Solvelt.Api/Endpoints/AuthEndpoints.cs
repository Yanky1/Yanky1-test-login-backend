using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Endpoints;

public record SignupRequest(string? Email, string? Password, bool AcceptTerms, string? CaptchaToken);
public record LoginRequest(string? Email, string? Password, string? CaptchaToken, string? TotpCode, string? RecoveryCode);
public record RefreshRequest(string? RefreshToken);
public record CodeRequest(string? Code);
public record VerifyEmailRequest(string? Email, string? Code);
public record ForgotRequest(string? Email);
public record ResetRequest(string? Email, string? Token, string? NewPassword);
public record LoginFailure(string Error, AttemptsHint Hint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TwoFactorRequired = null);

public static class Validation
{
    public static bool IsEmail(string? e)
    {
        if (string.IsNullOrWhiteSpace(e) || e.Length > 254) return false;
        if (!MailAddress.TryCreate(e.Trim(), out var a) || a.Address != e.Trim() || !a.Host.Contains('.')) return false;
        return true;
    }

    public static IResult Invalid(string field, params string[] errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = errors });

    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("sub")!);
    public static Guid SessionId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("sid")!);
}

public static class AuthEndpoints
{
    public static readonly object SignupAccepted = new { message = "If this address can be used, we sent a 6-digit verification code to it.", resendAfterSeconds = OneTimeCodeService.ResendSeconds };
    private static readonly AppUser DummyUser = new();
    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(DummyUser, "Dummy-Password-For-Timing-1!");

    public static void MapAuthEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/auth");

        a.MapPost("/signup", async (SignupRequest r, HttpContext ctx, UserManager<AppUser> users, AuthFlows flows, OneTimeCodeService codes,
            Mailer mailer, IAuditLog audit, AccountSecurityService security, AppDbContext db, TimeProvider time, IOptions<AppOptions> app) =>
        {
            if (!Validation.IsEmail(r.Email)) return Validation.Invalid("email", "Enter a valid email address.");
            if (!r.AcceptTerms) return Validation.Invalid("acceptTerms", "You must accept the Terms.");
            var email = Crypto.NormalizeEmail(r.Email!);
            var pwErrors = PasswordRulesValidator.Describe(r.Password, email);
            if (pwErrors.Count > 0) return Validation.Invalid("password", pwErrors.ToArray());

            // The response never depends on whether the address is already registered (no enumeration).
            var existing = await users.FindByEmailAsync(email);
            if (existing is { EmailConfirmed: true })
            {
                new PasswordHasher<AppUser>().HashPassword(DummyUser, r.Password!); // comparable work to creating an account
                await mailer.SendAsync(Templates.SignupExisting(email, app.Value.FrontendBaseUrl.TrimEnd('/') + "/login", app.Value.FrontendBaseUrl.TrimEnd('/') + "/forgot-password"));
                await audit.WriteAsync("signup.existing_notified", existing.Id, email);
            }
            else
            {
                // An unverified account may have been registered by someone else: the latest sign-up replaces it.
                if (existing is not null) await security.DeleteUnverifiedAsync(existing);
                AppUser user;
                try { user = await flows.CreateSellerUserAsync(email, r.Password, false, true); }
                catch (AccountCreationException ex) { return Validation.Invalid("password", ex.Errors); }
                var issued = await codes.IssueAsync(user.Id, "verify-email");
                if (issued.Code is not null) await mailer.SendAsync(Templates.VerifyCode(email, issued.Code));
                await audit.WriteAsync("signup.created", user.Id, email, "method=password");
            }
            return Results.Accepted(value: SignupAccepted);
        }).RequireRateLimiting("auth");

        a.MapPost("/login", async (LoginRequest r, HttpContext ctx, UserManager<AppUser> users, LoginThrottleService throttle,
            AuthFlows flows, SessionService sessions, AppDbContext db, Mailer mailer, IAuditLog audit, IOptions<LockoutSettings> lo) =>
        {
            var fail = (LoginThrottle? t) => Results.Json(new LoginFailure("Login failed", throttle.Hint(t)), statusCode: 401);
            if (string.IsNullOrWhiteSpace(r.Email) || string.IsNullOrEmpty(r.Password) || r.Email.Length > 254 || r.Password.Length > 256)
                return fail(null);
            var email = Crypto.NormalizeEmail(r.Email);
            var t = await throttle.GetAsync(email);
            if (throttle.IsLocked(t)) { await audit.WriteAsync("login.blocked_locked", null, email); return fail(t); }
            if (throttle.CaptchaRequired(t) && !await flows.CaptchaOkAsync(ctx, r.CaptchaToken))
                return Results.Json(new { error = "Verification failed" }, statusCode: 400);

            // Reserve (count) the attempt atomically BEFORE checking the password: parallel guesses cannot exceed the limit.
            var attempts = await throttle.ReserveAttemptAsync(email);
            if (attempts > lo.Value.MaxFailures)
            {
                await audit.WriteAsync("login.blocked_locked", null, email);
                return fail(await throttle.GetAsync(email));
            }

            var user = await users.FindByEmailAsync(email);
            var ok = false;
            if (user is not null && user.HasPassword) ok = await users.CheckPasswordAsync(user, r.Password);
            else new PasswordHasher<AppUser>().VerifyHashedPassword(DummyUser, DummyHash, r.Password); // equalize timing
            if (ok)
            {
                if (!user!.EmailConfirmed) ok = false; // unverified sign-ups cannot sign in until they prove the mailbox
                else if (!await flows.CanSignInAsync(user)) ok = false;
            }
            if (ok && user!.TwoFactorEnabled)
            {
                if (string.IsNullOrWhiteSpace(r.TotpCode) && string.IsNullOrWhiteSpace(r.RecoveryCode))
                {
                    await throttle.ReleaseAttemptAsync(email); // password was right; the code is the next step, not a failure
                    return Results.Json(new LoginFailure("Login failed", throttle.Hint(await throttle.GetAsync(email)), true), statusCode: 401);
                }
                ok = !string.IsNullOrWhiteSpace(r.TotpCode)
                    ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.TotpCode.Replace(" ", ""))
                    : (await users.RedeemTwoFactorRecoveryCodeAsync(user, r.RecoveryCode!.Replace(" ", ""))).Succeeded;
            }
            if (!ok)
            {
                var justLocked = await throttle.LockIfReachedAsync(email, attempts);
                var row = await throttle.GetAsync(email);
                await audit.WriteAsync("login.failed", user?.Id, email);
                if (justLocked)
                {
                    await audit.WriteAsync("account.locked", user?.Id, email);
                    if (user is not null)
                    {
                        await sessions.RevokeAllAsync(user.Id, "lockout");
                        await mailer.SendAsync(Templates.Locked(user.Email!, lo.Value.LockMinutes));
                    }
                }
                return fail(row);
            }
            await throttle.ResetAsync(email);
            var session = await sessions.CreateAsync(user!, "password", AuthFlows.Ip(ctx), AuthFlows.Ua(ctx));
            await audit.WriteAsync("login.succeeded", user!.Id, email);
            return Results.Ok(await flows.BuildLoginAsync(user, session, ctx));
        }).RequireRateLimiting("auth");

        a.MapPost("/refresh", async (HttpContext ctx, RefreshRequest? body, AuthFlows flows, SessionService sessions) =>
        {
            var cookie = ctx.Request.Cookies[AuthFlows.RefreshCookie];
            if (cookie is not null && !flows.CsrfOk(ctx)) return Results.Problem("CSRF validation failed", statusCode: 403);
            var raw = cookie ?? body?.RefreshToken;
            if (string.IsNullOrWhiteSpace(raw)) return Results.Json(new { error = "Session expired" }, statusCode: 401);
            var res = await sessions.RotateAsync(raw);
            if (res.Outcome != SessionService.RotateOutcome.Ok)
            {
                flows.ClearCookies(ctx);
                return Results.Json(new { error = "Session expired" }, statusCode: 401);
            }
            return Results.Ok(await flows.BuildLoginAsync(res.User!, res.Session!, ctx));
        }).RequireRateLimiting("auth");

        a.MapPost("/logout", async (HttpContext ctx, RefreshRequest? body, AuthFlows flows, SessionService sessions, AppDbContext db, IAuditLog audit) =>
        {
            var cookie = ctx.Request.Cookies[AuthFlows.RefreshCookie];
            if (cookie is not null && !flows.CsrfOk(ctx)) return Results.Problem("CSRF validation failed", statusCode: 403);
            var raw = cookie ?? body?.RefreshToken;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var hash = Crypto.Sha256(raw);
                var tok = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
                if (tok is not null)
                {
                    await sessions.RevokeSessionAsync(tok.SessionId, "logout");
                    await audit.WriteAsync("logout", null, null, $"session={tok.SessionId}");
                }
            }
            flows.ClearCookies(ctx);
            return Results.NoContent();
        });

        a.MapPost("/logout-all", async (ClaimsPrincipal p, HttpContext ctx, AuthFlows flows, SessionService sessions, IAuditLog audit) =>
        {
            await sessions.RevokeAllAsync(p.UserId(), "logout-all");
            await audit.WriteAsync("logout.all", p.UserId());
            flows.ClearCookies(ctx);
            return Results.NoContent();
        }).RequireAuthorization();

        // Unauthenticated: the account has no session until the mailbox is proven.
        a.MapPost("/verify-email", async (VerifyEmailRequest r, HttpContext ctx, UserManager<AppUser> users, OneTimeCodeService codes,
            SessionService sessions, AuthFlows flows, AppDbContext db, IAuditLog audit) =>
        {
            IResult Bad() => Results.Problem("That code isn't right or has expired. Request a new one if needed.", statusCode: 400,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_or_expired" });
            if (!Validation.IsEmail(r.Email) || string.IsNullOrWhiteSpace(r.Code) || r.Code.Trim().Length != 6 || !r.Code.Trim().All(char.IsAsciiDigit)) return Bad();
            var email = Crypto.NormalizeEmail(r.Email!);
            var user = await users.FindByEmailAsync(email);
            if (user is null || user.EmailConfirmed) return Bad(); // identical for unknown and already-verified
            var (outcome, _) = await codes.VerifyAsync(user.Id, "verify-email", r.Code);
            if (outcome != OneTimeCodeService.VerifyOutcome.Ok)
            {
                await audit.WriteAsync("email.verify_failed", user.Id, email, outcome.ToString());
                return Bad();
            }
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            // number+PIN chosen by a phone/link sign-up attach only in the browser that completed the link; any other verification drops them
            await ctx.RequestServices.GetService<Solvelt.Api.Phone.PendingPhoneService>()!.AttachAsync(user, phoneConversation: false, ctx.Request.Cookies[Solvelt.Api.Phone.PendingPhoneService.Cookie]);
            ctx.Response.Cookies.Delete(Solvelt.Api.Phone.PendingPhoneService.Cookie, new CookieOptions { Path = "/api/v1/auth", Secure = true, SameSite = SameSiteMode.Strict, HttpOnly = true });
            var seller = await db.Sellers.FirstOrDefaultAsync(s => s.UserId == user.Id);
            if (seller is not null && seller.DraftStep < 3) { seller.DraftStep = 3; await db.SaveChangesAsync(); }
            await audit.WriteAsync("email.verified", user.Id, email);
            var session = await sessions.CreateAsync(user, "email-code", AuthFlows.Ip(ctx), AuthFlows.Ua(ctx));
            return Results.Ok(await flows.BuildLoginAsync(user, session, ctx));
        }).RequireRateLimiting("sensitive");

        a.MapPost("/resend-code", async (ForgotRequest r, UserManager<AppUser> users, OneTimeCodeService codes, Mailer mailer) =>
        {
            if (Validation.IsEmail(r.Email))
            {
                var user = await users.FindByEmailAsync(Crypto.NormalizeEmail(r.Email!));
                if (user is { EmailConfirmed: false })
                {
                    var res = await codes.IssueAsync(user.Id, "verify-email");
                    if (res.Code is not null) await mailer.SendAsync(Templates.VerifyCode(user.Email!, res.Code));
                }
            }
            return Results.Accepted(value: SignupAccepted); // same answer whether or not a mail was sent
        }).RequireRateLimiting("sensitive");

        a.MapPost("/forgot-password", async (ForgotRequest r, UserManager<AppUser> users, Mailer mailer, IOptions<AppOptions> app, IAuditLog audit) =>
        {
            if (Validation.IsEmail(r.Email))
            {
                var email = Crypto.NormalizeEmail(r.Email!);
                var user = await users.FindByEmailAsync(email);
                if (user is not null)
                {
                    var token = await users.GeneratePasswordResetTokenAsync(user);
                    var link = $"{app.Value.FrontendBaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(email)}&token={WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))}";
                    await mailer.SendDetachedAsync(Templates.ResetPassword(email, link));
                    await audit.WriteAsync("password.reset_requested", user.Id, email);
                }
            }
            return Results.Accepted(value: new { message = "If an account exists for that email, a reset link is on its way." });
        }).RequireRateLimiting("sensitive");

        a.MapPost("/reset-password", async (ResetRequest r, UserManager<AppUser> users, LoginThrottleService throttle, SessionService sessions,
            Mailer mailer, IAuditLog audit, AccountSecurityService security) =>
        {
            var generic = () => Results.Problem("This reset link is invalid or has expired.", statusCode: 400,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_token" });
            if (!Validation.IsEmail(r.Email) || string.IsNullOrWhiteSpace(r.Token) || string.IsNullOrEmpty(r.NewPassword)) return generic();
            var email = Crypto.NormalizeEmail(r.Email!);
            var user = await users.FindByEmailAsync(email);
            if (user is null) return generic();
            string token;
            try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(r.Token)); } catch (FormatException) { return generic(); }
            var wasVerified = user.EmailConfirmed;
            var res = await users.ResetPasswordAsync(user, token, r.NewPassword);
            if (!res.Succeeded)
            {
                var pw = res.Errors.Where(e => e.Code == "PasswordRules" || e.Code.StartsWith("Password")).Select(e => e.Description).ToArray();
                return pw.Length > 0 ? Validation.Invalid("newPassword", pw) : generic();
            }
            if (!wasVerified) await security.WipeCredentialsAsync(user, removePassword: false); // plant-proof: attacker's passkeys/2FA/logins die
            user.HasPassword = true; user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await throttle.ResetAsync(email);
            await sessions.RevokeAllAsync(user.Id, "password-reset");
            await mailer.SendAsync(Templates.PasswordChanged(email));
            await audit.WriteAsync("password.reset_completed", user.Id, email);
            return Results.NoContent();
        }).RequireRateLimiting("sensitive");

        a.MapGet("/session", async (ClaimsPrincipal p, HttpContext ctx, UserManager<AppUser> users, AccountContextService accounts) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            var info = user is null ? null : await SessionEndpoints.Describe(user, ctx.GetAccess(), accounts);
            return Results.Ok(new { userId = p.UserId(), sessionId = p.SessionId(), activeAccountId = info?.ActiveAccountId, accounts = info?.Accounts ?? [] });
        }).RequireAuthorization();
    }
}
