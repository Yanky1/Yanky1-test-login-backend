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

namespace Solvelt.Api.Endpoints;

public record SignupRequest(string? Email, string? Password, bool AcceptTerms, string? CaptchaToken);
public record LoginRequest(string? Email, string? Password, string? CaptchaToken, string? TotpCode, string? RecoveryCode);
public record RefreshRequest(string? RefreshToken);
public record CodeRequest(string? Code);
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
    private static readonly AppUser DummyUser = new();
    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(DummyUser, "Dummy-Password-For-Timing-1!");

    public static void MapAuthEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/auth");

        a.MapPost("/signup", async (SignupRequest r, HttpContext ctx, UserManager<AppUser> users, AuthFlows flows, SessionService sessions,
            OneTimeCodeService codes, Mailer mailer, IAuditLog audit) =>
        {
            if (!Validation.IsEmail(r.Email)) return Validation.Invalid("email", "Enter a valid email address.");
            if (!r.AcceptTerms) return Validation.Invalid("acceptTerms", "You must accept the Terms.");
            var email = Crypto.NormalizeEmail(r.Email!);
            var pwErrors = PasswordRulesValidator.Describe(r.Password, email);
            if (pwErrors.Count > 0) return Validation.Invalid("password", pwErrors.ToArray());
            if (await users.FindByEmailAsync(email) is not null)
                return Validation.Invalid("email", "We couldn't create an account with this email. If you already have one, sign in or reset your password.");
            AppUser user;
            try { user = await flows.CreateSellerUserAsync(email, r.Password, false, true); }
            catch (AccountCreationException ex) { return Validation.Invalid("password", ex.Errors); }
            var issued = await codes.IssueAsync(user.Id, "verify-email");
            if (issued.Code is not null) await mailer.SendAsync(Templates.VerifyCode(email, issued.Code));
            await audit.WriteAsync("signup.created", user.Id, email, "method=password");
            var session = await sessions.CreateAsync(user, "password", AuthFlows.Ip(ctx), AuthFlows.Ua(ctx));
            return Results.Json(await flows.BuildLoginAsync(user, session, ctx), statusCode: 201);
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

            var user = await users.FindByEmailAsync(email);
            var ok = false;
            if (user is not null && user.HasPassword) ok = await users.CheckPasswordAsync(user, r.Password);
            else new PasswordHasher<AppUser>().VerifyHashedPassword(DummyUser, DummyHash, r.Password); // equalize timing
            if (ok)
            {
                var seller = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user!.Id);
                var isAdmin = await users.IsInRoleAsync(user!, "Admin");
                if (!isAdmin && (seller is null || seller.Status is SellerStatus.Rejected or SellerStatus.Suspended)) ok = false;
            }
            if (ok && user!.TwoFactorEnabled)
            {
                if (string.IsNullOrWhiteSpace(r.TotpCode) && string.IsNullOrWhiteSpace(r.RecoveryCode))
                    return Results.Json(new LoginFailure("Login failed", throttle.Hint(t), true), statusCode: 401);
                ok = !string.IsNullOrWhiteSpace(r.TotpCode)
                    ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.TotpCode.Replace(" ", ""))
                    : (await users.RedeemTwoFactorRecoveryCodeAsync(user, r.RecoveryCode!.Replace(" ", ""))).Succeeded;
            }
            if (!ok)
            {
                var (row, justLocked) = await throttle.RegisterFailureAsync(email);
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

        a.MapGet("/verification-status", async (ClaimsPrincipal p, AppDbContext db, TimeProvider time, UserManager<AppUser> users) =>
        {
            var id = p.UserId();
            var user = await users.FindByIdAsync(id.ToString());
            var last = await db.OneTimeCodes.Where(c => c.UserId == id && c.Purpose == "verify-email").OrderByDescending(c => c.SentAt).FirstOrDefaultAsync();
            var after = last is null ? 0 : Math.Max(0, OneTimeCodeService.ResendSeconds - (int)(time.UtcNow() - last.SentAt).TotalSeconds);
            return Results.Ok(new { emailConfirmed = user!.EmailConfirmed, email = user.Email, resendAfterSeconds = after });
        }).RequireAuthorization();

        a.MapPost("/verify-email", async (CodeRequest r, ClaimsPrincipal p, UserManager<AppUser> users, OneTimeCodeService codes,
            SessionService sessions, AppDbContext db, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(r.Code) || r.Code.Trim().Length != 6 || !r.Code.Trim().All(char.IsAsciiDigit))
                return Results.Problem("Enter the 6-digit code.", statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "invalid" });
            if (!user.EmailConfirmed)
            {
                var (outcome, _) = await codes.VerifyAsync(user.Id, "verify-email", r.Code);
                if (outcome != OneTimeCodeService.VerifyOutcome.Ok)
                {
                    await audit.WriteAsync("email.verify_failed", user.Id, user.Email, outcome.ToString());
                    var msg = outcome switch
                    {
                        OneTimeCodeService.VerifyOutcome.Expired => "That code has expired. Request a new one.",
                        OneTimeCodeService.VerifyOutcome.TooManyAttempts => "Too many attempts. Request a new code.",
                        _ => "That code isn't right."
                    };
                    return Results.Problem(msg, statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = outcome.ToString().ToLowerInvariant() });
                }
                user.EmailConfirmed = true;
                await users.UpdateAsync(user);
                var seller = await db.Sellers.FirstAsync(s => s.UserId == user.Id);
                if (seller.DraftStep < 3) { seller.DraftStep = 3; await db.SaveChangesAsync(); }
                await audit.WriteAsync("email.verified", user.Id, user.Email);
            }
            var token = await sessions.IssueAccessAsync(user, p.SessionId());
            return Results.Ok(new { tokenType = "Bearer", accessToken = token, expiresIn = sessions.AccessSeconds });
        }).RequireAuthorization().RequireRateLimiting("sensitive");

        a.MapPost("/resend-code", async (ClaimsPrincipal p, HttpContext ctx, UserManager<AppUser> users, OneTimeCodeService codes, Mailer mailer) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null || user.EmailConfirmed) return Results.Problem("Email already verified.", statusCode: 400);
            var res = await codes.IssueAsync(user.Id, "verify-email");
            if (res.Code is null)
            {
                ctx.Response.Headers.RetryAfter = res.RetryAfterSeconds.ToString();
                return Results.Problem("Please wait before requesting another code.", statusCode: 429,
                    extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = res.RetryAfterSeconds });
            }
            await mailer.SendAsync(Templates.VerifyCode(user.Email!, res.Code));
            return Results.Ok(new { resendAfterSeconds = res.RetryAfterSeconds });
        }).RequireAuthorization().RequireRateLimiting("sensitive");

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
                    await mailer.SendAsync(Templates.ResetPassword(email, link));
                    await audit.WriteAsync("password.reset_requested", user.Id, email);
                }
            }
            return Results.Accepted(value: new { message = "If an account exists for that email, a reset link is on its way." });
        }).RequireRateLimiting("sensitive");

        a.MapPost("/reset-password", async (ResetRequest r, UserManager<AppUser> users, LoginThrottleService throttle, SessionService sessions,
            Mailer mailer, IAuditLog audit) =>
        {
            var generic = () => Results.Problem("This reset link is invalid or has expired.", statusCode: 400,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_token" });
            if (!Validation.IsEmail(r.Email) || string.IsNullOrWhiteSpace(r.Token) || string.IsNullOrEmpty(r.NewPassword)) return generic();
            var email = Crypto.NormalizeEmail(r.Email!);
            var user = await users.FindByEmailAsync(email);
            if (user is null) return generic();
            string token;
            try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(r.Token)); } catch (FormatException) { return generic(); }
            var res = await users.ResetPasswordAsync(user, token, r.NewPassword);
            if (!res.Succeeded)
            {
                var pw = res.Errors.Where(e => e.Code == "PasswordRules" || e.Code.StartsWith("Password")).Select(e => e.Description).ToArray();
                return pw.Length > 0 ? Validation.Invalid("newPassword", pw) : generic();
            }
            user.HasPassword = true; user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await throttle.ResetAsync(email);
            await sessions.RevokeAllAsync(user.Id, "password-reset");
            await mailer.SendAsync(Templates.PasswordChanged(email));
            await audit.WriteAsync("password.reset_completed", user.Id, email);
            return Results.NoContent();
        }).RequireRateLimiting("sensitive");

        a.MapGet("/session", (ClaimsPrincipal p) => Results.Ok(new { userId = p.UserId(), sessionId = p.SessionId() })).RequireAuthorization();
    }
}
