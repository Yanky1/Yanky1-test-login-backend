using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;

namespace Solvelt.Api.Teams;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this RouteGroupBuilder g)
    {
        var s = g.MapGroup("/session").RequireAuthorization().RequireRateLimiting("team");

        s.MapGet("", async (ClaimsPrincipal p, HttpContext ctx, UserManager<AppUser> users, AccountContextService accounts) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            if (user is null) return Results.Unauthorized();
            return Results.Ok(await Describe(user, ctx.GetAccess(), accounts));
        });

        s.MapPost("/account", async (SwitchAccountRequest r, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccountContextService accounts,
            SessionService sessions, IAuditLog audit) =>
        {
            var user = await users.FindByIdAsync(p.UserId().ToString());
            var session = await db.Sessions.FirstOrDefaultAsync(x => x.Id == p.SessionId() && x.UserId == p.UserId());
            if (user is null || session is null) return Results.Unauthorized();
            var access = await accounts.ResolveAsync(user.Id, r.SellerId); // membership active + account usable, else 404 (same as unknown)
            if (access is null) return Results.NotFound();
            await accounts.SetActiveAsync(user, session, r.SellerId);
            await users.UpdateAsync(user);
            await audit.WriteAsync("session.account_switched", user.Id, null, $"session={session.Id};account={r.SellerId}");
            var token = await sessions.IssueAccessAsync(user, session.Id);
            var body = await Describe(user, access, accounts);
            return Results.Ok(new { tokenType = "Bearer", accessToken = token, expiresIn = sessions.AccessSeconds, body });
        });
    }

    /// <summary>The shared "who am I / which accounts" shape used by login, refresh, /account/me and /session.</summary>
    public static async Task<AccountsInfo> Describe(AppUser user, AccountAccess? active, AccountContextService accounts)
    {
        var list = await accounts.ListAsync(user.Id, user.LastAccountId);
        var cur = active is null ? null : list.FirstOrDefault(a => a.SellerId == active.SellerId);
        return new AccountsInfo(active?.SellerId, cur?.VendorName ?? "", cur?.AccountStatus, cur?.Role,
            list.Select(a => new AccountItem(a.SellerId, a.VendorName, a.Role, a.AccountStatus, a.BrandCount)).ToList());
    }
}

public record AccountItem(Guid SellerId, string VendorName, string Role, string AccountStatus, int BrandCount);
public record AccountsInfo(Guid? ActiveAccountId, string VendorName, string? AccountStatus, string? Role, List<AccountItem> Accounts);
