using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Orders;
using Solvelt.Api.Phone;
using Solvelt.Api.Services;

namespace Solvelt.Api.Admin;

public static class UserAdminEndpoints
{
    private static async Task<AppUser?> Find(UserManager<AppUser> users, Guid id) => await users.FindByIdAsync(id.ToString());

    /// <summary>Acting on an ACTIVE staff member's account (sign-out, lockout, reset, phone) is a staff-management action: Super admin with a recent step-up.</summary>
    private static async Task<IResult?> StaffTargetAsync(HttpContext ctx, AppDbContext db, Guid target)
    {
        if (!await db.StaffMembers.AsNoTracking().AnyAsync(m => m.UserId == target && m.Active)) return null;
        var me = ctx.Staff();
        var opt = ctx.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
        var time = ctx.RequestServices.GetRequiredService<TimeProvider>().UtcNow();
        if (!StaffPermissions.Has(me.Role, StaffPermission.StaffManage)) return AdminHttp.Err(403, "staff_target", "This account belongs to a staff member: only a Super admin can act on it.");
        var sess = await db.Sessions.AsNoTracking().Where(x => x.Id == me.SessionId).Select(x => x.StepUpAt).FirstOrDefaultAsync();
        return sess is { } su && time - su <= TimeSpan.FromMinutes(opt.StepUpMinutes) ? null : AdminHttp.Err(403, "step_up_required", "Confirm it's you again with your authenticator code to do this.");
    }

    public static void MapUserAdminEndpoints(this RouteGroupBuilder g)
    {
        var u = g.MapGroup("/users");

        u.AGet("", async (string? q, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 3) return Results.Ok(new { items = Array.Empty<object>() });
            var t = q.Trim();
            var query = db.Users.AsNoTracking().AsQueryable();
            if (Guid.TryParse(t, out var gid)) query = query.Where(x => x.Id == gid);
            else if (t.Any(char.IsLetter)) { var e = t.ToLowerInvariant(); query = query.Where(x => x.Email!.ToLower().Contains(e)); }
            else
            {
                var phone = PhoneFormat.Normalize(t) ?? t;
                query = query.Where(x => x.PhoneNumber == phone);
            }
            var rows = await query.OrderBy(x => x.Email).Take(25).Select(x => new
            {
                x.Id, x.Email, phone = x.PhoneNumber, x.EmailConfirmed, x.CreatedAt, isStaff = db.StaffMembers.Any(s => s.UserId == x.Id && s.Active)
            }).ToListAsync();
            return Results.Ok(new { items = rows.Select(r => new { r.Id, r.Email, phone = Mask.PhoneNo(r.phone), r.EmailConfirmed, r.CreatedAt, r.isStaff }) });
        }, StaffPermission.UsersView);

        u.AGet("/{id:guid}", async (Guid id, AppDbContext db, UserManager<AppUser> users, LoginThrottleService throttle, TimeProvider time) =>
        {
            var user = await Find(users, id);
            if (user is null) return Results.NotFound();
            var now = time.UtcNow();
            var memberships = await (from m in db.Memberships.AsNoTracking() join s in db.Sellers on m.SellerId equals s.Id where m.UserId == id
                                     select new { sellerId = s.Id, s.VendorName, role = m.Role.ToString(), status = m.Status.ToString(), accountStatus = s.Status.ToString() }).ToListAsync();
            var sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == id && s.RevokedAt == null).OrderByDescending(s => s.LastActivityAt).Take(20)
                .Select(s => new { s.Id, s.Kind, s.Method, s.IpAddress, s.UserAgent, s.CreatedAt, s.LastActivityAt }).ToListAsync();
            var t = user.Email is null ? null : await throttle.GetAsync(Crypto.NormalizeEmail(user.Email));
            var events = await db.AuditEvents.AsNoTracking().Where(e => e.UserId == id).OrderByDescending(e => e.At).Take(50)
                .Select(e => new { e.Type, e.At, e.IpAddress, e.Detail }).ToListAsync();
            var staff = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == id);
            return Results.Ok(new
            {
                id = user.Id, email = user.Email, emailConfirmed = user.EmailConfirmed, phone = Mask.PhoneNo(user.PhoneNumber), phoneConfirmed = user.PhoneNumberConfirmed,
                createdAt = user.CreatedAt, hasPassword = user.HasPassword,
                staff = staff is null ? null : new { role = staff.Role.ToString(), staff.Active },
                memberships,
                sessions = sessions.Select(s => new { s.Id, s.Kind, s.Method, s.IpAddress, userAgent = s.UserAgent, s.CreatedAt, s.LastActivityAt }),
                security = new
                {
                    twoFactorEnabled = user.TwoFactorEnabled, passkeys = (await users.GetPasskeysAsync(user)).Count, externalLogins = (await users.GetLoginsAsync(user)).Count,
                    hasPhonePin = await db.PhoneCredentials.AnyAsync(c => c.UserId == id)
                },
                lockout = new { locked = throttle.IsLocked(t), failures = t?.Failures ?? 0, lockedUntil = t?.LockedUntil is { } lu && lu > now ? lu : (DateTime?)null },
                recentSecurityEvents = events.Select(e => new { e.Type, e.At, e.IpAddress, detail = Mask.Text(e.Detail) })
            });
        }, StaffPermission.UsersView);

        u.APost("/{id:guid}/sign-out-everywhere", async (Guid id, ReasonRequest r, HttpContext ctx, UserManager<AppUser> users, SessionService sessions, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (await StaffTargetAsync(ctx, ctx.RequestServices.GetRequiredService<AppDbContext>(), id) is { } blocked) return blocked;
            var user = await Find(users, id); if (user is null) return Results.NotFound();
            await sessions.RevokeAllAsync(id, "staff-sign-out");
            await audit.WriteAsync(ctx.Staff(), "user.signed_out_everywhere", "user", id, reason);
            return Results.NoContent();
        }, StaffPermission.UsersAct);

        u.APost("/{id:guid}/clear-lockout", async (Guid id, ReasonRequest r, HttpContext ctx, UserManager<AppUser> users, LoginThrottleService throttle, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (await StaffTargetAsync(ctx, ctx.RequestServices.GetRequiredService<AppDbContext>(), id) is { } blocked) return blocked;
            var user = await Find(users, id); if (user is null) return Results.NotFound();
            var email = Crypto.NormalizeEmail(user.Email!);
            var before = await throttle.GetAsync(email);
            await throttle.ResetAsync(email);
            await audit.WriteAsync(ctx.Staff(), "user.lockout_cleared", "user", id, reason, new { failures = before?.Failures ?? 0 });
            return Results.NoContent();
        }, StaffPermission.UsersAct);

        u.APost("/{id:guid}/send-password-reset", async (Guid id, ReasonRequest r, HttpContext ctx, UserManager<AppUser> users, Mailer mailer, IOptions<AppOptions> app, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (await StaffTargetAsync(ctx, ctx.RequestServices.GetRequiredService<AppDbContext>(), id) is { } blocked) return blocked;
            var user = await Find(users, id); if (user is null) return Results.NotFound();
            var email = Crypto.NormalizeEmail(user.Email!);
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = $"{app.Value.FrontendBaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(email)}&token={WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))}";
            await mailer.SendAsync(Templates.ResetPassword(email, link));
            await audit.WriteAsync(ctx.Staff(), "user.password_reset_sent", "user", id, reason);
            return Results.NoContent();
        }, StaffPermission.UsersAct);

        u.APost("/{id:guid}/remove-phone", async (Guid id, ReasonRequest r, HttpContext ctx, UserManager<AppUser> users, AppDbContext db, SessionService sessions, Mailer mailer, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (await StaffTargetAsync(ctx, ctx.RequestServices.GetRequiredService<AppDbContext>(), id) is { } blocked) return blocked;
            var user = await Find(users, id); if (user is null) return Results.NotFound();
            if (string.IsNullOrEmpty(user.PhoneNumber)) return AdminHttp.Err(409, "no_phone", "This user has no phone number.");
            var masked = Mask.PhoneNo(user.PhoneNumber);
            user.PhoneNumber = null; user.PhoneNumberConfirmed = false;
            await users.UpdateAsync(user);
            await db.PhoneCredentials.Where(c => c.UserId == id).ExecuteDeleteAsync();
            await db.PhoneLinks.Where(l => l.UserId == id).ExecuteDeleteAsync();
            await db.PhoneStates.Where(p => p.UserId == id).ExecuteUpdateAsync(p => p.SetProperty(x => x.UserId, (Guid?)null).SetProperty(x => x.AuthUntil, (DateTime?)null));
            await audit.WriteAsync(ctx.Staff(), "user.phone_removed", "user", id, reason, new { phone = masked });
            if (user.Email is { } e) await mailer.SendAsync(Templates.Notification(e, "phone-removed", "Your phone number was removed", "Phone number removed",
                ["Solvelt support removed the phone number from your account. You can add a number again in Settings."]));
            return Results.NoContent();
        }, StaffPermission.UsersAct);

        u.APost("/{id:guid}/disable-2fa", async (Guid id, ReasonRequest r, HttpContext ctx, UserManager<AppUser> users, SessionService sessions, Mailer mailer, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (id == ctx.Staff().UserId) return AdminHttp.Err(403, "self_change", "You can't disable your own two-factor authentication.");
            var user = await Find(users, id); if (user is null) return Results.NotFound();
            if (!user.TwoFactorEnabled) return AdminHttp.Err(409, "not_enabled", "Two-factor authentication isn't enabled for this user.");
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user);
            await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
            await users.UpdateSecurityStampAsync(user);
            await sessions.RevokeAllAsync(id, "staff-2fa-disabled");
            await audit.WriteAsync(ctx.Staff(), "user.2fa_disabled", "user", id, reason);
            if (user.Email is { } e) await mailer.SendAsync(Templates.Notification(e, "2fa-disabled-by-staff", "Two-factor authentication was turned off", "Two-factor authentication turned off",
                ["Solvelt support turned off two-factor authentication on your account and signed you out everywhere. Sign in and turn it on again as soon as you can. If you did not ask for this, contact support."]));
            return Results.NoContent();
        }, StaffPermission.UsersDisable2fa);
    }

    // ---------------------------------------------------------------------------------------------------------------- dashboard

    public static void MapDashboardEndpoints(this RouteGroupBuilder g)
    {
        g.AGet("/dashboard", async (AppDbContext db, ModerationService moderation, TimeProvider time) =>
        {
            var now = time.UtcNow();
            var today = now.Date;
            var since7 = now.AddDays(-7); var since30 = now.AddDays(-30); var since24 = now.AddHours(-24);
            await moderation.SyncAsync();
            var waiting = db.Sellers.AsNoTracking().Where(s => s.Status == SellerStatus.PendingReview || s.Status == SellerStatus.NeedsInfo);
            var submitted = await db.Sellers.AsNoTracking().Where(s => s.Status == SellerStatus.PendingReview && s.SubmittedAt != null).Select(s => s.SubmittedAt!.Value).ToListAsync();
            var oldest = submitted.Count == 0 ? (double?)null : Math.Floor((now - submitted.Min()).TotalDays);
            var avg = submitted.Count == 0 ? (double?)null : Math.Round(submitted.Average(d => (now - d).TotalDays), 1);

            // 14-day trends (UTC days)
            var days = Enumerable.Range(0, 14).Select(i => today.AddDays(-13 + i)).ToList();
            var from = days[0];
            var sellersByDay = (await db.Sellers.AsNoTracking().Where(s => s.CreatedAt >= from).Select(s => s.CreatedAt).ToListAsync()).GroupBy(d => d.Date).ToDictionary(x => x.Key, x => x.Count());
            var ordersByDay = (await db.Orders.AsNoTracking().Where(o => o.PlacedAt >= from && !db.DemoRecords.Any(d => d.EntityId == o.Id)).Select(o => o.PlacedAt).ToListAsync()).GroupBy(d => d.Date).ToDictionary(x => x.Key, x => x.Count());
            var bookingsByDay = (await db.Bookings.AsNoTracking().Where(o => o.CreatedAt >= from && !db.DemoRecords.Any(d => d.EntityId == o.Id)).Select(o => o.CreatedAt).ToListAsync()).GroupBy(d => d.Date).ToDictionary(x => x.Key, x => x.Count());
            var trend = days.Select(d => new { date = d.ToString("yyyy-MM-dd"), newSellers = sellersByDay.GetValueOrDefault(d), orders = ordersByDay.GetValueOrDefault(d), bookings = bookingsByDay.GetValueOrDefault(d) });

            var sms = await db.AuditEvents.AsNoTracking().Where(e => e.At >= since24 && (e.Type == "sms.sent" || e.Type == "sms.blocked" || e.Type == "sms.failed")).GroupBy(e => e.Type).Select(x => new { x.Key, N = x.Count() }).ToListAsync();
            var calls = await db.AuditEvents.CountAsync(e => e.At >= since24 && (e.Type == "phone.signin_succeeded" || e.Type == "phone.signin_failed"));
            return Results.Ok(new
            {
                generatedAt = now,
                applications = new { waiting = await waiting.CountAsync(), pendingReview = submitted.Count, needsInfo = await db.Sellers.CountAsync(s => s.Status == SellerStatus.NeedsInfo), oldestPendingDays = oldest, averagePendingDays = avg },
                sellers = new { newLast7Days = await db.Sellers.CountAsync(s => s.CreatedAt >= since7), newLast30Days = await db.Sellers.CountAsync(s => s.CreatedAt >= since30),
                    approved = await db.Sellers.CountAsync(s => s.Status == SellerStatus.Approved), suspended = await db.Sellers.CountAsync(s => s.Status == SellerStatus.Suspended) },
                listings = new { active = await db.Products.CountAsync(p => p.Status == ProductStatus.Active && !db.DemoRecords.Any(d => d.EntityId == p.Id)), activeServices = await db.Products.CountAsync(p => p.Status == ProductStatus.Active && p.Type == ListingType.Service && !db.DemoRecords.Any(d => d.EntityId == p.Id)),
                    hiddenByStaff = await db.Products.CountAsync(p => p.ModeratedAt != null) },
                today = new { orders = await db.Orders.CountAsync(o => o.PlacedAt >= today && !db.DemoRecords.Any(d => d.EntityId == o.Id)), bookings = await db.Bookings.CountAsync(b => b.CreatedAt >= today && !db.DemoRecords.Any(d => d.EntityId == b.Id)) },
                openReports = await db.ModerationReports.CountAsync(r => r.Status == ReportStatus.Open),
                integrations = new
                {
                    deadLetters = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Dead),
                    retrying = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Pending && m.Attempts > 0),
                    pending = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Pending),
                    inboundRejectedLast24h = await db.AuditEvents.CountAsync(e => e.At >= since24 && e.Type == "inbound.rejected")
                },
                phone = new { smsSentLast24h = sms.FirstOrDefault(x => x.Key == "sms.sent")?.N ?? 0, smsBlockedLast24h = sms.FirstOrDefault(x => x.Key == "sms.blocked")?.N ?? 0,
                    smsFailedLast24h = sms.FirstOrDefault(x => x.Key == "sms.failed")?.N ?? 0, phoneSignInsLast24h = calls },
                trend
            });
        }, StaffPermission.DashboardView);
    }
}
