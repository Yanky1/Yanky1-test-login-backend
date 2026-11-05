using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
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

public record ContentReportIn(string? ReportId, string? Kind, string? TargetId, string? Reason, string? Note, string? ReporterRef);
public record IntakeResult(int Status, string Code, string Message, ContentReport? Report = null, bool Duplicate = false);

public class ModerationService(AppDbContext db, UserManager<AppUser> users, Mailer mailer, AdminAudit audit, SellerAdminService sellers, Outbox outbox, TimeProvider time, Solvelt.Api.Reviews.ReviewEngine reviews)
{
    public const string DecisionDismissed = "dismissed", DecisionHidden = "hidden", DecisionRemoved = "removed";
    public static readonly string[] ContentKinds = ["review", "review_reply", "listing", "message"];

    /// <summary>Stable id for a marketplace-owned target (a review) so it can share the queue's (kind, targetId) uniqueness with our own Guid-keyed targets.</summary>
    public static Guid RefGuid(string kind, string targetRef) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(kind + ":" + targetRef))[..16]);

    /// <summary>Optional text for the reporter: trimmed, at most 500 characters, null when blank.</summary>
    public static string? Shown(string? s) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s.Length > 500 ? s[..500] : s; }

    /// <summary>
    /// Records the decision on every still-undecided marketplace report of this queue row and queues one <c>content.report_decided</c> event per report. Each report is CLAIMED with a
    /// conditional UPDATE (<c>WHERE Decision IS NULL</c>) and its event is queued only if this call won the claim, so concurrent decisions can never emit an event twice. Runs inside the
    /// decision's transaction; the caller's SaveChanges (the audit entry) commits the events with it.
    /// </summary>
    public async Task DecideAsync(ModerationReport row, string decision, string? shownToUser)
    {
        var open = await db.ContentReports.AsNoTracking().Where(c => c.ModerationReportId == row.Id && c.Decision == null).ToListAsync();
        var now = time.UtcNow();
        foreach (var c in open)
        {
            var id = c.Id;
            var claimed = await db.ContentReports.Where(x => x.Id == id && x.Decision == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Decision, decision).SetProperty(x => x.DecidedAt, (DateTime?)now));
            if (claimed != 1) continue;
            outbox.Enqueue("content.report_decided", row.SellerId == Guid.Empty ? null : row.SellerId, c.Id.ToString(),
                new { reportId = c.ReportId, kind = c.Kind, targetId = c.TargetRef, decision, reasonShownToUser = shownToUser });
        }
    }

    /// <summary>
    /// Marketplace content report (POST /api/internal/v1/content/reports): idempotent on reportId. The report joins the existing queue row of its target
    /// (reopening a resolved one), exactly like the older "report listing" path.
    /// </summary>
    public async Task<IntakeResult> IntakeAsync(ContentReportIn r, Func<string, string?, Task<bool>> flagMessage)
    {
        var reportId = r.ReportId?.Trim() ?? ""; var kind = r.Kind?.Trim().ToLowerInvariant() ?? ""; var target = r.TargetId?.Trim() ?? "";
        var reason = r.Reason?.Trim() ?? ""; var note = r.Note?.Trim(); var reporter = r.ReporterRef?.Trim() ?? "";
        if (reportId.Length is 0 or > 100) return new(422, "invalid_report", "reportId is required (at most 100 characters).");
        if (!ContentKinds.Contains(kind)) return new(422, "invalid_report", "kind must be review, review_reply, listing or message.");
        if (target.Length is 0 or > 200) return new(422, "invalid_report", "targetId is required (at most 200 characters).");
        if (reason.Length is 0 or > 300) return new(422, "invalid_report", "reason is required (at most 300 characters).");
        if (note is { Length: > 1000 }) return new(422, "invalid_report", "note can be at most 1000 characters.");
        if (reporter.Length is 0 or > 200) return new(422, "invalid_report", "reporterRef is required (at most 200 characters).");
        if (string.IsNullOrEmpty(note)) note = null;

        async Task<IntakeResult?> Existing()
        {
            var e = await db.ContentReports.AsNoTracking().FirstOrDefaultAsync(x => x.ReportId == reportId);
            if (e is null) return null;
            // after retention the reporter reference is "[redacted]": a redelivery of the same report (same kind and target) is still the same report, not a conflict
            var redacted = e.ReporterRef == Solvelt.Api.Orders.OrderMaintenance.Redacted;
            return e.Kind == kind && e.TargetRef == target && (redacted || e.ReporterRef == reporter) ? new(200, "ok", "", e, true)
                : new(409, "report_id_conflict", "This reportId was already used for a different report.");
        }
        if (await Existing() is { } dup) return dup;

        // resolve the target: who owns it, and what the staff queue shows
        Guid queueTarget; Guid sellerId; string summary; string queueKind = kind;
        switch (kind)
        {
            case "listing":
                {
                    if (!Guid.TryParse(target, out var pid)) return new(422, "invalid_report", "targetId of a listing report must be a listing id.");
                    var p = await db.Products.AsNoTracking().Where(x => x.Id == pid).Select(x => new { x.SellerId, x.Title }).FirstOrDefaultAsync();
                    if (p is null) return new(404, "not_found", "Unknown listing.");
                    queueTarget = pid; sellerId = p.SellerId; summary = Cut(p.Title, 160);
                    break;
                }
            case "message":
                {
                    var matches = await db.Messages.AsNoTracking().Where(x => x.MarketplaceMessageId == target).Select(x => new { x.Id, x.SellerId, x.Text }).Take(2).ToListAsync();
                    if (matches.Count > 1) return new(422, "ambiguous_target", "This message id exists for several sellers; the report cannot be matched to one message.");
                    var m = matches.FirstOrDefault();
                    if (m is not null) { queueTarget = m.Id; sellerId = m.SellerId; summary = Cut(Mask.Text(m.Text), 160); }
                    else if (Guid.TryParse(target, out var bid) && await db.BookingMessages.AsNoTracking().Where(x => x.Id == bid).Select(x => new { x.SellerId, x.Text }).FirstOrDefaultAsync() is { } bm)
                    { queueTarget = bid; sellerId = bm.SellerId; summary = Cut(Mask.Text(bm.Text), 160); queueKind = "booking_message"; }
                    else return new(404, "not_found", "Unknown message.");
                    break;
                }
            case "review_reply":
                {
                    // the seller's reply to a review (C17): targetId is the replyId the marketplace got in review.reply_posted
                    if (!Guid.TryParse(target, out var rid)) return new(422, "invalid_report", "targetId of a review_reply report must be a replyId.");
                    var rp = await db.ReviewReplies.AsNoTracking().Where(x => x.Id == rid && x.RemovedAt == null).Select(x => new { x.SellerId, x.Text }).FirstOrDefaultAsync();
                    if (rp is null) return new(404, "not_found", "Unknown reply.");
                    queueTarget = rid; sellerId = rp.SellerId; summary = Cut(Mask.Text(rp.Text), 160);
                    break;
                }
            default:    // review: it lives on the marketplace; the portal keeps only the report and the decision
                queueTarget = RefGuid("review", target); sellerId = Guid.Empty; summary = "Review reported by the marketplace";
                break;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = time.UtcNow();
            var row = await db.ModerationReports.FirstOrDefaultAsync(x => x.Kind == queueKind && x.TargetId == queueTarget);
            if (row is null) { row = new ModerationReport { Kind = queueKind, TargetId = queueTarget, SellerId = sellerId, Reason = Cut(reason, 300), Summary = summary, ReporterKind = "marketplace", CreatedAt = now }; db.ModerationReports.Add(row); }
            else if (row.Status != ReportStatus.Open)
            { row.Status = ReportStatus.Open; row.Reason = Cut(reason, 300); row.CreatedAt = now; row.ResolvedAt = null; row.ResolvedBy = null; row.Resolution = null; row.ResolutionNote = null; }
            var report = new ContentReport { ReportId = reportId, Kind = kind, TargetRef = target, Reason = Cut(reason, 300), Note = note, ReporterRef = reporter, ModerationReportId = row.Id, CreatedAt = now };
            db.ContentReports.Add(report);
            try
            {
                await db.SaveChangesAsync();
                if (kind == "message" && queueKind == "message") await flagMessage(target, reason);   // existing "report message" path: the seller's conversation shows the flag
                return new(201, "ok", "", report);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                if (await Existing() is { } raced) return raced;     // a parallel delivery of the same reportId won, or another report created the queue row first: retry
            }
        }
        return new(503, "retry", "The report could not be stored; retry.");
    }

    private static string Cut(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>Turns buyer-reported and contact-flagged order messages into queue rows (idempotent; one row per message and kind).</summary>
    public async Task SyncAsync()
    {
        var existing = await db.ModerationReports.AsNoTracking().Where(r => r.Kind == "message" || r.Kind == "contact_warning").Select(r => new { r.Kind, r.TargetId }).ToListAsync();
        var seen = existing.Select(x => (x.Kind, x.TargetId)).ToHashSet();
        var msgs = await db.Messages.AsNoTracking().Where(m => m.Reported || m.ContactWarning)
            .OrderByDescending(m => m.CreatedAt).Take(500).Select(m => new { m.Id, m.SellerId, m.Reported, m.ReportReason, m.Text, m.CreatedAt }).ToListAsync();
        var now = time.UtcNow();
        foreach (var m in msgs)
        {
            var kind = m.Reported ? "message" : "contact_warning";
            if (!seen.Add((kind, m.Id))) continue;
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

    /// <summary>
    /// Claims an OPEN report row for a decision: a conditional UPDATE (<c>WHERE Id AND Status = Open</c>) that exactly one of several concurrent deciders wins. The loser gets
    /// false and must not do anything else (no event, no audit, no side effect).
    /// </summary>
    private async Task<bool> ClaimAsync(Guid id, StaffContext s, ReportStatus st, string resolution, string reason)
    {
        var now = time.UtcNow(); var uid = (Guid?)s.UserId;
        return await db.ModerationReports.Where(x => x.Id == id && x.Status == ReportStatus.Open).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Status, st).SetProperty(x => x.ResolvedAt, (DateTime?)now).SetProperty(x => x.ResolvedBy, uid)
            .SetProperty(x => x.Resolution, resolution).SetProperty(x => x.ResolutionNote, reason)) == 1;
    }

    private static IResult AlreadyResolved() => AdminHttp.Err(409, "already_resolved", "This report is already resolved.");

    /// <summary>Runs one decision in one transaction (the caller's, or one opened here): the claim, the decision rows, the events and the audit entry commit together or not at all.</summary>
    private async Task<IResult> InTx(Func<Task<IResult>> work)
    {
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            var res = await work();
            if (tx is not null) { if ((res as IStatusCodeHttpResult)?.StatusCode is >= 400) await tx.RollbackAsync(); else await tx.CommitAsync(); }
            return res;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            if (tx is not null) await tx.RollbackAsync();
            return AdminHttp.Err(409, "conflict", "The item was changed by someone else at the same time; reload and try again.");
        }
    }

    public async Task<IResult> DismissAsync(StaffContext s, Guid id, string reason, string? shown = null)
    {
        var r = await db.ModerationReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        if (r.Status != ReportStatus.Open) return AlreadyResolved();
        return await InTx(async () =>
        {
            if (!await ClaimAsync(id, s, ReportStatus.Dismissed, "dismissed", reason)) return AlreadyResolved();
            await DecideAsync(r, DecisionDismissed, shown);
            await audit.WriteAsync(s, "moderation.dismissed", "report", id, reason, new { status = "Open" }, new { status = "Dismissed" });
            return Results.Ok(new { id, status = "Dismissed" });
        });
    }

    public async Task<IResult> WarnAsync(StaffContext s, Guid id, string reason, string? shown = null)
    {
        var r = await db.ModerationReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        if (r.Status != ReportStatus.Open) return AlreadyResolved();
        if (r.SellerId == Guid.Empty) return AdminHttp.Err(409, "no_seller", "This report has no seller on this portal (it concerns marketplace content).");
        return await InTx(async () =>
        {
            if (!await ClaimAsync(id, s, ReportStatus.Actioned, "warned", reason)) return AlreadyResolved();
            await DecideAsync(r, DecisionDismissed, shown);   // the content stays: to the reporter the report is closed without a content action
            await audit.WriteAsync(s, "moderation.seller_warned", "report", id, reason, extra: "seller=" + r.SellerId);
            await NotifyAsync(r.SellerId, "moderation-warning", "A notice about your Solvelt account", "A notice from Solvelt", reason,
                "We received a report about content on your account and ask you to review our seller rules.");
            return Results.Ok(new { id, status = "Actioned", resolution = "warned" });
        });
    }

    public async Task<IResult> EscalateAsync(StaffContext s, Guid id, string reason, string? shown = null)
    {
        var r = await db.ModerationReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        if (r.Status != ReportStatus.Open) return AlreadyResolved();
        if (r.SellerId == Guid.Empty) return AdminHttp.Err(409, "no_seller", "This report has no seller on this portal (it concerns marketplace content).");
        return await InTx(async () =>
        {
            if (!await ClaimAsync(id, s, ReportStatus.Actioned, "suspended", reason)) return AlreadyResolved();     // the winner suspends; a loser never touches the seller
            var res = await sellers.ChangeStatusAsync(s, r.SellerId, SellerStatus.Suspended, "moderation.escalated_suspend", reason);
            if (((res as IStatusCodeHttpResult)?.StatusCode ?? 200) >= 400) return res;                              // InTx rolls the claim back: the report stays open
            await DecideAsync(r, DecisionRemoved, shown);     // suspending the seller takes all their listings off the marketplace
            await audit.WriteAsync(s, "moderation.escalated", "report", id, reason, extra: "seller=" + r.SellerId);
            return Results.Ok(new { id, status = "Actioned", resolution = "suspended" });
        });
    }

    public async Task<IResult> HideListingAsync(StaffContext s, Guid productId, string reason, Guid? reportId = null, string? shown = null, bool remove = false)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId);
        if (p is null) return Results.NotFound();
        var already = p.ModeratedAt is not null;
        if (already && !remove && reportId is null) return AdminHttp.Err(409, "already_hidden", "This listing is already hidden.");
        return await InTx(async () =>
        {
            // a report decided as "hidden" on a listing that staff already hid: the decision is recorded, the listing is not hidden a second time
            var change = !already || remove;
            var decision = remove ? DecisionRemoved : DecisionHidden;
            var claimedAny = false;
            // the report this decision is about is claimed FIRST, before the listing is touched: a concurrent dismiss (or another hide) that won it leaves this request with a 409 and no side effect
            // (previously the listing was hidden, the seller e-mailed and the event said "dismissed")
            if (reportId is { } rid)
            {
                if (!await ClaimAsync(rid, s, ReportStatus.Actioned, remove ? "removed" : "hidden", reason)) return AlreadyResolved();
                var own = await db.ModerationReports.AsNoTracking().FirstAsync(x => x.Id == rid);
                await DecideAsync(own, decision, shown);
                claimedAny = true;
            }
            if (change)
            {
                if (!already) { p.ModerationPrevStatus = p.Status; p.ModeratedAt = time.UtcNow(); p.ModerationReason = reason; p.ModeratedBy = s.UserId; }
                if (remove) p.Status = ProductStatus.Archived;
                else if (p.Status == ProductStatus.Active) p.Status = ProductStatus.Inactive;
                p.Version++; p.UpdatedAt = time.UtcNow();
            }
            foreach (var r in await db.ModerationReports.AsNoTracking().Where(x => x.Kind == "listing" && x.TargetId == productId && x.Status == ReportStatus.Open).ToListAsync())
            {
                if (!await ClaimAsync(r.Id, s, ReportStatus.Actioned, remove ? "removed" : "hidden", reason)) continue;   // another decider got this one
                claimedAny = true;
                await DecideAsync(r, decision, shown);
            }
            if (!change && !claimedAny) return AlreadyResolved();
            audit.Add(s, change ? (remove ? "listing.removed" : "listing.hidden") : "listing.report_decided_hidden", "listing", productId, reason,
                new { status = p.ModerationPrevStatus.ToString() }, new { status = p.Status.ToString(), hidden = true }, "seller=" + p.SellerId);
            await db.SaveChangesAsync();
            if (change) await NotifyAsync(p.SellerId, "listing-hidden", "A listing was hidden", "One of your listings was hidden", reason, $"Solvelt hid your listing \"{p.Title}\".");
            return Results.Ok(new { productId, hidden = true });
        });
    }

    public async Task<IResult> DecideContentAsync(StaffContext s, Guid id, string reason, string decision, string? shown)
    {
        var r = await db.ModerationReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        if (r.Status != ReportStatus.Open) return AlreadyResolved();
        if (r.Kind == "listing") return await HideListingAsync(s, r.TargetId, reason, id, shown, remove: decision == DecisionRemoved);
        if (r.Kind == "review_reply") return await DecideReplyAsync(s, r, reason, decision, shown);
        return await InTx(async () =>
        {
            if (!await ClaimAsync(id, s, ReportStatus.Actioned, decision, reason)) return AlreadyResolved();
            await DecideAsync(r, decision, shown);
            await audit.WriteAsync(s, "moderation." + decision, "report", id, reason, extra: "kind=" + r.Kind);
            return Results.Ok(new { id, status = "Actioned", resolution = decision });
        });
    }

    /// <summary>
    /// Staff decision on a reported seller reply: the only content action is removal (a reply cannot be hidden). The report is claimed first, so of a parallel dismiss and remove exactly one wins;
    /// the winner of a removal removes the reply, queues <c>review.reply_removed</c> (reason <c>staff_removed</c>) and e-mails the seller once the decision is committed.
    /// </summary>
    private async Task<IResult> DecideReplyAsync(StaffContext s, ModerationReport r, string reason, string decision, string? shown)
    {
        if (decision != DecisionRemoved) return AdminHttp.Err(409, "unsupported_decision", "A reply can only be dismissed or removed.");
        string? title = null;
        var res = await InTx(async () =>
        {
            if (!await ClaimAsync(r.Id, s, ReportStatus.Actioned, "removed", reason)) return AlreadyResolved();
            await DecideAsync(r, DecisionRemoved, shown);
            var removed = await reviews.RemoveReplyForStaffAsync(r.TargetId);
            await audit.WriteAsync(s, "moderation.reply_removed", "report", r.Id, reason, extra: $"reply={r.TargetId};seller={r.SellerId};removed={removed}");
            if (removed) title = "removed";
            return Results.Ok(new { id = r.Id, status = "Actioned", resolution = "removed", replyRemoved = removed });
        });
        if (title is not null && (res as IStatusCodeHttpResult)?.StatusCode is null or < 400)
            await NotifyAsync(r.SellerId, "review-reply-removed", "A reply of yours was removed", "A reply of yours was removed", reason,
                "Solvelt removed one of your replies to a buyer review after a report. Please review our seller rules before replying again.");
        return res;
    }

    public async Task<IResult> UnhideListingAsync(StaffContext s, Guid productId, string reason)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId);
        if (p is null) return Results.NotFound();
        if (p.ModeratedAt is null) return AdminHttp.Err(409, "not_hidden", "This listing is not hidden by staff.");
        var prev = p.ModerationPrevStatus;
        var sellerUsable = await db.Sellers.AnyAsync(x => x.Id == p.SellerId && x.Status == SellerStatus.Approved);
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
            // only roles that can act on reports (Moderator, Super admin) read message text unmasked
            var full = StaffPermissions.Has(ctx.Staff().Role, StaffPermission.ModerationAct);
            string T(string? t) => full ? t ?? "" : Mask.Text(t);
            if (r.Kind is "message" or "contact_warning")
            {
                var msg = await db.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.TargetId);
                if (msg is not null)
                {
                    var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == msg.ConversationId);
                    var around = await db.Messages.AsNoTracking().Where(x => x.ConversationId == msg.ConversationId && x.CreatedAt <= msg.CreatedAt).OrderByDescending(x => x.CreatedAt).Take(6)
                        .Select(x => new { x.Id, direction = x.Direction.ToString(), x.Text, x.CreatedAt, x.ContactWarning, x.Reported }).ToListAsync();
                    target = new { conversationId = msg.ConversationId, subject = T(conv?.Subject), messages = around.OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.direction, text = T(x.Text), x.CreatedAt, x.ContactWarning, x.Reported }) };
                }
            }
            else if (r.Kind == "booking_message")
            {
                var msg = await db.BookingMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.TargetId);
                if (msg is not null) target = new { bookingId = msg.BookingId, messages = new[] { new { msg.Id, direction = msg.Direction, text = T(msg.Text), msg.CreatedAt } } };
            }
            else if (r.Kind == "listing") target = new { productId = r.TargetId };
            else if (r.Kind == "review_reply")
            {
                // the reply and the review it answers (the buyer's display name is the only thing about the buyer that exists here); text is masked for roles that cannot act
                var ctxRow = await (from rp in db.ReviewReplies.AsNoTracking()
                                    join rv in db.Reviews.AsNoTracking() on rp.ReviewId equals rv.Id
                                    join pr in db.Products.AsNoTracking() on rv.ProductId equals pr.Id into prs
                                    from pr in prs.DefaultIfEmpty()
                                    where rp.Id == r.TargetId
                                    select new { rp, rv, title = pr == null ? null : pr.Title }).FirstOrDefaultAsync();
                if (ctxRow is not null)
                    target = new
                    {
                        replyId = ctxRow.rp.Id, replyText = T(ctxRow.rp.Text), repliedAt = ctxRow.rp.CreatedAt, editedAt = ctxRow.rp.EditedAt, removed = ctxRow.rp.RemovedAt is not null, removedReason = ctxRow.rp.RemovedReason,
                        review = new { reviewId = ctxRow.rv.ExternalId, rating = ctxRow.rv.Rating, text = T(ctxRow.rv.Text), buyerDisplayName = ctxRow.rv.BuyerDisplayName, createdAt = ctxRow.rv.CreatedAt, state = ctxRow.rv.State.ToString().ToLowerInvariant(), productTitle = ctxRow.title }
                    };
            }
            var content = await db.ContentReports.AsNoTracking().Where(c => c.ModerationReportId == id).OrderBy(c => c.CreatedAt)
                .Select(c => new { c.ReportId, c.Kind, targetId = c.TargetRef, c.Reason, c.Note, c.ReporterRef, c.Decision, c.DecidedAt, c.CreatedAt }).ToListAsync();
            if (r.Kind == "review") target = new { reviewId = content.FirstOrDefault()?.targetId };
            await audit.WriteAsync(ctx.Staff(), "moderation.viewed", "report", id, null);
            return Results.Ok(new { r.Id, r.Kind, r.TargetId, r.SellerId, r.Reason, r.Summary, r.ReporterKind, status = r.Status.ToString(), r.CreatedAt, r.ResolvedAt, r.Resolution, r.ResolutionNote, target, contentReports = content });
        }, StaffPermission.ModerationView);

        m.APost("/{id:guid}/dismiss", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.DismissAsync(ctx.Staff(), id, reason, ModerationService.Shown(r.ReasonShownToUser)), StaffPermission.ModerationAct);
        m.APost("/{id:guid}/warn-seller", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.WarnAsync(ctx.Staff(), id, reason, ModerationService.Shown(r.ReasonShownToUser)), StaffPermission.ModerationAct);
        m.APost("/{id:guid}/escalate-suspend", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.EscalateAsync(ctx.Staff(), id, reason, ModerationService.Shown(r.ReasonShownToUser)), StaffPermission.ModerationAct);

        // decide a report by acting on the content: hide it (reversible) or remove it. Listings are hidden / archived here; messages and reviews are decided (the
        // portal cannot edit a review: it lives on the marketplace, which acts on the content.report_decided event).
        m.APost("/{id:guid}/hide-content", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.DecideContentAsync(ctx.Staff(), id, reason, ModerationService.DecisionHidden, ModerationService.Shown(r.ReasonShownToUser)), StaffPermission.ModerationAct);
        m.APost("/{id:guid}/remove-content", async (Guid id, ReasonRequest r, HttpContext ctx, ModerationService svc) =>
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.DecideContentAsync(ctx.Staff(), id, reason, ModerationService.DecisionRemoved, ModerationService.Shown(r.ReasonShownToUser)), StaffPermission.ModerationAct);

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
            AdminHttp.NeedReason(r.Reason, out var reason) ?? await svc.HideListingAsync(ctx.Staff(), id, reason, null, ModerationService.Shown(r.ReasonShownToUser)), StaffPermission.ModerationAct);
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
        g.MapPost("/content/reports", async (HttpContext ctx, InboundVerifier verifier, ModerationService svc, MessageService messages, Catalog.MarketplaceWriteLimits limits) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return limits.Rejected(ctx, v.Status, v.Code, v.Message);      // failed verification: only the per-IP bucket is touched
            ContentReportIn? req;
            try { req = JsonSerializer.Deserialize<ContentReportIn>(v.Body!, Orders.OrderJson.Opts); } catch (JsonException) { req = null; }
            if (req is null) return AdminHttp.Err(400, "invalid_json", "The request body is not valid JSON of the expected shape.");
            if (!limits.TryReport(req.TargetId)) return limits.Limited(ctx, "Too many reports for this target or from this caller.");
            var r = await svc.IntakeAsync(req, async (id, reason) => (await messages.ReportAsync(id, reason)).Status < 400);
            if (r.Status >= 400) return AdminHttp.Err(r.Status, r.Code, r.Message);
            return Results.Json(new { reportId = r.Report!.ReportId, status = r.Report.Decision is null ? "open" : "decided", decision = r.Report.Decision, duplicate = r.Duplicate }, statusCode: r.Duplicate ? 200 : 201);
        }).DisableRateLimiting();      // the group's per-IP "internal" policy would be a global cap for the one marketplace caller; this endpoint has its own keyed limits
        g.MapPost("/listings/{id:guid}/reports", (HttpContext ctx, InboundVerifier v, ModerationService svc, Guid id) => Handle(ctx, v, svc, "listing", id));
        g.MapPost("/booking-messages/{id:guid}/reports", (HttpContext ctx, InboundVerifier v, ModerationService svc, Guid id) => Handle(ctx, v, svc, "booking_message", id));
    }
}
