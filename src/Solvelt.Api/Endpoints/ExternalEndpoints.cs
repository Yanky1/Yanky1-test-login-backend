using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;
using Solvelt.Api.Security;
using Solvelt.Api.Services;

namespace Solvelt.Api.Endpoints;

public record ExchangeRequest(string? Code, string? TotpCode, string? RecoveryCode);

public static class ExternalEndpoints
{
    public static void MapExternalEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/auth/external");

        // Start: GET so the browser can navigate. terms=1 is required to create a new account.
        a.MapGet("/{provider}/start", async (string provider, string? returnUrl, string? terms, HttpContext ctx,
            IAuthenticationSchemeProvider schemes, IOptions<ExternalAuthOptions> ext) =>
        {
            var name = provider.ToLowerInvariant() switch { "google" => "Google", "apple" when ext.Value.AppleEnabled => "Apple", _ => null };
            if (name is null || await schemes.GetSchemeAsync(name) is null) return Results.NotFound();
            var props = new AuthenticationProperties
            {
                RedirectUri = "/api/v1/auth/external/callback?provider=" + name
            };
            props.Items["terms"] = terms == "1" ? "1" : "0";
            props.Items["next"] = SafeRedirect.Sanitize(returnUrl) ?? "";
            return Results.Challenge(props, [name]);
        }).RequireRateLimiting("auth");

        a.MapGet("/callback", async (string provider, HttpContext ctx, UserManager<AppUser> users, AuthFlows flows, AppDbContext db,
            TimeProvider time, IAuditLog audit, IOptions<AppOptions> app, AccountSecurityService security) =>
        {
            var front = app.Value.FrontendBaseUrl.TrimEnd('/');
            IResult Error(string code) => Results.Redirect($"{front}/login?error={code}");
            var res = await ctx.AuthenticateAsync(Extensions.ExternalScheme);
            await ctx.SignOutAsync(Extensions.ExternalScheme);
            if (!res.Succeeded || res.Principal is null) return Error("external_failed");
            string next = "", terms = "";
            res.Properties?.Items.TryGetValue("next", out next!);
            res.Properties?.Items.TryGetValue("terms", out terms!);
            next ??= ""; var termsAccepted = terms == "1";
            var sub = res.Principal.FindFirstValue("sub") ?? res.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var email = res.Principal.FindFirstValue("email") ?? res.Principal.FindFirstValue(ClaimTypes.Email);
            var verified = string.Equals(res.Principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(sub)) return Error("external_failed");

            var user = await users.FindByLoginAsync(provider, sub);
            if (user is null)
            {
                if (string.IsNullOrEmpty(email) || !verified) return Error("email_unverified"); // never link/create on unverified provider email
                email = Crypto.NormalizeEmail(email);
                user = await users.FindByEmailAsync(email);
                if (user is not null)
                {
                    if (!user.EmailConfirmed)
                    {
                        // Pre-hijacking defence: an unverified local account may have been registered by someone else.
                        // Wipe everything it carries (password, passkeys, logins, TOTP, sessions) before the real owner takes it over.
                        await security.WipeCredentialsAsync(user, removePassword: true);
                        user.EmailConfirmed = true;
                        await users.UpdateAsync(user);
                    }
                    await users.AddLoginAsync(user, new UserLoginInfo(provider, sub, provider));
                    await audit.WriteAsync("login.linked", user.Id, email, provider);
                }
                else
                {
                    if (!termsAccepted) return Error("terms_required");
                    user = await flows.CreateSellerUserAsync(email, null, true, true);
                    await users.AddLoginAsync(user, new UserLoginInfo(provider, sub, provider));
                    await audit.WriteAsync("signup.created", user.Id, email, "method=" + provider);
                }
            }
            if (!await flows.CanSignInAsync(user)) return Error("login_failed");

            var code = Crypto.RandomToken();
            db.ExternalExchanges.Add(new ExternalExchange
            {
                CodeHash = Crypto.Sha256(code), UserId = user.Id, Provider = provider, ReturnUrl = SafeRedirect.Sanitize(next),
                ExpiresAt = time.UtcNow().AddMinutes(2)
            });
            await db.SaveChangesAsync();
            var url = $"{front}/auth/callback?code={Uri.EscapeDataString(code)}";
            if (SafeRedirect.Sanitize(next) is { } n) url += "&next=" + Uri.EscapeDataString(n);
            return Results.Redirect(url);
        });

        a.MapPost("/exchange", async (ExchangeRequest r, HttpContext ctx, AppDbContext db, UserManager<AppUser> users, SessionService sessions,
            AuthFlows flows, TimeProvider time, IAuditLog audit, LoginThrottleService throttle, IOptions<LockoutSettings> lo) =>
        {
            IResult Fail(bool twoFactor = false) => Results.Json(new LoginFailure("Login failed", new AttemptsHint(null, false, false, null, null), twoFactor ? true : null), statusCode: 401);
            if (string.IsNullOrWhiteSpace(r.Code)) return Fail();
            var hash = Crypto.Sha256(r.Code);
            var ex = await db.ExternalExchanges.FirstOrDefaultAsync(e => e.CodeHash == hash);
            if (ex is null || ex.UsedAt is not null || ex.ExpiresAt <= time.UtcNow()) return Fail();
            var user = await users.FindByIdAsync(ex.UserId.ToString());
            if (user is null) return Fail();
            if (user.TwoFactorEnabled)
            {
                // A provider login must not bypass TOTP: the one-time code stays usable (2 min) until a valid second factor is presented.
                if (string.IsNullOrWhiteSpace(r.TotpCode) && string.IsNullOrWhiteSpace(r.RecoveryCode)) return Fail(true);
                var key = user.Email!;
                var t = await throttle.GetAsync(key);
                if (throttle.IsLocked(t)) return Fail();
                var attempts = await throttle.ReserveAttemptAsync(key);
                if (attempts > lo.Value.MaxFailures) return Fail();
                var good = !string.IsNullOrWhiteSpace(r.TotpCode)
                    ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.TotpCode.Replace(" ", ""))
                    : (await users.RedeemTwoFactorRecoveryCodeAsync(user, r.RecoveryCode!.Replace(" ", ""))).Succeeded;
                if (!good)
                {
                    await throttle.LockIfReachedAsync(key, attempts);
                    await audit.WriteAsync("login.failed", user.Id, user.Email, "method=" + ex.Provider + ";2fa");
                    return Fail(true);
                }
                await throttle.ResetAsync(key);
            }
            ex.UsedAt = time.UtcNow();
            await db.SaveChangesAsync();
            var s = await sessions.CreateAsync(user, ex.Provider.ToLowerInvariant(), AuthFlows.Ip(ctx), AuthFlows.Ua(ctx));
            await audit.WriteAsync("login.succeeded", user.Id, user.Email, "method=" + ex.Provider);
            return Results.Ok(await flows.BuildLoginAsync(user, s, ctx));
        }).RequireRateLimiting("auth");

        // Link a provider to the signed-in account: start must be authenticated -> frontend calls this to get a one-time link URL.
    }
}
