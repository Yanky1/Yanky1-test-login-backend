using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;
using Microsoft.AspNetCore.Identity;

namespace Solvelt.Api.Admin;

public record ListingReportRequest(string? Reason);

public class ModerationService(AppDbContext db, UserManager<AppUser> users, Mailer mailer, AdminAudit audit, SellerAdminService sellers, TimeProvider time)
{
    private static string Cut(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>Turns buyer-reported and contact-flagged order messages into queue rows (idempotent; one row per message and kind).</summary>
    public async Task SyncAsync()
    {
        var existing = await db.ModerationReports.AsNoTracking().Where(r => r.Kind == "message" || r.Kind == "contact_warning").Select(r => r.Kind + ":" + r.TargetId).ToListAsync();
        var seen = existing.ToHashSet();
        var msgs = await db.Messages.AsNoTracking().Where(m => m.Reported || m.ContactWarning)
            .OrderByDescending(m => m.CreatedAt).Take(500).Select(m => new { m.Id, m.SellerId, m.Reported, m.ReportReason, m.Text, m.CreatedAt }).ToListAsync();
        var now = time.UtcNow();
        foreach (var m in msgs)
        {
            var kind = m.Reported ? "message" : "contact_warning";
            if (!seen.Add(kind + ":" + m.Id)) continue;
            db.ModerationReports.Add(new ModerationReport
            {
                Kind = kind, TargetId = m.Id, SellerId = m.SellerId, ReporterKind = m.Reported ? "buyer" : "system",
                Reason = m.Reported ? (m.ReportReason ?? "reported") : "Contact details in a message", Summary = Cut(Mask.Text(m.Text), 160), CreatedAt = now
            });
        }
        try { await db.SaveChangesAsync(); } catch (DbUpdateException) { db.ChangeTracker.Clear(); } // a parallel sync won the race
    }

    /// <summary>Internal marketplace report (idempotent: an open report stays; a resolved one reopens).</summary>
    public async Task<(bool Ok, string? Error)> ReportAsync(string kind, Guid targetId, string? reason)
    {
        Guid sellerId; string summary;
        if (kind == "listing")
        {
            var p = await db.Products.AsNoTracking().Where(x => x.Id == targetId).Select(x => new { x.SellerId, x.Title }).FirstOrDefaultAsync();
            if (p is null) return (false, "Unknown listing.");
            sellerId = p.SellerId; summary = Cut(p.Title, 160);
        }
        else
        {
            var m = await db.BookingMessages.AsNoTracking().Where(x => x.Id == targetId).Select(x => new { x.SellerId, x.Text }).FirstOrDefaultAsync();
            if (m is null) return (false, "Unknown message.");
            sellerId = m.SellerId; summary = Cut(Mask.Text(m.Text), 160);
        }
        var r = Cut((reason ?? "reported").Trim(), 300);
        var now = time.UtcNow();
        var row = await db.ModerationReports.FirstOrDefaultAsync(x => x.Kind == kind && x.TargetId == targetId);
        if (row is null) db.ModerationReports.Add(new ModerationReport { Kind = kind, TargetId = targetId, SellerId = sellerId, Reason = r, Summary = summary, ReporterKind = "marketplace", CreatedAt = now });
        else if (row.Status != ReportStatus.Open)
        { row.Status = ReportStatus.Open; row.Reason = r; row.CreatedAt = now; row.ResolvedAt = null; row.ResolvedBy = null; row.Resolution = null; row.ResolutionNote = null; }
        try { await db.SaveChangesAsync(); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        return (true, null);
    }

    private async Task<List<string>> OwnerEmailsAsync(Guid sellerId)
    {
        var ids = await db.Memberships.Where(m => m.SellerId == sellerId && m.Role == AccountRole.Owner && m.Status == MembershipStatus.Active).Select(m => m.UserId).ToListAsync();
        if (ids.Count == 0) ids = await db.Sellers.Where(s => s.Id == sellerId).Select(s => s.UserId).ToListAsync();
        var res = new List<string>();
        foreach (var id in ids) if ((await users.FindByIdAsync(id.ToString()))?.Email is { } e) res.Add(e);
        return res;
    }

    private async Task NotifyAsync(Guid sellerId, string kind, string subject, string heading, string reason, string? extra = null)
    {
        foreach (var e in await OwnerEmailsAsync(sellerId))
            await mailer.SendAsync(Templates.Notification(e, kind, subject, heading, extra is null ? [reason] : [extra, "Reason: " + reason]));
    }

    private void Resolve(ModerationReport r, StaffContext s, ReportStatus st, string resolution, string reason)
    { r.Status = st; r.ResolvedAt = time.UtcNow(); r.ResolvedBy = s.UserId; r.Resolution = resolution; r.ResolutionNote = reason; }

    public async Task<IResult> DismissAsync(StaffContext s, Guid id, string reason)
    {
        var r = await db.ModerationReports.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        if (r.Status != ReportStatus.Open) return AdminHttp.Err(409, "already_resolved", "This report is already resolved.");
        Resolve(r, s, ReportStatus.Dismissed, "dismissed", reason);
        await audit.WriteAsync(s, "moderation.dismissed", "report", id, reason, new { status = "Open" }, new { status = "Dismissed" });
        return Results.Ok(new { id, status = "Dismissed" });
    }

    public async Task<IResult> WarnAsync(StaffContext s, Guid id, string reason)
    {
        var r = await db.ModerationReports.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        if (r.Status != ReportStatus.Open) return AdminHttp.Err(409, "already_resolved", "This report is already resolved.");
        Resolve(r, s, ReportStatus.Actioned, "warned", reason);
        await audit.WriteAsync(s, "moderation.seller_warned", "report", id, reason, extra: "seller=" + r.SellerId);
        await NotifyAsync(r.SellerId, "moderation-warning", "A notice about your Solvelt account", "A notice from Solvelt", reason,
            "We received a report about content on your account and ask you to review our seller rules.");
        return Results.Ok(new { id, status = "Actioned", resolution = "warned" });
    }

    public async Task<IResult> EscalateAsync(StaffContext s, Guid id, string reason)
    {
        var r = await db.ModerationReports.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        if (r.Status != ReportStatus.Open) return AdminHttp.Err(409, "already_resolved", "This report is already resolved.");
        var res = await sellers.ChangeStatusAsync(s, r.SellerId, SellerStatus.Suspended, "moderation.escalated_suspend", reason);
        var status = (res as IStatusCodeHttpResult)?.StatusCode ?? 200;
        if (status >= 400) return res;
        db.ChangeTracker.Clear();
        r = await db.ModerationReports.FirstAsync(x => x.Id == id);
        Resolve(r, s, ReportStatus.Actioned, "suspended", reason);
        await audit.WriteAsync(s, "moderation.escalated", "report", id, reason, extra: "seller=" + r.SellerId);
        return Results.Ok(new { id, status = "Actioned", resolution = "suspended" });
    }

    public async Task<IResult> HideListingAsync(StaffContext s, Guid productId, string reason, Guid? reportId = null)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId);
        if (p is null) return Results.NotFound();
        if (p.ModeratedAt is not null) return AdminHttp.Err(409, "already_hidden", "This listing is already hidden.");
        p.ModerationPrevStatus = p.Status; p.ModeratedAt = time.UtcNow(); p.ModerationReason = reason; p.ModeratedBy = s.UserId;
        if (p.Status == ProductStatus.Active) p.Status = ProductStatus.Inactive;
        p.Version++; p.UpdatedAt = time.UtcNow();
        foreach (var r in await db.ModerationReports.Where(x => x.Kind == "listing" && x.TargetId == productId && x.Status == ReportStatus.Open).ToListAsync())
            Resolve(r, s, ReportStatus.Actioned, "hidden", reason);
        audit.Add(s, "listing.hidden", "listing", productId, reason, new { status = p.ModerationPrevStatus.ToString() }, new { status = p.Status.ToString(), hidden = true }, "seller=" + p.SellerId);
        await db.SaveChangesAsync();
        await NotifyAsync(p.SellerId, "listing-hidden", "A listing was hidden", "One of your listings was hidden", reason, $"Solvelt hid your listing \"{p.Title}\".");
        return Results.Ok(new { productId, hidden = true });
    }

    public async Task<IResult> UnhideListingAsync(StaffContext s, Guid productId, string reason)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId);
        if (p is null) return Results.NotFound();
        if (p.ModeratedAt is null) return AdminHttp.Err(409, "not_hidden", "This listing is not hidden by staff.");
        var prev = p.ModerationPrevStatus;
        var sellerUsable = await db.Sellers.AnyAsync(x => x.Id == p.SellerId && (x.Status == SellerStatus.Approved || x.Status == SellerStatus.Live));
        p.ModeratedAt = null; p.ModerationReason = null; p.ModeratedBy = null; p.ModerationPrevStatus = null;
        if (prev == ProductStatus.Active && sellerUsable && p.Status == ProductStatus.Inactive) p.Status = ProductStatus.Active;
        p.Version++; p.UpdatedAt = time.UtcNow();
        audit.Add(s, "listing.unhidden", "listing", productId, reason, new { hidden = true }, new { status = p.Status.ToString(), hidden = false }, "seller=" + p.SellerId);
        await db.SaveChangesAsync();
        await NotifyAsync(p.SellerId, "listing-restored", "A listing was restored", "Your listing is visible again", reason, $"Solvelt restored your listing \"{p.Title}\".");
        return Results.Ok(new { productId, hidden = false, status = p.Status.ToString() });
    }
}

public static class ModerationEndpoints
{
    public static void MapModerationEndpoints(this RouteGroupBuilder g)
    {
        var m = g.MapGroup("/moderation");

        m.AGet("", async (string? status, string? kind, int? page, int? pageSize, AppDbContext db, ModerationService svc) =>
        {
            var (p, size) = AdminHttp.Paging(page, pageSize);
            await svc.SyncAsync();
            var q = db.ModerationReports.AsNoTracking().AsQueryable();
            var st = (status ?? "open").ToLowerInvariant();
            if (st != "all")
            {
                if (!Enum.TryParse<ReportStatus>(st, true, out var rs)) return Validation.Invalid("status", "Use open, dismissed, actioned or all.");
                q = q.Where(x => x.Status == rs);
            }
            if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(x => x.Kind == kind);
            var total = await q.CountAsync();
            var rows = await q.OrderBy(x => x.CreatedAt).Skip((p - 1) * size).Take(size).Select(x => new
            {
                x.Id, x.Kind, x.TargetId, x.SellerId, x.Reason, x.Summary, x.ReporterKind, status = x.Status.ToString(), x.CreatedAt, x.Resolution,
                vendor = db.Sellers.Where(s => s.Id == x.SellerId).Select(s => s.VendorName).FirstOrDefault()
            }).ToListAsync();
            return Results.Ok(new { total, page = p, pageSize = size, items = rows });
        }, StaffPermission.ModerationView);

        m.AGet("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, AdminAudit audit) =>
        {
            var r = await db.ModerationReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (r is null) return Results.NotFound();
            object? target = null;
            if (r.Kind is "message" or "contact_warning")
            {
                var msg = await db.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.TargetId);
                if (msg is not null)
                {
                    var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == msg.ConversationId);
                    var around = await db.Messages.AsNoTracking().Where(x => x.ConversationId == msg.ConversationId && x.CreatedAt <= msg.CreatedAt).OrderByDescending(x => x.CreatedAt).Take(6)
                        .Select(x => new { x.Id, direction = x.Direction.ToString(), x.Text, x.CreatedAt, x.ContactWarning, x.Reported }).ToListAsync();
                    target = new { conversationId = msg.ConversationId, subject = conv?.Subject, messages = around.OrderBy(x => x.CreatedAt) };
                }
            }
            else if (r.Kind == "booking_message")
            {
                var msg = await db.BookingMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.TargetId);
                if (msg is not null) target = new { bookingId = msg.BookingId, messages = new[] { new { msg.Id, direction = msg.Direction, msg.Text, msg.CreatedAt } } };
            }
            else if (r.Kind == "listing") target = new { productId = r.TargetId };
            await audit.WriteAsync(ctx.Staff(), "moderation.viewed", "report", id, null);
            return Results.Ok(new { r.Id, r.Kind, r.TargetId, r.SellerId, r.Reason, r.Summary, r.ReporterKind, status = r.Status.ToString(), r.CreatedAt, r.ResolvedAt, r.Resolution, r.ResolutionNote, target });
        }, StaffPermission.ModerationView);

        m.APost("/{id:guid}/dismiss", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.DismissAsync(ctx.Staff(), id, reason), StaffPermission.ModerationAct);
        m.APost("/{id:guid}/warn-seller", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.WarnAsync(ctx.Staff(), id, reason), StaffPermission.ModerationAct);
        m.APost("/{id:guid}/escalate-suspend", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.EscalateAsync(ctx.Staff(), id, reason), StaffPermission.ModerationAct);

        var l = g.MapGroup("/listings");
        l.AGet("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            var brand = await db.Brands.AsNoTracking().Where(b => b.Id == p.BrandId).Select(b => b.Name).FirstOrDefaultAsync();
            var seller = await db.Sellers.AsNoTracking().Where(s => s.Id == p.SellerId).Select(s => new { s.Id, s.VendorName, status = s.Status.ToString() }).FirstOrDefaultAsync();
            return Results.Ok(new
            {
                p.Id, type = p.Type.ToString(), status = p.Status.ToString(), p.Title, p.Description, p.Sku, priceCents = p.PriceCents, p.Stock, brand, seller,
                images = await db.ProductImages.CountAsync(i => i.ProductId == id), p.CreatedAt, p.UpdatedAt, p.PublishedAt,
                moderation = p.ModeratedAt is null ? null : new { hidden = true, reason = p.ModerationReason, since = p.ModeratedAt, by = p.ModeratedBy },
                reports = await db.ModerationReports.AsNoTracking().Where(r => r.Kind == "listing" && r.TargetId == id).Select(r => new { r.Id, r.Reason, status = r.Status.ToString(), r.CreatedAt }).ToListAsync()
            });
        }, StaffPermission.ModerationView);
        l.APost("/{id:guid}/hide", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.HideListingAsync(ctx.Staff(), id, reason), StaffPermission.ModerationAct);
        l.APost("/{id:guid}/unhide", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.UnhideListingAsync(ctx.Staff(), id, reason), StaffPermission.ModerationAct);
    }

    /// <summary>Marketplace-facing report endpoints (signed like the rest of /api/internal/v1).</summary>
    public static void MapAdminInternalEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/internal/v1").RequireRateLimiting("internal").AllowAnonymous();
        static async Task<IResult> Handle(HttpContext ctx, InboundVerifier verifier, ModerationService svc, string kind, Guid id)
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return AdminHttp.Err(v.Status, v.Code, v.Message);
            string? reason = null;
            try { reason = JsonSerializer.Deserialize<ListingReportRequest>(v.Body!, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Reason; }
            catch (JsonException) { return AdminHttp.Err(400, "invalid_json", "The request body is not valid JSON of the expected shape."); }
            var (ok, err) = await svc.ReportAsync(kind, id, reason);
            return ok ? Results.Json(new { reported = true }) : AdminHttp.Err(404, "not_found", err!);
        }
        g.MapPost("/listings/{id:guid}/reports", (HttpContext ctx, InboundVerifier v, ModerationService svc, Guid id) => Handle(ctx, v, svc, "listing", id));
        g.MapPost("/booking-messages/{id:guid}/reports", (HttpContext ctx, InboundVerifier v, ModerationService svc, Guid id) => Handle(ctx, v, svc, "booking_message", id));
    }
}
