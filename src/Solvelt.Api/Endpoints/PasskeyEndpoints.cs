using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Services;

namespace Solvelt.Api.Endpoints;

public record PasskeyEmailRequest(string? Email, bool AcceptTerms);
public record PasskeyCredentialRequest(System.Text.Json.JsonElement? Credential, string? Email, bool AcceptTerms, string? Name);
public record PasskeyOptionsRequest(string? Email);
public record RenameRequest(string? Name);

public static class PasskeyEndpoints
{
    private static async Task<PasskeyAttestationResult> SafeAttest(SignInManager<AppUser> sm, PasskeyCredentialRequest r)
    {
        try { return await sm.PerformPasskeyAttestationAsync(CredJson(r)); }
        catch (Exception) { return PasskeyAttestationResult.Fail(new PasskeyException("Invalid credential")); }
    }

    private static string CredJson(PasskeyCredentialRequest r) => r.Credential?.GetRawText() ?? "";

    public static void MapPasskeyEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/auth");

        // ---- sign-in (discoverable credentials / conditional UI)
        a.MapPost("/passkey/options", async (PasskeyOptionsRequest? r, SignInManager<AppUser> sm) =>
        {
            var options = await sm.MakePasskeyRequestOptionsAsync(null); // always discoverable: never reveals whether an email exists
            return Results.Content(options, "application/json");
        }).RequireRateLimiting("auth");

        a.MapPost("/passkey/signin", async (PasskeyCredentialRequest r, HttpContext ctx, SignInManager<AppUser> sm, UserManager<AppUser> users,
            AppDbContext db, SessionService sessions, AuthFlows flows, IAuditLog audit) =>
        {
            var fail = () => Results.Json(new LoginFailure("Login failed", new AttemptsHint(null, false, false, null, null)), statusCode: 401);
            if (r.Credential is null) return fail();
            PasskeyAssertionResult<AppUser> res;
            try { res = await sm.PerformPasskeyAssertionAsync(CredJson(r)); } catch (Exception) { return fail(); }
            if (!res.Succeeded || res.User is null) { await audit.WriteAsync("passkey.signin_failed"); return fail(); }
            var user = res.User;
            var seller = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user.Id);
            if (!await users.IsInRoleAsync(user, "Admin") && (seller is null || seller.Status is SellerStatus.Rejected or SellerStatus.Suspended)) return fail();
            await users.AddOrUpdatePasskeyAsync(user, res.Passkey);
            var s = await sessions.CreateAsync(user, "passkey", AuthFlows.Ip(ctx), AuthFlows.Ua(ctx));
            await audit.WriteAsync("login.succeeded", user.Id, user.Email, "method=passkey");
            return Results.Ok(await flows.BuildLoginAsync(user, s, ctx));
        }).RequireRateLimiting("auth");

        // ---- sign-up with a passkey
        a.MapPost("/signup/passkey/options", async (PasskeyEmailRequest r, SignInManager<AppUser> sm, UserManager<AppUser> users) =>
        {
            if (!Validation.IsEmail(r.Email)) return Validation.Invalid("email", "Enter a valid email address.");
            if (!r.AcceptTerms) return Validation.Invalid("acceptTerms", "You must accept the Terms.");
            var email = Crypto.NormalizeEmail(r.Email!);
            if (await users.FindByEmailAsync(email) is not null)
                return Validation.Invalid("email", "We couldn't create an account with this email. If you already have one, sign in or reset your password.");
            var entity = new PasskeyUserEntity { Id = Guid.NewGuid().ToString(), Name = email, DisplayName = email };
            var options = await sm.MakePasskeyCreationOptionsAsync(entity);
            return Results.Content(options, "application/json");
        }).RequireRateLimiting("auth");

        a.MapPost("/signup/passkey/complete", async (PasskeyCredentialRequest r, HttpContext ctx, SignInManager<AppUser> sm, UserManager<AppUser> users,
            AuthFlows flows, SessionService sessions, OneTimeCodeService codes, Mailer mailer, IAuditLog audit) =>
        {
            if (!Validation.IsEmail(r.Email) || !r.AcceptTerms || r.Credential is null) return Results.Problem("Invalid request.", statusCode: 400);
            var email = Crypto.NormalizeEmail(r.Email!);
            if (await users.FindByEmailAsync(email) is not null) return Results.Problem("We couldn't create an account with this email.", statusCode: 400);
            var att = await SafeAttest(sm, r);
            if (!att.Succeeded) return Results.Problem("Passkey registration failed.", statusCode: 400);
            AppUser user;
            try { user = await flows.CreateSellerUserAsync(email, null, false, true, Guid.TryParse(att.UserEntity?.Id, out var uid) ? uid : null); }
            catch (AccountCreationException) { return Results.Problem("We couldn't create an account with this email.", statusCode: 400); }
            att.Passkey.Name = "Passkey";
            await users.AddOrUpdatePasskeyAsync(user, att.Passkey);
            var issued = await codes.IssueAsync(user.Id, "verify-email");
            if (issued.Code is not null) await mailer.SendAsync(Templates.VerifyCode(email, issued.Code));
            await audit.WriteAsync("signup.created", user.Id, email, "method=passkey");
            var s = await sessions.CreateAsync(user, "passkey", AuthFlows.Ip(ctx), AuthFlows.Ua(ctx));
            return Results.Json(await flows.BuildLoginAsync(user, s, ctx), statusCode: 201);
        }).RequireRateLimiting("auth");

        // ---- manage passkeys (authenticated)
        var m = g.MapGroup("/account/passkeys").RequireAuthorization();

        m.MapGet("", async (ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            return Results.Ok((await users.GetPasskeysAsync(user)).Select(k => new
            {
                id = WebEncoders.Base64UrlEncode(k.CredentialId), name = k.Name ?? "Passkey", createdAt = k.CreatedAt,
                backedUp = k.IsBackedUp
            }));
        });

        m.MapPost("/options", async (ClaimsPrincipal p, SignInManager<AppUser> sm, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            var entity = new PasskeyUserEntity { Id = user.Id.ToString(), Name = user.Email!, DisplayName = user.Email! };
            var options = await sm.MakePasskeyCreationOptionsAsync(entity);
            return Results.Content(options, "application/json");
        });

        m.MapPost("", async (PasskeyCredentialRequest r, ClaimsPrincipal p, SignInManager<AppUser> sm, UserManager<AppUser> users, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null || r.Credential is null) return Results.Unauthorized();
            var att = await SafeAttest(sm, r);
            if (!att.Succeeded) return Results.Problem("Passkey registration failed.", statusCode: 400);
            var name = string.IsNullOrWhiteSpace(r.Name) ? "Passkey" : r.Name.Trim();
            att.Passkey.Name = name.Length > 60 ? name[..60] : name;
            await users.AddOrUpdatePasskeyAsync(user, att.Passkey);
            await audit.WriteAsync("passkey.added", user.Id, user.Email);
            return Results.Ok(new { id = WebEncoders.Base64UrlEncode(att.Passkey.CredentialId), name = att.Passkey.Name });
        });

        m.MapPatch("/{id}", async (string id, RenameRequest r, ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 60) return Validation.Invalid("name", "Enter a name up to 60 characters.");
            byte[] cid;
            try { cid = WebEncoders.Base64UrlDecode(id); } catch (FormatException) { return Results.NotFound(); }
            var key = await users.GetPasskeyAsync(user, cid);
            if (key is null) return Results.NotFound();
            key.Name = r.Name.Trim();
            await users.AddOrUpdatePasskeyAsync(user, key);
            return Results.NoContent();
        });

        m.MapDelete("/{id}", async (string id, ClaimsPrincipal p, UserManager<AppUser> users, AuthFlows flows, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            byte[] cid;
            try { cid = WebEncoders.Base64UrlDecode(id); } catch (FormatException) { return Results.NotFound(); }
            if (await users.GetPasskeyAsync(user, cid) is null) return Results.NotFound();
            if (await flows.SignInMethodCountAsync(user) <= 1)
                return Results.Problem("You can't remove your last sign-in method.", statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "last_method" });
            await users.RemovePasskeyAsync(user, cid);
            await audit.WriteAsync("passkey.removed", user.Id, user.Email);
            return Results.NoContent();
        });
    }
}
