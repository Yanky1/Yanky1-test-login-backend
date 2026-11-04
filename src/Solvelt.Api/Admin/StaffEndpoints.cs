using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Admin;

public record StaffLoginRequest(string? Email, string? Password, string? TotpCode, string? RecoveryCode);
public record StaffTwoFactorRequest(string? Email, string? Password, string? Code);
public record StepUpRequest(string? Code, string? RecoveryCode);
public record AcceptStaffInviteRequest(string? Email, string? Token, string? Password);
public record InviteStaffRequest(string? Email, string? Role, string? Reason);
public record ChangeStaffRoleRequest(string? Role, string? Reason);
/// <summary>Staff reason (internal, audited). <c>ReasonShownToUser</c> is optional text the marketplace may show to the person who reported (moderation decisions only); it is never derived from <c>Reason</c>.</summary>
public record ReasonRequest(string? Reason, string? ReasonShownToUser = null);

public class StaffService(AppDbContext db, UserManager<AppUser> users, SessionService sessions, AdminAudit audit, IAuditLog plainAudit,
    Mailer mailer, TimeProvider time, IOptions<AdminOptions> opt, IOptions<AppOptions> app)
{
    public static bool TryRole(string? s, out StaffRole r) => Enum.TryParse(s, true, out r) && Enum.IsDefined(r);

    private async Task<int> ActiveSuperAdminsExceptAsync(Guid userId) =>
        await db.StaffMembers.AsNoTracking().CountAsync(s => s.Active && s.Role == StaffRole.SuperAdmin && s.UserId != userId);

    // One writer at a time for every change that can reduce the number of Super admins (this process), plus a PostgreSQL advisory lock for several instances.
    private static readonly SemaphoreSlim StaffLock = new(1, 1);

    private async Task<IResult> LockedAsync(StaffContext actor, Func<Task<IResult>> work)
    {
        await StaffLock.WaitAsync();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7403)");
            // the acting Super admin is re-read INSIDE the lock: an earlier request may just have demoted or deactivated them
            db.ChangeTracker.Clear();
            var me = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == actor.UserId);
            if (me is not { Active: true, Role: StaffRole.SuperAdmin }) return AdminHttp.Err(403, "forbidden", "Your staff role doesn't allow this.");
            var res = await work();
            await tx.CommitAsync();
            return res;
        }
        finally { StaffLock.Release(); }
    }

    /// <summary>The legacy Identity role "Admin" (old seller-status API, seller-kind sessions, no 2FA) is only ever REMOVED here: new Super admins never receive it,
    /// and a demoted or deactivated person loses it.</summary>
    private async Task RevokeLegacyRoleAsync(Guid userId)
    {
        var u = await users.FindByIdAsync(userId.ToString());
        if (u is not null && await users.IsInRoleAsync(u, "Admin")) await users.RemoveFromRoleAsync(u, "Admin");
    }

    public async Task<IResult> InviteAsync(StaffContext actor, string? email, string? role, string reason)
    {
        if (!Validation.IsEmail(email)) return Validation.Invalid("email", "Enter a valid email address.");
        if (!TryRole(role, out var r)) return Validation.Invalid("role", "Choose SuperAdmin, Support, Moderator or ReadOnly.");
        var e = Crypto.NormalizeEmail(email!);
        var existing = await users.FindByEmailAsync(e);
        if (existing is not null && await db.StaffMembers.AnyAsync(s => s.UserId == existing.Id && s.Active))
            return AdminHttp.Err(409, "already_staff", "That person is already staff.");
        var now = time.UtcNow();
        foreach (var old in await db.StaffInvitations.Where(i => i.Email == e && i.Status == InviteStatus.Pending).ToListAsync())
        { old.Status = InviteStatus.Revoked; old.ResolvedAt = now; }
        var token = Crypto.RandomToken();
        var inv = new StaffInvitation { Email = e, Role = r, TokenHash = Crypto.Sha256(token), InvitedBy = actor.UserId, CreatedAt = now, ExpiresAt = now.AddDays(opt.Value.InviteDays) };
        db.StaffInvitations.Add(inv);
        audit.Add(actor, "staff.invited", "staff_invitation", inv.Id, reason, after: new { email = Mask.EmailAddr(e), role = r.ToString() });
        await db.SaveChangesAsync();
        var link = $"{app.Value.FrontendBaseUrl.TrimEnd('/')}/admin/accept-invite?email={Uri.EscapeDataString(e)}&token={Uri.EscapeDataString(token)}";
        await mailer.SendAsync(Templates.Notification(e, "staff-invite", "You're invited to the Solvelt staff area", "Join the Solvelt staff team",
            [$"You were invited as {r}. The link works once and expires in {opt.Value.InviteDays} days. Two-factor authentication is required for staff."], "Accept invitation", link));
        return Results.Ok(new { id = inv.Id, email = e, role = r.ToString(), expiresAt = inv.ExpiresAt });
    }

    public async Task<IResult> AcceptAsync(string? email, string? token, string? password)
    {
        IResult Bad() => AdminHttp.Err(400, "invalid_invitation", "This invitation is invalid or has expired.");
        if (!Validation.IsEmail(email) || string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(password)) return Bad();
        var e = Crypto.NormalizeEmail(email!);
        var hash = Crypto.Sha256(token);
        var now = time.UtcNow();
        var inv = await db.StaffInvitations.FirstOrDefaultAsync(i => i.TokenHash == hash && i.Email == e && i.Status == InviteStatus.Pending && i.ExpiresAt > now);
        if (inv is null) return Bad();
        var user = await users.FindByEmailAsync(e);
        if (user is null)
        {
            var errs = PasswordRulesValidator.Describe(password, e);
            if (errs.Count > 0) return Validation.Invalid("password", errs.ToArray());
            user = new AppUser { UserName = e, Email = e, EmailConfirmed = true, HasPassword = true, CreatedAt = now };
            var res = await users.CreateAsync(user, password);
            if (!res.Succeeded) return Validation.Invalid("password", res.Errors.Select(x => x.Description).ToArray());
        }
        else
        {
            // An existing account joins staff only by proving its current password (the invitation alone never takes over an account).
            if (!user.HasPassword || !await users.CheckPasswordAsync(user, password)) return Bad();
            if (!user.EmailConfirmed) { user.EmailConfirmed = true; await users.UpdateAsync(user); }
        }
        // single use: claim the invitation atomically
        var claimed = await db.StaffInvitations.Where(i => i.Id == inv.Id && i.Status == InviteStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, InviteStatus.Accepted).SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.AcceptedUserId, user.Id));
        if (claimed == 0) return Bad();
        var member = await db.StaffMembers.FirstOrDefaultAsync(s => s.UserId == user.Id);
        if (member is null) db.StaffMembers.Add(new StaffMember { UserId = user.Id, Role = inv.Role, CreatedBy = inv.InvitedBy, CreatedAt = now, UpdatedAt = now });
        else { member.Role = inv.Role; member.Active = true; member.DeactivatedAt = null; member.UpdatedAt = now; }
        await db.SaveChangesAsync();
        await plainAudit.WriteAsync("admin.staff.invite_accepted", user.Id, e, $"role={inv.Role};invitation={inv.Id}");
        return Results.NoContent();
    }

    public Task<IResult> ChangeRoleAsync(StaffContext actor, Guid userId, string? role, string reason) => LockedAsync(actor, () => ChangeRoleCoreAsync(actor, userId, role, reason));
    public Task<IResult> SetActiveAsync(StaffContext actor, Guid userId, bool active, string reason) => LockedAsync(actor, () => SetActiveCoreAsync(actor, userId, active, reason));

    private async Task<IResult> ChangeRoleCoreAsync(StaffContext actor, Guid userId, string? role, string reason)
    {
        if (!TryRole(role, out var r)) return Validation.Invalid("role", "Choose SuperAdmin, Support, Moderator or ReadOnly.");
        if (userId == actor.UserId) return AdminHttp.Err(403, "self_change", "You can't change your own role.");
        var m = await db.StaffMembers.FirstOrDefaultAsync(s => s.UserId == userId);
        if (m is null) return Results.NotFound();
        if (m.Role == r) return Results.Ok(new { userId, role = r.ToString() });
        if (m.Active && m.Role == StaffRole.SuperAdmin && r != StaffRole.SuperAdmin && await ActiveSuperAdminsExceptAsync(userId) == 0)
            return AdminHttp.Err(409, "last_super_admin", "There must always be at least one Super admin.");
        var before = m.Role; m.Role = r; m.UpdatedAt = time.UtcNow();
        audit.Add(actor, "staff.role_changed", "staff", userId, reason, new { role = before.ToString() }, new { role = r.ToString() });
        await db.SaveChangesAsync();
        if (r != StaffRole.SuperAdmin) await RevokeLegacyRoleAsync(userId);
        return Results.Ok(new { userId, role = r.ToString() });
    }

    private async Task<IResult> SetActiveCoreAsync(StaffContext actor, Guid userId, bool active, string reason)
    {
        if (userId == actor.UserId) return AdminHttp.Err(403, "self_change", "You can't change your own staff access.");
        var m = await db.StaffMembers.FirstOrDefaultAsync(s => s.UserId == userId);
        if (m is null) return Results.NotFound();
        if (m.Active == active) return Results.Ok(new { userId, active });
        if (!active && m.Role == StaffRole.SuperAdmin && await ActiveSuperAdminsExceptAsync(userId) == 0)
            return AdminHttp.Err(409, "last_super_admin", "There must always be at least one Super admin.");
        m.Active = active; m.UpdatedAt = time.UtcNow(); m.DeactivatedAt = active ? null : time.UtcNow();
        audit.Add(actor, active ? "staff.reactivated" : "staff.deactivated", "staff", userId, reason, new { active = !active }, new { active });
        await db.SaveChangesAsync();
        if (!active) await sessions.RevokeAllAsync(userId, "staff-deactivated");
        if (!active || m.Role != StaffRole.SuperAdmin) await RevokeLegacyRoleAsync(userId);
        return Results.Ok(new { userId, active });
    }
}

public static class StaffEndpoints
{
    private static readonly AppUser Dummy = new();
    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(Dummy, "Dummy-Password-For-Timing-1!");
    private static IResult Generic() => Results.Json(new { error = "Login failed" }, statusCode: 401);

    /// <summary>Password check with the shared sign-in throttle (same lockout as the seller sign-in). Returns the staff user, or an error result.</summary>
    private static async Task<(AppUser? User, StaffMember? Staff, IResult? Error)> PasswordAsync(string? emailRaw, string? password,
        UserManager<AppUser> users, LoginThrottleService throttle, AppDbContext db, IAuditLog audit, IOptions<LockoutSettings> lo)
    {
        if (string.IsNullOrWhiteSpace(emailRaw) || string.IsNullOrEmpty(password) || emailRaw.Length > 254 || password.Length > 256) return (null, null, Generic());
        var email = Crypto.NormalizeEmail(emailRaw);
        var t = await throttle.GetAsync(email);
        if (throttle.IsLocked(t)) { await audit.WriteAsync("admin.login.blocked_locked", null, email); return (null, null, Generic()); }
        var attempts = await throttle.ReserveAttemptAsync(email);
        if (attempts > lo.Value.MaxFailures) return (null, null, Generic());
        var user = await users.FindByEmailAsync(email);
        var ok = false;
        if (user is not null && user.HasPassword) ok = await users.CheckPasswordAsync(user, password);
        else new PasswordHasher<AppUser>().VerifyHashedPassword(Dummy, DummyHash, password);
        var staff = ok && user!.EmailConfirmed ? await db.StaffMembers.FirstOrDefaultAsync(s => s.UserId == user.Id && s.Active) : null;
        if (staff is null)
        {
            await throttle.LockIfReachedAsync(email, attempts);
            await audit.WriteAsync("admin.login.failed", user?.Id, email);
            return (null, null, Generic());
        }
        await throttle.ReleaseAttemptAsync(email); // the second factor is the next step, not a failure
        return (user, staff, null);
    }

    public static void MapStaffAuthEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/auth");

        a.MapPost("/login", async (StaffLoginRequest r, HttpContext ctx, UserManager<AppUser> users, LoginThrottleService throttle, AppDbContext db,
            IAuditLog audit, IOptions<LockoutSettings> lo, StaffSessionService staffSessions, SessionService sessions, TimeProvider time) =>
        {
            var (user, staff, err) = await PasswordAsync(r.Email, r.Password, users, throttle, db, audit, lo);
            if (err is not null) return err;
            var email = Crypto.NormalizeEmail(r.Email!);
            if (!user!.TwoFactorEnabled)
                return AdminHttp.Err(403, "two_factor_setup_required", "Staff accounts need two-factor authentication. Set it up first.");
            if (string.IsNullOrWhiteSpace(r.TotpCode) && string.IsNullOrWhiteSpace(r.RecoveryCode))
                return Results.Json(new { error = "Login failed", twoFactorRequired = true }, statusCode: 401);
            var attempts = await throttle.ReserveAttemptAsync(email);
            var ok = attempts <= lo.Value.MaxFailures && (!string.IsNullOrWhiteSpace(r.TotpCode)
                ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.TotpCode.Replace(" ", ""))
                : (await users.RedeemTwoFactorRecoveryCodeAsync(user, r.RecoveryCode!.Replace(" ", ""))).Succeeded);
            if (!ok)
            {
                await throttle.LockIfReachedAsync(email, attempts);
                await audit.WriteAsync("admin.login.failed_2fa", user.Id, email);
                return Generic();
            }
            await throttle.ResetAsync(email);
            var (token, expiresIn, sid) = await staffSessions.CreateAsync(user, AuthFlows.Ip(ctx), AuthFlows.Ua(ctx));
            staff!.LastLoginAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("admin.login.succeeded", user.Id, email, $"role={staff.Role};session={sid}");
            return Results.Ok(new
            {
                tokenType = "Bearer", accessToken = token, expiresIn, email = user.Email, role = staff.Role.ToString(),
                permissions = StaffPermissions.Matrix[staff.Role].Select(p => p.ToString()).Order()
            });
        }).RequireRateLimiting("auth");

        a.MapPost("/2fa/setup", async (StaffTwoFactorRequest r, UserManager<AppUser> users, LoginThrottleService throttle, AppDbContext db, IAuditLog audit, IOptions<LockoutSettings> lo) =>
        {
            var (user, _, err) = await PasswordAsync(r.Email, r.Password, users, throttle, db, audit, lo);
            if (err is not null) return err;
            if (user!.TwoFactorEnabled) return AdminHttp.Err(409, "already_enabled", "Two-factor authentication is already enabled.");
            await users.ResetAuthenticatorKeyAsync(user);
            var key = (await users.GetAuthenticatorKeyAsync(user))!;
            return Results.Ok(new { sharedKey = key, otpAuthUri = $"otpauth://totp/Solvelt%20Staff:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer=Solvelt%20Staff&digits=6" });
        }).RequireRateLimiting("auth");

        a.MapPost("/2fa/enable", async (StaffTwoFactorRequest r, UserManager<AppUser> users, LoginThrottleService throttle, AppDbContext db, IAuditLog audit, IOptions<LockoutSettings> lo) =>
        {
            var (user, _, err) = await PasswordAsync(r.Email, r.Password, users, throttle, db, audit, lo);
            if (err is not null) return err;
            if (user!.TwoFactorEnabled) return AdminHttp.Err(409, "already_enabled", "Two-factor authentication is already enabled.");
            if (string.IsNullOrWhiteSpace(r.Code) || !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.Code.Replace(" ", "")))
                return Validation.Invalid("code", "That code isn't right.");
            await users.SetTwoFactorEnabledAsync(user, true);
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            await audit.WriteAsync("admin.2fa.enabled", user.Id, user.Email);
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireRateLimiting("auth");

        a.MapPost("/accept-invite", async (AcceptStaffInviteRequest r, StaffService svc) => await svc.AcceptAsync(r.Email, r.Token, r.Password)).RequireRateLimiting("auth");

        a.MapGet("/me", async (HttpContext ctx, AppDbContext db, IOptions<AdminOptions> o, TimeProvider time) =>
        {
            var s = ctx.Staff();
            var sess = await db.Sessions.AsNoTracking().FirstAsync(x => x.Id == s.SessionId);
            var stepUntil = sess.StepUpAt?.AddMinutes(o.Value.StepUpMinutes);
            return Results.Ok(new
            {
                userId = s.UserId, email = s.Email, role = s.Role.ToString(),
                permissions = StaffPermissions.Matrix[s.Role].Select(p => p.ToString()).Order(),
                sessionExpiresAt = sess.ExpiresAt, idleMinutes = o.Value.IdleMinutes,
                stepUpUntil = stepUntil is { } su && su > time.UtcNow() ? stepUntil : null
            });
        }).RequireStaff(StaffPermission.DashboardView, "read");

        a.MapPost("/step-up", async (StepUpRequest r, HttpContext ctx, UserManager<AppUser> users, AppDbContext db, LoginThrottleService throttle, IOptions<LockoutSettings> lo, AdminAudit audit, TimeProvider time) =>
        {
            var s = ctx.Staff();
            var user = (await users.FindByIdAsync(s.UserId.ToString()))!;
            var email = Crypto.NormalizeEmail(user.Email!);
            var attempts = await throttle.ReserveAttemptAsync(email);
            var ok = attempts <= lo.Value.MaxFailures && user.TwoFactorEnabled && (!string.IsNullOrWhiteSpace(r.Code)
                ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, r.Code.Replace(" ", ""))
                : !string.IsNullOrWhiteSpace(r.RecoveryCode) && (await users.RedeemTwoFactorRecoveryCodeAsync(user, r.RecoveryCode.Replace(" ", ""))).Succeeded);
            if (!ok) { await throttle.LockIfReachedAsync(email, attempts); return AdminHttp.Err(401, "step_up_failed", "That code isn't right."); }
            await throttle.ResetAsync(email);
            var now = time.UtcNow();
            await db.Sessions.Where(x => x.Id == s.SessionId).ExecuteUpdateAsync(x => x.SetProperty(y => y.StepUpAt, now));
            await audit.WriteAsync(s, "auth.step_up", "session", s.SessionId, null);
            return Results.Ok(new { stepUpUntil = now.AddMinutes(ctx.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.StepUpMinutes) });
        }).RequireStaff(StaffPermission.DashboardView, "write").RequireRateLimiting("auth");

        a.MapPost("/logout", async (HttpContext ctx, SessionService sessions) =>
        {
            await sessions.RevokeSessionAsync(ctx.Staff().SessionId, "staff-logout");
            return Results.NoContent();
        }).RequireStaff(StaffPermission.DashboardView, "write");
    }

    public static void MapStaffManagementEndpoints(this RouteGroupBuilder g)
    {
        var s = g.MapGroup("/staff");

        s.AGet("", async (AppDbContext db, UserManager<AppUser> users, TimeProvider time) =>
        {
            var members = await db.StaffMembers.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
            var ids = members.Select(m => m.UserId).ToList();
            var u = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Email, x.TwoFactorEnabled }).ToDictionaryAsync(x => x.Id);
            var now = time.UtcNow();
            var invites = await db.StaffInvitations.AsNoTracking().Where(i => i.Status == InviteStatus.Pending && i.ExpiresAt > now).OrderByDescending(i => i.CreatedAt).ToListAsync();
            return Results.Ok(new
            {
                members = members.Select(m => new { userId = m.UserId, email = u.GetValueOrDefault(m.UserId)?.Email, role = m.Role.ToString(), active = m.Active,
                    twoFactorEnabled = u.GetValueOrDefault(m.UserId)?.TwoFactorEnabled ?? false, m.CreatedAt, m.LastLoginAt, m.DeactivatedAt }),
                invitations = invites.Select(i => new { i.Id, i.Email, role = i.Role.ToString(), i.CreatedAt, i.ExpiresAt })
            });
        }, StaffPermission.StaffView);

        s.APost("/invitations", async (InviteStaffRequest r, HttpContext ctx, StaffService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.InviteAsync(ctx.Staff(), r.Email, r.Role, reason), StaffPermission.StaffManage);

        s.APost("/invitations/{id:guid}/revoke", async (Guid id, ReasonRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var inv = await db.StaffInvitations.FirstOrDefaultAsync(i => i.Id == id && i.Status == InviteStatus.Pending);
            if (inv is null) return Results.NotFound();
            inv.Status = InviteStatus.Revoked; inv.ResolvedAt = time.UtcNow();
            await audit.WriteAsync(ctx.Staff(), "staff.invite_revoked", "staff_invitation", id, reason);
            return Results.NoContent();
        }, StaffPermission.StaffManage);

        s.APut("/{userId:guid}/role", async (Guid userId, ChangeStaffRoleRequest r, HttpContext ctx, StaffService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.ChangeRoleAsync(ctx.Staff(), userId, r.Role, reason), StaffPermission.StaffManage);

        s.APost("/{userId:guid}/deactivate", async (Guid userId, ReasonRequest r, HttpContext ctx, StaffService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.SetActiveAsync(ctx.Staff(), userId, false, reason), StaffPermission.StaffManage);

        s.APost("/{userId:guid}/reactivate", async (Guid userId, ReasonRequest r, HttpContext ctx, StaffService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.SetActiveAsync(ctx.Staff(), userId, true, reason), StaffPermission.StaffManage);
    }
}
