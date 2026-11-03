using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Admin;

public record DecisionRequest(string? Reason, string? Message);
public record AssignRequest(Guid? AssignTo, string? Reason);
public record NoteRequest(string? Text);
public record TransferRequest(Guid? NewOwnerUserId, string? Reason);

/// <summary>Staff actions on seller accounts. Status changes go through <see cref="SellerStatusService"/> (the same state machine and mails as the seller flow).</summary>
public class SellerAdminService(AppDbContext db, SellerStatusService status, SessionService sessions, UserManager<AppUser> users, Mailer mailer,
    AdminAudit audit, OneTimeCodeService codes, TimeProvider time)
{
    private async Task<List<AppUser>> OwnersAsync(Guid sellerId)
    {
        var ids = await db.Memberships.Where(m => m.SellerId == sellerId && m.Role == AccountRole.Owner && m.Status == MembershipStatus.Active).Select(m => m.UserId).ToListAsync();
        var list = new List<AppUser>();
        foreach (var id in ids) if (await users.FindByIdAsync(id.ToString()) is { } u) list.Add(u);
        return list;
    }

    public async Task<IResult> ChangeStatusAsync(StaffContext actor, Guid id, SellerStatus to, string action, string reason, string? note = null)
    {
        var sel = await db.Sellers.FirstOrDefaultAsync(s => s.Id == id);
        if (sel is null) return Results.NotFound();
        var from = sel.Status;
        var (ok, err) = await status.ChangeAsync(sel, to, note ?? reason, actor.UserId);
        if (!ok) return AdminHttp.Err(409, "invalid_transition", err!);
        await audit.WriteAsync(actor, action, "seller", id, reason, new { status = from.ToString() }, new { status = to.ToString() });
        return Results.Ok(new { sellerId = id, status = sel.Status.ToString() });
    }

    public async Task<IResult> ReactivateAsync(StaffContext actor, Guid id, string reason)
    {
        var sel = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (sel is null) return Results.NotFound();
        if (sel.Status != SellerStatus.Suspended) return AdminHttp.Err(409, "invalid_transition", $"Cannot reactivate an account that is {sel.Status}.");
        // back to where it was before the suspension (Live or Approved)
        var tag = $"seller={id}";
        var last = await db.AuditEvents.AsNoTracking().Where(e => e.Type == "seller.status_changed" && e.Detail!.EndsWith(tag) && e.Detail.Contains("->Suspended"))
            .OrderByDescending(e => e.At).Select(e => e.Detail).FirstOrDefaultAsync();
        var to = last is not null && last.StartsWith("Live->") ? SellerStatus.Live : SellerStatus.Approved;
        return await ChangeStatusAsync(actor, id, to, "seller.reactivated", reason);
    }

    public async Task<IResult> CloseAsync(StaffContext actor, Guid id, string reason)
    {
        var sel = await db.Sellers.FirstOrDefaultAsync(s => s.Id == id);
        if (sel is null) return Results.NotFound();
        if (sel.Status == SellerStatus.Closed) return AdminHttp.Err(409, "invalid_transition", "The account is already closed.");
        var from = sel.Status;
        sel.Status = SellerStatus.Closed; sel.StatusNote = "Closed by Solvelt"; sel.UpdatedAt = time.UtcNow();
        await db.Sessions.Where(s => s.ActiveAccountId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ActiveAccountId, (Guid?)null));
        await db.Users.Where(u => u.LastAccountId == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastAccountId, (Guid?)null));
        audit.Add(actor, "seller.closed", "seller", id, reason, new { status = from.ToString() }, new { status = "Closed" });
        await db.SaveChangesAsync();
        var members = await db.Memberships.Where(m => m.SellerId == id).Select(m => m.UserId).Distinct().ToListAsync();
        foreach (var uid in members)
        {
            var hasOther = await (from m in db.Memberships join s in db.Sellers on m.SellerId equals s.Id
                                  where m.UserId == uid && m.Status == MembershipStatus.Active && m.SellerId != id && s.Status != SellerStatus.Rejected && s.Status != SellerStatus.Suspended && s.Status != SellerStatus.Closed select m.Id).AnyAsync();
            if (!hasOther) await sessions.RevokeAllAsync(uid, "status:Closed");
        }
        foreach (var o in await OwnersAsync(id)) await mailer.SendAsync(TeamTemplates.AccountClosed(o.Email!, sel.VendorName ?? "your business"));
        return Results.Ok(new { sellerId = id, status = "Closed" });
    }

    public async Task<IResult> ResendVerificationAsync(StaffContext actor, Guid id, string reason)
    {
        var sel = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (sel is null) return Results.NotFound();
        var owner = (await OwnersAsync(id)).FirstOrDefault() ?? await users.FindByIdAsync(sel.UserId.ToString());
        if (owner is null || owner.EmailConfirmed) return AdminHttp.Err(409, "already_verified", "The owner's email address is already verified.");
        var issued = await codes.IssueAsync(owner.Id, "verify-email");
        if (issued.Code is null) return AdminHttp.Err(429, "too_soon", "A code was sent a moment ago. Try again shortly.");
        await mailer.SendAsync(Templates.VerifyCode(owner.Email!, issued.Code));
        await audit.WriteAsync(actor, "seller.verification_resent", "seller", id, reason);
        return Results.NoContent();
    }

    public async Task<IResult> TransferOwnershipAsync(StaffContext actor, Guid id, Guid? newOwnerUserId, string reason)
    {
        if (newOwnerUserId is null) return Validation.Invalid("newOwnerUserId", "Choose the member who becomes the Owner.");
        var sel = await db.Sellers.FirstOrDefaultAsync(s => s.Id == id);
        if (sel is null) return Results.NotFound();
        var target = await db.Memberships.FirstOrDefaultAsync(m => m.SellerId == id && m.UserId == newOwnerUserId && m.Status == MembershipStatus.Active);
        if (target is null) return AdminHttp.Err(404, "member_not_found", "That user is not an active member of the account.");
        if (target.Role == AccountRole.Owner) return AdminHttp.Err(409, "already_owner", "That member is already an Owner.");
        var oldOwners = await OwnersAsync(id);
        var now = time.UtcNow();
        foreach (var m in await db.Memberships.Where(m => m.SellerId == id && m.Role == AccountRole.Owner).ToListAsync()) { m.Role = AccountRole.Admin; m.UpdatedAt = now; }
        target.Role = AccountRole.Owner; target.AllBrands = true; target.UpdatedAt = now;
        db.MembershipBrands.RemoveRange(db.MembershipBrands.Where(x => x.MembershipId == target.Id));
        sel.UserId = target.UserId; sel.UpdatedAt = now;
        audit.Add(actor, "seller.ownership_transferred", "seller", id, reason, new { owners = oldOwners.Select(o => o.Id) }, new { owner = target.UserId });
        await db.SaveChangesAsync();
        var newOwner = await users.FindByIdAsync(target.UserId.ToString());
        var name = sel.VendorName ?? "your business";
        if (newOwner?.Email is { } ne) await mailer.SendAsync(TeamTemplates.OwnershipTransferred(ne, name, newOwner.Email, true));
        foreach (var o in oldOwners.Where(o => o.Id != target.UserId))
            await mailer.SendAsync(TeamTemplates.OwnershipTransferred(o.Email!, name, newOwner?.Email ?? "a team member", false));
        return Results.Ok(new { sellerId = id, ownerUserId = target.UserId });
    }
}

public static class SellerAdminEndpoints
{
    private static string Lower(string s) => s.Trim().ToLowerInvariant();

    private static async Task<object> DetailAsync(Guid id, AppDbContext db, UserManager<AppUser> users, bool full)
    {
        var s = await db.Sellers.AsNoTracking().FirstAsync(x => x.Id == id);
        var members = await (from m in db.Memberships.AsNoTracking() join u in db.Users on m.UserId equals u.Id where m.SellerId == id
                             select new { m.UserId, u.Email, role = m.Role.ToString(), status = m.Status.ToString(), m.LastActiveAt, m.CreatedAt }).ToListAsync();
        var brands = await db.Brands.AsNoTracking().Where(b => b.SellerId == id).Select(b => new { b.Id, b.Name, status = b.Status.ToString() }).ToListAsync();
        var tag = $"seller={id}";
        var history = await db.AuditEvents.AsNoTracking()
            .Where(e => (e.Type == "seller.status_changed" && (e.Detail!.EndsWith(tag) || (e.UserId == s.UserId && !e.Detail!.Contains("seller=")))) || (e.TargetType == "seller" && e.TargetId == id.ToString()))
            .OrderByDescending(e => e.At).Take(50).Select(e => new { e.Type, e.At, e.UserId, e.Detail, e.Reason, e.ActorRole }).ToListAsync();
        var lastOrder = await db.Orders.AsNoTracking().Where(o => o.SellerId == id).MaxAsync(o => (DateTime?)o.PlacedAt);
        var lastBooking = await db.Bookings.AsNoTracking().Where(o => o.SellerId == id).MaxAsync(o => (DateTime?)o.CreatedAt);
        var lastProduct = await db.Products.AsNoTracking().Where(o => o.SellerId == id).MaxAsync(o => (DateTime?)o.UpdatedAt);
        var owner = await users.FindByIdAsync(s.UserId.ToString());
        return new
        {
            id = s.Id, vendorName = s.VendorName, status = s.Status.ToString(), statusNote = s.StatusNote, country = s.Country, createdAt = s.CreatedAt, submittedAt = s.SubmittedAt,
            ownerEmail = owner?.Email, ownerEmailVerified = owner?.EmailConfirmed ?? false,
            members, brands,
            counts = new
            {
                listings = await db.Products.CountAsync(p => p.SellerId == id), activeListings = await db.Products.CountAsync(p => p.SellerId == id && p.Status == ProductStatus.Active),
                orders = await db.Orders.CountAsync(o => o.SellerId == id), bookings = await db.Bookings.CountAsync(b => b.SellerId == id)
            },
            statusHistory = history,
            recentActivity = new { lastOrderAt = lastOrder, lastBookingAt = lastBooking, lastListingUpdateAt = lastProduct, lastMemberActivityAt = members.Max(m => m.LastActiveAt) },
            notes = full ? await db.AdminNotes.AsNoTracking().Where(n => n.SellerId == id).OrderByDescending(n => n.At).Select(n => new { n.Id, n.Text, n.AuthorId, n.At }).ToListAsync() : null
        };
    }

    public static void MapSellerAdminEndpoints(this RouteGroupBuilder g)
    {
        var s = g.MapGroup("/sellers");

        s.AGet("", async (string? q, string? status, int? page, int? pageSize, AppDbContext db) =>
        {
            var (p, size) = AdminHttp.Paging(page, pageSize);
            var query = db.Sellers.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<SellerStatus>(status, true, out var st)) return Validation.Invalid("status", "Unknown status.");
                query = query.Where(x => x.Status == st);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = Lower(q);
                var gid = Guid.TryParse(q.Trim(), out var g1) ? g1 : Guid.Empty;
                query = query.Where(x => x.Id == gid || (x.VendorName != null && x.VendorName.ToLower().Contains(t))
                    || db.Users.Any(u => u.Id == x.UserId && u.Email!.ToLower().Contains(t))
                    || db.Memberships.Any(m => m.SellerId == x.Id && db.Users.Any(u => u.Id == m.UserId && u.Email!.ToLower().Contains(t))));
            }
            var total = await query.CountAsync();
            var rows = await query.OrderByDescending(x => x.CreatedAt).Skip((p - 1) * size).Take(size)
                .Select(x => new { x.Id, x.VendorName, status = x.Status.ToString(), x.Country, x.CreatedAt, ownerEmail = db.Users.Where(u => u.Id == x.UserId).Select(u => u.Email).FirstOrDefault() }).ToListAsync();
            return Results.Ok(new { total, page = p, pageSize = size, items = rows });
        }, StaffPermission.SellersView);

        s.AGet("/{id:guid}", async (Guid id, AppDbContext db, UserManager<AppUser> users) =>
            await db.Sellers.AnyAsync(x => x.Id == id) ? Results.Ok(await DetailAsync(id, db, users, true)) : Results.NotFound(), StaffPermission.SellersView);

        s.APost("/{id:guid}/suspend", async (Guid id, ReasonRequest r, HttpContext ctx, SellerAdminService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.ChangeStatusAsync(ctx.Staff(), id, SellerStatus.Suspended, "seller.suspended", reason), StaffPermission.SellersSuspend);
        s.APost("/{id:guid}/reactivate", async (Guid id, ReasonRequest r, HttpContext ctx, SellerAdminService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.ReactivateAsync(ctx.Staff(), id, reason), StaffPermission.SellersSuspend);
        s.APost("/{id:guid}/close", async (Guid id, ReasonRequest r, HttpContext ctx, SellerAdminService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.CloseAsync(ctx.Staff(), id, reason), StaffPermission.SellersClose);
        s.APost("/{id:guid}/resend-verification", async (Guid id, ReasonRequest r, HttpContext ctx, SellerAdminService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.ResendVerificationAsync(ctx.Staff(), id, reason), StaffPermission.SellersResendVerification);
        s.APost("/{id:guid}/transfer-ownership", async (Guid id, TransferRequest r, HttpContext ctx, SellerAdminService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.TransferOwnershipAsync(ctx.Staff(), id, r.NewOwnerUserId, reason), StaffPermission.SellersTransferOwnership);
    }

    // ------------------------------------------------------------------------------------------------------------ applications

    public static void MapApplicationEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/applications");

        a.AGet("", async (string? status, string? country, int? minAgeDays, string? assigned, string? q, int? page, int? pageSize, HttpContext ctx, AppDbContext db, TimeProvider time) =>
        {
            var (p, size) = AdminHttp.Paging(page, pageSize);
            var now = time.UtcNow();
            var query = db.Sellers.AsNoTracking().Where(x => x.Status != SellerStatus.Draft);
            if (string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == SellerStatus.PendingReview || x.Status == SellerStatus.NeedsInfo);
            else if (Enum.TryParse<SellerStatus>(status, true, out var st) && st != SellerStatus.Draft) query = query.Where(x => x.Status == st);
            else return Validation.Invalid("status", "Unknown status.");
            if (!string.IsNullOrWhiteSpace(country)) { var c = country.Trim().ToLower(); query = query.Where(x => x.Country != null && x.Country.ToLower() == c); }
            if (minAgeDays is > 0) { var cutoff = now.AddDays(-minAgeDays.Value); query = query.Where(x => x.SubmittedAt != null && x.SubmittedAt <= cutoff); }
            if (assigned == "me") { var me = ctx.Staff().UserId; query = query.Where(x => db.ApplicationAssignments.Any(y => y.SellerId == x.Id && y.AssignedTo == me)); }
            else if (assigned == "none") query = query.Where(x => !db.ApplicationAssignments.Any(y => y.SellerId == x.Id && y.AssignedTo != null));
            else if (Guid.TryParse(assigned, out var who)) query = query.Where(x => db.ApplicationAssignments.Any(y => y.SellerId == x.Id && y.AssignedTo == who));
            if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim().ToLower(); query = query.Where(x => x.VendorName != null && x.VendorName.ToLower().Contains(t) || db.Users.Any(u => u.Id == x.UserId && u.Email!.ToLower().Contains(t))); }
            var total = await query.CountAsync();
            var rows = await query.OrderBy(x => x.SubmittedAt ?? x.CreatedAt).Skip((p - 1) * size).Take(size).Select(x => new
            {
                x.Id, x.VendorName, status = x.Status.ToString(), x.Country, x.SubmittedAt,
                ownerEmail = db.Users.Where(u => u.Id == x.UserId).Select(u => u.Email).FirstOrDefault(),
                assignedTo = db.ApplicationAssignments.Where(y => y.SellerId == x.Id).Select(y => y.AssignedTo).FirstOrDefault()
            }).ToListAsync();
            return Results.Ok(new { total, page = p, pageSize = size, items = rows.Select(r => new { r.Id, r.VendorName, r.status, r.Country, r.SubmittedAt, r.ownerEmail, r.assignedTo,
                ageDays = r.SubmittedAt is { } sa ? Math.Floor((now - sa).TotalDays) : (double?)null }) });
        }, StaffPermission.ApplicationsView);

        a.AGet("/{id:guid}", async (Guid id, AppDbContext db, UserManager<AppUser> users, KosherService kosher, TimeProvider time) =>
        {
            var s = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Status != SellerStatus.Draft);
            if (s is null) return Results.NotFound();
            var cfg = await kosher.ConfigAsync();
            var answers = KosherService.Parse(s.KosherAnswersJson);
            var visible = KosherService.VisibleKeys(cfg, answers);
            var files = await db.SellerFiles.AsNoTracking().Where(f => f.SellerId == id).Select(f => new { f.Id, f.FieldKey, f.OriginalName, f.ContentType, f.Size, f.CreatedAt }).ToListAsync();
            var owner = await users.FindByIdAsync(s.UserId.ToString());
            var asg = await db.ApplicationAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.SellerId == id);
            var detail = await DetailAsync(id, db, users, true);
            return Results.Ok(new
            {
                id = s.Id, status = s.Status.ToString(), statusNote = s.StatusNote, submittedAt = s.SubmittedAt, ageDays = s.SubmittedAt is { } sa ? Math.Floor((time.UtcNow() - sa).TotalDays) : (double?)null,
                owner = new { email = owner?.Email, emailVerified = owner?.EmailConfirmed ?? false },
                business = new { s.VendorName, s.ContactName, s.ContactPhone, s.AddressLine1, s.AddressLine2, s.City, s.Region, s.PostalCode, s.Country, s.SmsOptIn, s.SmsConsentAt, s.SmsConsentWordingVersion },
                kosher = cfg.Where(f => visible.Contains(f.Key) && f.Type != "file").OrderBy(f => f.SortOrder).Select(f => new { f.Key, f.Label, f.Type, answer = answers.GetValueOrDefault(f.Key) }),
                files,
                legal = new { s.TermsVersion, s.TermsAcceptedAt, s.SellerAgreementVersion, s.SellerAgreementAcceptedAt },
                assignment = asg is null ? null : new { asg.AssignedTo, asg.AssignedAt },
                detail
            });
        }, StaffPermission.ApplicationsView);

        // Certificate viewer: stored (sniffed) type only, nosniff, never executed as HTML; every view is audited.
        a.AGet("/{id:guid}/files/{fileId:guid}", async (Guid id, Guid fileId, HttpContext ctx, AppDbContext db, IFileStorage storage, AdminAudit audit) =>
        {
            var f = await db.SellerFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.SellerId == id);
            if (f is null) return Results.NotFound();
            var stream = await storage.OpenReadAsync(f.StorageKey);
            if (stream is null) return Results.NotFound();
            await audit.WriteAsync(ctx.Staff(), "seller.file_viewed", "seller_file", fileId, null, extra: "seller=" + id);
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            var safeName = new string(f.OriginalName.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray());
            ctx.Response.Headers["Content-Disposition"] = $"inline; filename=\"{(safeName.Length == 0 ? "certificate" : safeName)}\"";
            var type = f.ContentType is "application/pdf" or "image/png" or "image/jpeg" ? f.ContentType : "application/octet-stream";
            return Results.Stream(stream, type);
        }, StaffPermission.ApplicationsView);

        a.APost("/{id:guid}/approve", async (Guid id, DecisionRequest r, HttpContext ctx, SellerAdminService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.ChangeStatusAsync(ctx.Staff(), id, SellerStatus.Approved, "application.approved", reason, note: null), StaffPermission.ApplicationsDecide);
        a.APost("/{id:guid}/needs-info", async (Guid id, DecisionRequest r, HttpContext ctx, SellerAdminService svc) =>
        {
            var msg = (r.Message ?? "").Trim();
            if (msg.Length < 5 || msg.Length > 1000) return Validation.Invalid("message", "Tell the seller what is missing (5-1000 characters).");
            return await svc.ChangeStatusAsync(ctx.Staff(), id, SellerStatus.NeedsInfo, "application.needs_info", msg, note: msg);
        }, StaffPermission.ApplicationsDecide);
        a.APost("/{id:guid}/reject", async (Guid id, DecisionRequest r, HttpContext ctx, SellerAdminService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.ChangeStatusAsync(ctx.Staff(), id, SellerStatus.Rejected, "application.rejected", reason), StaffPermission.ApplicationsDecide);

        a.APost("/{id:guid}/assign", async (Guid id, AssignRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (!await db.Sellers.AnyAsync(x => x.Id == id && x.Status != SellerStatus.Draft)) return Results.NotFound();
            if (r.AssignTo is { } who)
            {
                var m = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == who && x.Active);
                if (m is null || !StaffPermissions.Has(m.Role, StaffPermission.ApplicationsDecide)) return Validation.Invalid("assignTo", "Choose an active reviewer (Moderator or Super admin).");
            }
            var row = await db.ApplicationAssignments.FirstOrDefaultAsync(x => x.SellerId == id);
            var before = row?.AssignedTo;
            if (row is null) db.ApplicationAssignments.Add(row = new ApplicationAssignment { SellerId = id });
            row.AssignedTo = r.AssignTo; row.AssignedAt = time.UtcNow();
            await audit.WriteAsync(ctx.Staff(), "application.assigned", "seller", id, reason, new { assignedTo = before }, new { assignedTo = r.AssignTo });
            return Results.Ok(new { sellerId = id, assignedTo = r.AssignTo });
        }, StaffPermission.ApplicationsAssign);

        a.APost("/{id:guid}/notes", async (Guid id, NoteRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit, TimeProvider time) =>
        {
            var text = (r.Text ?? "").Trim();
            if (text.Length < 3 || text.Length > 2000) return Validation.Invalid("text", "A note needs 3-2000 characters.");
            if (!await db.Sellers.AnyAsync(x => x.Id == id)) return Results.NotFound();
            var n = new AdminNote { SellerId = id, Text = text, AuthorId = ctx.Staff().UserId, At = time.UtcNow() };
            db.AdminNotes.Add(n);
            await audit.WriteAsync(ctx.Staff(), "seller.note_added", "seller", id, "internal note");
            return Results.Ok(new { n.Id, n.Text, n.At });
        }, StaffPermission.ApplicationsDecide);
    }
}
