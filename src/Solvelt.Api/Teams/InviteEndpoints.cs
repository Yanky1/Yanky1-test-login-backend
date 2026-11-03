using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;

namespace Solvelt.Api.Teams;

public record JoinRequest(string? Password, bool AcceptTerms);

/// <summary>Invitation endpoints used by the invitee: preview/decline/join are token-based (anonymous), accept needs a signed-in user.</summary>
public static class InviteEndpoints
{
    public static void MapInviteEndpoints(this RouteGroupBuilder g)
    {
        var i = g.MapGroup("/invitations").RequireRateLimiting("sensitive");
        IResult NotFound() => Results.Json(new { type = "about:blank", title = "This invitation link isn't valid.", status = 404, code = "invite_not_found" }, statusCode: 404);

        // Anonymous: what the link is for. Unknown tokens are a uniform 404.
        i.MapGet("/{token}", async (string token, TeamService svc) =>
            await svc.FindByTokenAsync(token) is { } inv ? Results.Ok(await svc.PreviewAsync(inv)) : NotFound());

        i.MapPost("/{token}/decline", async (string token, TeamService svc) =>
            await svc.FindByTokenAsync(token) is { } inv ? (await svc.DeclineAsync(inv)).ToHttp() : NotFound());

        i.MapPost("/{token}/accept", async (string token, ClaimsPrincipal p, UserManager<AppUser> users, TeamService svc) =>
        {
            if (await svc.FindByTokenAsync(token) is not { } inv) return NotFound();
            var user = await users.FindByIdAsync(p.UserId().ToString());
            return user is null ? Results.Unauthorized() : (await svc.AcceptAsync(inv, user)).ToHttp();
        }).RequireAuthorization();

        // New-user join: possession of the emailed link proves the mailbox (the link is single-use, random, hashed at rest, expires in 7 days),
        // so the account is created with a verified email and no verification code; no seller sign-up wizard.
        i.MapPost("/{token}/join", async (string token, JoinRequest r, HttpContext ctx, UserManager<AppUser> users, TeamService svc, AuthFlows flows,
            SessionService sessions, AccountSecurityService security, IAuditLog audit, TimeProvider time) =>
        {
            if (await svc.FindByTokenAsync(token) is not { } inv) return NotFound();
            var state = svc.StateOf(inv);
            if (state != "pending") return (await svc.AcceptAsync(inv, new AppUser())).ToHttp(); // yields the matching 410
            if (!r.AcceptTerms) return Validation.Invalid("acceptTerms", "You must accept the Terms.");
            if (string.IsNullOrEmpty(r.Password) || r.Password.Length > 256) return Validation.Invalid("password", "Enter a password.");
            var existing = await users.FindByEmailAsync(inv.Email);
            if (existing is { EmailConfirmed: true })
                return Results.Json(new { type = "about:blank", title = "Sign in to accept this invitation.", status = 409, code = "sign_in_required" }, statusCode: 409);
            // An UNVERIFIED user with this address is a pre-registration by somebody who never proved the mailbox (web or phone sign-up). It is
            // deleted outright (login, phone number + PIN, seller account, memberships, sessions) and the real owner gets a brand-new user,
            // exactly like a fresh web sign-up does. Never adopt it: whatever the squatter planted would survive.
            if (existing is not null) await security.DeleteUnverifiedAsync(existing);
            var user = new AppUser { UserName = inv.Email, Email = inv.Email, EmailConfirmed = true, HasPassword = true, CreatedAt = time.UtcNow() };
            var res = await users.CreateAsync(user, r.Password);
            if (!res.Succeeded) return Validation.Invalid("password", res.Errors.Select(e => e.Description).ToArray());
            await users.AddToRoleAsync(user, "Seller");
            var accepted = await svc.AcceptAsync(inv, user);
            if (!accepted.Ok) return accepted.ToHttp();
            await audit.WriteAsync("signup.created", user.Id, user.Email, "method=invite");
            var session = await sessions.CreateAsync(user, "invite", AuthFlows.Ip(ctx), AuthFlows.Ua(ctx));
            return Results.Ok(await flows.BuildLoginAsync(user, session, ctx));
        });

        // Signed-in user: pending invitations for their verified email (the account picker / "no accounts" screen).
        g.MapGet("/account/invitations", async (ClaimsPrincipal p, UserManager<AppUser> users, TeamService svc) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            return user is null ? Results.Unauthorized() : Results.Ok(await svc.PendingForAsync(user));
        }).RequireAuthorization().RequireRateLimiting("team");

        g.MapPost("/account/invitations/{id:guid}/accept", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, TeamService svc, AppDbContext db) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            var inv = await db.Invitations.FirstOrDefaultAsync(x => x.Id == id);
            if (user is null) return Results.Unauthorized();
            // Not yours (different email) is indistinguishable from missing.
            if (inv is null || inv.Email != Crypto.NormalizeEmail(user.Email ?? "")) return NotFound();
            return (await svc.AcceptAsync(inv, user)).ToHttp();
        }).RequireAuthorization().RequireRateLimiting("team");

        g.MapPost("/account/invitations/{id:guid}/decline", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, TeamService svc, AppDbContext db) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            var inv = await db.Invitations.FirstOrDefaultAsync(x => x.Id == id);
            if (user is null) return Results.Unauthorized();
            if (inv is null || inv.Email != Crypto.NormalizeEmail(user.Email ?? "")) return NotFound();
            return (await svc.DeclineAsync(inv)).ToHttp();
        }).RequireAuthorization().RequireRateLimiting("team");

        // Create an additional seller account of one's own (goes through the normal wizard + approval).
        g.MapPost("/account/seller-accounts", async (NewAccountRequest r, ClaimsPrincipal p, UserManager<AppUser> users, TeamService svc, AppDbContext db,
            IAuditLog audit, TimeProvider time, Microsoft.Extensions.Options.IOptions<Options.AppOptions> app) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            if (!r.AcceptTerms) return Validation.Invalid("acceptTerms", "You must accept the Terms.");
            if (await db.Sellers.CountAsync(s => s.UserId == user.Id && s.Status == SellerStatus.Draft) >= 3)
                return Results.Json(new { type = "about:blank", title = "Finish your existing draft accounts first.", status = 409, code = "too_many_drafts" }, statusCode: 409);
            var acc = svc.NewAccount(user.Id, 3, app.Value.TermsVersion, time.UtcNow());
            if (!await users.IsInRoleAsync(user, "Seller")) await users.AddToRoleAsync(user, "Seller");
            await db.SaveChangesAsync();
            await audit.WriteAsync("account.created", user.Id, null, $"account={acc.Id}");
            return Results.Json(new { sellerId = acc.Id }, statusCode: 201);
        }).RequireAuthorization("VerifiedEmail").RequireRateLimiting("team");
    }
}
