using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;

namespace Solvelt.Api.Teams;

public record InviteRequest(string? Email, string? Role, bool? AllBrands, List<Guid>? BrandIds);
public record UpdateMemberRequest(string? Role, bool? AllBrands, List<Guid>? BrandIds, string? Status);
public record TransferRequest(Guid MemberId, string? CurrentPassword, string? StepUpCode, string? TotpCode);
public record SwitchAccountRequest(Guid SellerId);
public record NewAccountRequest(bool AcceptTerms);

public static class TeamEndpoints
{
    public static void MapTeamEndpoints(this RouteGroupBuilder g)
    {
        var t = g.MapGroup("/team").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("team");

        t.MapGet("/members", async (HttpContext ctx, TeamService svc) => Results.Ok(await svc.ListMembersAsync(ctx.GetAccess()!)))
            .RequirePermission(Permission.MembersRead);

        t.MapPatch("/members/{id:guid}", async (Guid id, UpdateMemberRequest r, HttpContext ctx, TeamService svc) =>
        {
            AccountRole? role = null;
            if (r.Role is not null) { if (!TeamService.TryRole(r.Role, out var rr)) return Validation.Invalid("role", "Unknown role."); role = rr; }
            MembershipStatus? status = null;
            if (r.Status is not null) { if (!Enum.TryParse<MembershipStatus>(r.Status, true, out var ss) || !Enum.IsDefined(ss)) return Validation.Invalid("status", "Unknown status."); status = ss; }
            return (await svc.ChangeMemberAsync(ctx.GetAccess()!, id, role, r.AllBrands, r.BrandIds, status)).ToHttp();
        }).RequirePermission(Permission.MembersManage);

        t.MapDelete("/members/{id:guid}", async (Guid id, HttpContext ctx, TeamService svc) =>
            (await svc.RemoveMemberAsync(ctx.GetAccess()!, id)).ToHttp()).RequirePermission(Permission.MembersManage);

        t.MapPost("/leave", async (HttpContext ctx, TeamService svc) => (await svc.LeaveAsync(ctx.GetAccess()!)).ToHttp()).RequireActiveAccount();

        t.MapPost("/transfer-ownership", async (TransferRequest r, HttpContext ctx, UserManager<AppUser> users, OneTimeCodeService codes,
            Email.Mailer mailer, TeamService svc, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var user = await users.FindByIdAsync(a.UserId.ToString());
            if (user is null) return Results.Unauthorized();
            // Step-up re-authentication: password (+ TOTP when enabled), or a code mailed to the current address for passwordless accounts.
            static IResult Denied() => Results.Json(new { type = "about:blank", title = "Confirmation failed", status = 400, code = "stepup_failed" }, statusCode: 400);
            if (user.HasPassword)
            {
                if (string.IsNullOrEmpty(r.CurrentPassword) || !await users.CheckPasswordAsync(user, r.CurrentPassword)) { await audit.WriteAsync("team.stepup_failed", user.Id); return Denied(); }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(r.StepUpCode))
                {
                    var su = await codes.IssueAsync(user.Id, "team-stepup");
                    if (su.Code is not null) await mailer.SendAsync(TeamTemplates.StepUp(user.Email!, su.Code));
                    return Results.Json(new { stepUpRequired = true, message = "We sent a confirmation code to your email address." }, statusCode: 202);
                }
                var (o, _) = await codes.VerifyAsync(user.Id, "team-stepup", r.StepUpCode);
                if (o != OneTimeCodeService.VerifyOutcome.Ok) { await audit.WriteAsync("team.stepup_failed", user.Id); return Denied(); }
            }
            if (user.TwoFactorEnabled && (string.IsNullOrWhiteSpace(r.TotpCode) ||
                !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.TotpCode.Replace(" ", ""))))
            { await audit.WriteAsync("team.stepup_failed", user.Id); return Denied(); }
            return (await svc.TransferOwnershipAsync(a, r.MemberId)).ToHttp();
        }).RequirePermission(Permission.OwnershipTransfer).RequireRateLimiting("sensitive");

        // ---- invitations (manage)
        t.MapGet("/invitations", async (HttpContext ctx, TeamService svc) => Results.Ok(await svc.ListInvitesAsync(ctx.GetAccess()!)))
            .RequirePermission(Permission.MembersRead);

        t.MapPost("/invitations", async (InviteRequest r, HttpContext ctx, TeamService svc) =>
        {
            if (!Validation.IsEmail(r.Email)) return Validation.Invalid("email", "Enter a valid email address.");
            if (!TeamService.TryRole(r.Role, out var role)) return Validation.Invalid("role", "Choose a role.");
            var res = await svc.InviteAsync(ctx.GetAccess()!, r.Email!, role, r.AllBrands, r.BrandIds);
            return res.Ok ? Results.Json(res.Data, statusCode: 202) : res.ToHttp(); // same 202 whether or not the address has a login
        }).RequirePermission(Permission.MembersManage);

        t.MapPost("/invitations/{id:guid}/resend", async (Guid id, HttpContext ctx, TeamService svc) =>
        {
            var res = await svc.ResendAsync(ctx.GetAccess()!, id);
            return res.Ok ? Results.Json(res.Data, statusCode: 202) : res.ToHttp();
        }).RequirePermission(Permission.MembersManage);

        t.MapDelete("/invitations/{id:guid}", async (Guid id, HttpContext ctx, TeamService svc) =>
            (await svc.RevokeAsync(ctx.GetAccess()!, id)).ToHttp()).RequirePermission(Permission.MembersManage);
    }
}
