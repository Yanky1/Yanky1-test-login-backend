using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Bookings;

public record VersionIn(int? Version);
public record DeclineIn(int? Version, string? Reason);
public record TimeIn(int? Version, DateTime? Start);
public record DecideIn(int? Version, string? Note);
public record CompleteIn(int? Version, int? ActualMinutes);
public record ReassignIn(int? Version, Guid? ProviderId);
public record SendQuoteIn(decimal? Price, int? DurationMinutes, List<DateTime>? Slots, Guid? ProviderId, DateTime? ExpiresAt, string? Note, int? Version);
public record DeclineQuoteIn(string? Reason, int? Version);

public static class BookingEndpoints
{
    private static IResult ToResult(BRes r) => r.Ok ? Results.Json(r.Data ?? new { ok = true }, BJson.Opts) : ProviderEndpoints.Problem(r.Status, r.Code, r.Message);

    private static readonly BookingStatus[] Upcoming = [BookingStatus.Confirmed, BookingStatus.Rescheduled, BookingStatus.InProgress];
    private static readonly BookingStatus[] Closed = [BookingStatus.CancelledByBuyer, BookingStatus.CancelledBySeller, BookingStatus.Declined, BookingStatus.Expired];

    private static IQueryable<Booking> Tab(IQueryable<Booking> q, string? tab, DateTime now, DateTime dayStart, DateTime dayEnd) => tab?.ToLowerInvariant() switch
    {
        "requests" => q.Where(b => b.Status == BookingStatus.Requested || b.RescheduleRequestedStartUtc != null),
        "upcoming" => q.Where(b => Upcoming.Contains(b.Status) && b.EndUtc >= now),
        "today" => q.Where(b => (Upcoming.Contains(b.Status) || b.Status == BookingStatus.Requested) && b.StartUtc < dayEnd && b.EndUtc >= dayStart),
        "past" => q.Where(b => b.Status == BookingStatus.Completed || b.Status == BookingStatus.NoShow || (Upcoming.Contains(b.Status) && b.EndUtc < now)),
        "cancelled" => q.Where(b => Closed.Contains(b.Status)),
        _ => q
    };

    private static async Task<Booking?> Load(AppDbContext db, AccountAccess a, Guid id, bool write)
    {
        var b = await (await db.BookingsForAsync(a)).FirstOrDefaultAsync(x => x.Id == id);
        return b is not null && (!write || BookingAccess.CanAct(a, b)) ? b : null;
    }

    private static object Actions(AccountAccess a, Booking b)
    {
        var can = BookingAccess.CanAct(a, b);
        bool Ok(BookingStateMachine.Act x) => can && BookingStateMachine.Can(b.Status, x);
        var started = BJson.Utc(b.StartUtc) <= DateTime.UtcNow;
        return new
        {
            approve = Ok(BookingStateMachine.Act.Approve), decline = Ok(BookingStateMachine.Act.Decline), propose = Ok(BookingStateMachine.Act.Propose),
            reschedule = Ok(BookingStateMachine.Act.Reschedule), cancel = Ok(BookingStateMachine.Act.CancelBySeller), start = Ok(BookingStateMachine.Act.Start),
            complete = Ok(BookingStateMachine.Act.Complete), noShow = Ok(BookingStateMachine.Act.NoShow) && started, reassign = Ok(BookingStateMachine.Act.Reassign),
            decideReschedule = can && b.RescheduleRequestedStartUtc != null
        };
    }

    public static async Task<object> DetailAsync(AppDbContext db, AccountAccess a, Booking b, CalendarService cal)
    {
        var events = await db.BookingEvents.AsNoTracking().Where(e => e.BookingId == b.Id).OrderBy(e => e.At).ToListAsync();
        var d = await db.ServiceDetails.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == b.ProductId);
        var zone = await cal.ZoneAsync(b.SellerId, b.ProviderId);
        var email = await (from m in db.Memberships join u in db.Users on m.UserId equals u.Id where m.Id == b.ProviderId select u.Email).FirstOrDefaultAsync();
        var pii = BookingAccess.SeesCustomerData(a, b);
        var intake = BJson.From<List<IntakeAnswer>>(b.IntakeAnswersJson) ?? [];
        var questions = d is null ? [] : BJson.Intake(d);
        var policy = BJson.From<BookingPolicy>(b.PolicyJson);
        return new
        {
            id = b.Id, number = b.Number, status = b.Status.ToString(), version = b.Version, serviceId = b.ProductId, serviceTitle = b.ServiceTitle, brandId = b.BrandId,
            providerId = b.ProviderId, providerEmail = email, providerTimeZone = zone.Id, isMine = b.ProviderId == a.MembershipId,
            location = b.Location.ToString().ToLowerInvariant(), startAt = BJson.Utc(b.StartUtc), endAt = BJson.Utc(b.EndUtc), durationMinutes = b.DurationMinutes,
            approvalDeadline = b.ApprovalDeadline, proposedStartAt = b.ProposedStartUtc, rescheduleRequestedStartAt = b.RescheduleRequestedStartUtc, rescheduleCount = b.RescheduleCount,
            pricing = new
            {
                model = b.PricingModel.ToString().ToLowerInvariant(), rateCents = b.RateCents, priceCents = b.PriceCents, addOnsCents = b.AddOnsCents, travelFeeCents = b.TravelFeeCents, totalCents = b.TotalCents,
                cancellationFeeCents = b.CancellationFeeCents, additionalChargeCents = b.AdditionalChargeCents, actualMinutes = b.ActualMinutes
            },
            policy,
            addOns = BJson.From<List<AddOn>>(b.AddOnsJson) ?? [],
            intake = intake.Select(i => new { id = i.Id, question = questions.FirstOrDefault(q => q.Id == i.Id)?.Label ?? i.Id, answer = pii || (b.Status == BookingStatus.Requested && BookingAccess.CanAct(a, b)) ? i.Answer : null }),
            customer = pii ? new { name = b.BuyerName, phone = b.BuyerPhone, address = BJson.From<System.Text.Json.JsonElement?>(b.AddressJson), postalCode = b.PostalCode, visible = true, redacted = false }
                : (object)new { name = b.PiiRedactedAt is null && b.Status == BookingStatus.Requested ? b.BuyerName : null, phone = (string?)null, address = (object?)null, postalCode = b.Location == LocationKind.Customer && b.PiiRedactedAt is null ? b.PostalCode : null, visible = false, redacted = b.PiiRedactedAt != null },
            onlineInstructions = b.Location == LocationKind.Online ? d?.OnlineInstructions : null,
            sellerAddress = b.Location == LocationKind.Seller ? d?.SellerAddress : null,
            requirements = d?.Requirements,
            timeline = events.Select(e => new { at = BJson.Utc(e.At), type = e.Type, actor = e.Actor, text = e.Text }),
            actions = Actions(a, b), createdAt = b.CreatedAt
        };
    }

    public static void MapBookingEndpoints(this RouteGroupBuilder g)
    {
        var s = g.MapGroup("/seller/bookings").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("bookings");

        s.MapGet("", async (HttpContext ctx, AppDbContext db, CalendarService cal, TimeProvider time, string? tab, Guid? providerId, Guid? brandId, Guid? serviceId, string? q, DateTime? from, DateTime? to, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!;
            var now = time.UtcNow();
            var zone = TimeZoneInfo.FindSystemTimeZoneById(await cal.AccountZoneIdAsync(a.SellerId));
            var day = SlotEngine.LocalDate(zone, now);
            var dayStart = SlotEngine.LocalToUtc(zone, day, 0); var dayEnd = SlotEngine.LocalToUtc(zone, day.AddDays(1), 0);
            var rows = Tab((await db.BookingsForAsync(a)).AsNoTracking(), tab, now, dayStart, dayEnd);
            if (providerId is { } pid) rows = rows.Where(b => b.ProviderId == pid);
            if (brandId is { } bid) rows = rows.Where(b => b.BrandId == bid);
            if (serviceId is { } sid) rows = rows.Where(b => b.ProductId == sid);
            if (from is { } f0) { var f = BJson.Utc(f0); rows = rows.Where(b => b.StartUtc >= f); }
            if (to is { } t0) { var t = BJson.Utc(t0); rows = rows.Where(b => b.StartUtc < t); }
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower(); if (term.Length > 100) term = term[..100];
                rows = rows.Where(b => b.Number.ToLower().Contains(term) || b.ServiceTitle.ToLower().Contains(term) || b.MarketplaceBookingId.ToLower().Contains(term) || (b.PiiRedactedAt == null && b.BuyerName.ToLower().Contains(term)));
            }
            var total = await rows.CountAsync();
            var upcomingFirst = tab is "requests" or "upcoming" or "today";
            var ordered = upcomingFirst ? rows.OrderBy(b => b.StartUtc) : rows.OrderByDescending(b => b.StartUtc);
            var size = Math.Clamp(pageSize ?? 25, 1, 100); var pg = Math.Max(page ?? 1, 1);
            var list = await ordered.ThenBy(b => b.Id).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Json(new
            {
                items = list.Select(b => new
                {
                    id = b.Id, number = b.Number, status = b.Status.ToString(), serviceId = b.ProductId, serviceTitle = b.ServiceTitle, brandId = b.BrandId, providerId = b.ProviderId,
                    location = b.Location.ToString().ToLowerInvariant(), startAt = BJson.Utc(b.StartUtc), endAt = BJson.Utc(b.EndUtc), totalCents = b.TotalCents,
                    approvalDeadline = b.ApprovalDeadline, rescheduleRequested = b.RescheduleRequestedStartUtc != null, isMine = b.ProviderId == a.MembershipId,
                    buyerName = b.PiiRedactedAt == null && (b.Status == BookingStatus.Requested || BookingAccess.SeesCustomerData(a, b)) ? b.BuyerName : null
                }),
                total, page = pg, pageSize = size
            }, BJson.Opts);
        }).RequireActiveAccount();

        s.MapGet("/counts", async (HttpContext ctx, AppDbContext db, CalendarService cal, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var now = time.UtcNow();
            var zone = TimeZoneInfo.FindSystemTimeZoneById(await cal.AccountZoneIdAsync(a.SellerId));
            var day = SlotEngine.LocalDate(zone, now);
            var dayStart = SlotEngine.LocalToUtc(zone, day, 0); var dayEnd = SlotEngine.LocalToUtc(zone, day.AddDays(1), 0);
            var q = (await db.BookingsForAsync(a)).AsNoTracking();
            var requests = await Tab(q, "requests", now, dayStart, dayEnd).CountAsync();
            var today = await Tab(q, "today", now, dayStart, dayEnd).CountAsync();
            var upcoming = await Tab(q, "upcoming", now, dayStart, dayEnd).CountAsync();
            var quotes = await QuotesFor(db, a).CountAsync(x => x.Status == QuoteStatus.Requested);
            return Results.Json(new { requests, today, upcoming, quoteRequests = quotes, needsAction = requests + quotes });
        }).RequireActiveAccount();

        s.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CalendarService cal) =>
        {
            var a = ctx.GetAccess()!;
            var b = await Load(db, a, id, false);
            return b is null ? Results.NotFound() : Results.Json(await DetailAsync(db, a, b, cal), BJson.Opts);
        }).RequireActiveAccount();

        async Task<IResult> Act(HttpContext ctx, AppDbContext db, CalendarService cal, IAuditLog audit, Guid id, string audited, Func<Booking, AccountAccess, Task<BRes>> run)
        {
            var a = ctx.GetAccess()!;
            var b = await Load(db, a, id, true);
            if (b is null)
            {
                // visible but not actionable by this caller -> 403; invisible -> 404
                return await Load(db, a, id, false) is null ? Results.NotFound() : AccessExtensions.Forbidden();
            }
            var r = await run(b, a);
            if (!r.Ok) return ToResult(r);
            if (audited.Length > 0) await audit.WriteAsync(audited, a.UserId, null, $"booking={id};seller={a.SellerId}");
            db.ChangeTracker.Clear();
            var fresh = await Load(db, a, id, false);
            // the action may have moved the booking out of the caller's own scope (e.g. a provider reassigning it away)
            return fresh is null ? Results.Json(new { ok = true, gone = true }) : Results.Json(await DetailAsync(db, a, fresh, cal), BJson.Opts);
        }

        s.MapPost("/{id:guid}/approve", (Guid id, VersionIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.approved", (b, a) => svc.ApproveAsync(b, Actor.Seller(a.UserId), r.Version)));
        s.MapPost("/{id:guid}/decline", (Guid id, DeclineIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.declined", (b, a) => svc.DeclineAsync(b, Actor.Seller(a.UserId), r.Reason, r.Version)));
        s.MapPost("/{id:guid}/propose", (Guid id, TimeIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.time_proposed", (b, a) => r.Start is null ? Task.FromResult(BRes.Invalid("Choose a start time.", "start")) : svc.ProposeAsync(b, Actor.Seller(a.UserId), r.Start.Value, r.Version)));
        s.MapPost("/{id:guid}/reschedule", (Guid id, TimeIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.rescheduled", (b, a) => r.Start is null ? Task.FromResult(BRes.Invalid("Choose a start time.", "start")) : svc.RescheduleAsync(b, Actor.Seller(a.UserId), r.Start.Value, r.Version)));
        s.MapPost("/{id:guid}/approve-reschedule", (Guid id, DecideIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.reschedule_approved", (b, a) => svc.DecideRescheduleAsync(b, Actor.Seller(a.UserId), true, r.Note, r.Version)));
        s.MapPost("/{id:guid}/decline-reschedule", (Guid id, DecideIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.reschedule_declined", (b, a) => svc.DecideRescheduleAsync(b, Actor.Seller(a.UserId), false, r.Note, r.Version)));
        s.MapPost("/{id:guid}/cancel", (Guid id, DeclineIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.cancelled_by_seller", (b, a) => string.IsNullOrWhiteSpace(r.Reason) ? Task.FromResult(BRes.Invalid("Give a reason; the buyer will see it.", "reason")) : svc.CancelAsync(b, Actor.Seller(a.UserId), r.Reason, r.Version)));
        s.MapPost("/{id:guid}/start", (Guid id, VersionIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "", (b, a) => svc.StartAsync(b, Actor.Seller(a.UserId), r.Version)));
        s.MapPost("/{id:guid}/complete", (Guid id, CompleteIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.completed", (b, a) => svc.CompleteAsync(b, Actor.Seller(a.UserId), r.ActualMinutes, r.Version)));
        s.MapPost("/{id:guid}/no-show", (Guid id, VersionIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.no_show", (b, a) => svc.NoShowAsync(b, Actor.Seller(a.UserId), r.Version)));
        s.MapPost("/{id:guid}/reassign", (Guid id, ReassignIn r, HttpContext ctx, AppDbContext db, CalendarService cal, BookingService svc, IAuditLog audit) =>
            Act(ctx, db, cal, audit, id, "booking.reassigned", (b, a) => r.ProviderId is null ? Task.FromResult(BRes.Invalid("Choose a provider.", "provider")) : svc.ReassignAsync(b, Actor.Seller(a.UserId), r.ProviderId.Value, r.Version)));

        s.MapGet("/{id:guid}/messages", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            if (await Load(db, a, id, false) is null) return Results.NotFound();
            var rows = await db.BookingMessages.AsNoTracking().Where(m => m.BookingId == id).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Take(500).ToListAsync();
            return Results.Json(new { messages = rows.Select(m => new { id = m.Id, direction = m.Direction, text = m.Text, createdAt = BJson.Utc(m.CreatedAt), mine = m.AuthorUserId == a.UserId }) }, BJson.Opts);
        }).RequireActiveAccount();

        s.MapPost("/{id:guid}/messages", async (Guid id, BookingMessageOut r, HttpContext ctx, AppDbContext db, Outbox outbox, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var b = await Load(db, a, id, true);
            if (b is null) return await Load(db, a, id, false) is null ? Results.NotFound() : AccessExtensions.Forbidden();
            if (string.IsNullOrWhiteSpace(r.Text) || r.Text.Length > 2000) return Validation.Invalid("text", "Write a message of up to 2000 characters.");
            if (b.Status is BookingStatus.Hold or BookingStatus.Expired || b.PiiRedactedAt is not null) return ProviderEndpoints.Problem(409, "thread_closed", "This booking can't receive messages.");
            var m = new BookingMessage { BookingId = id, SellerId = b.SellerId, Direction = "seller", AuthorUserId = a.UserId, Text = r.Text.Trim(), CreatedAt = time.UtcNow() };
            db.BookingMessages.Add(m);
            outbox.Enqueue("message.sent", b.SellerId, b.Id.ToString(), new
            {
                messageId = m.Id, kind = "booking", booking = new { bookingId = b.Id, number = b.Number, marketplaceBookingId = b.MarketplaceBookingId }, text = m.Text, sentAt = m.CreatedAt
            });
            await db.SaveChangesAsync();
            return Results.Json(new { id = m.Id, direction = m.Direction, text = m.Text, createdAt = m.CreatedAt, mine = true }, BJson.Opts, statusCode: 201);
        }).RequireActiveAccount();

        MapQuotes(g);
    }

    // ---- quotes ----------------------------------------------------------------------------------------------------------------
    internal static IQueryable<QuoteRequest> QuotesFor(AppDbContext db, AccountAccess a)
    {
        var q = db.QuoteRequests.Where(x => x.SellerId == a.SellerId);
        if (a.Role >= AccountRole.Admin || a.AllBrands) return q;
        var ids = a.BrandIds.ToList();
        // Viewers read their brands; only Managers/Owners/Admins may answer (checked on write)
        return q.Where(x => ids.Contains(x.BrandId));
    }

    private static object QuoteDto(QuoteRequest x, AccountAccess a, bool full)
    {
        var pii = x.PiiRedactedAt is null && (a.Role >= AccountRole.Admin || x.QuoteProviderId == a.MembershipId);
        return new
        {
            id = x.Id, status = x.Status.ToString(), serviceId = x.ProductId, serviceTitle = x.ServiceTitle, brandId = x.BrandId, location = x.Location.ToString().ToLowerInvariant(),
            buyerName = x.PiiRedactedAt is null ? x.BuyerName : null, details = x.PiiRedactedAt is null ? x.Details : null, postalCode = x.PiiRedactedAt is null ? x.PostalCode : null,
            preferredTimes = x.PiiRedactedAt is null ? BJson.From<List<DateTime>>(x.PreferredTimesJson) : null,
            address = full && pii ? BJson.From<System.Text.Json.JsonElement?>(x.AddressJson) : null, phone = full && pii ? x.BuyerPhone : null,
            quote = x.QuotedAt is null ? null : new
            {
                priceCents = x.QuotePriceCents, durationMinutes = x.QuoteDurationMinutes, slots = BJson.From<List<DateTime>>(x.SlotsJson), providerId = x.QuoteProviderId,
                expiresAt = x.QuoteExpiresAt, note = x.QuoteNote, quotedAt = x.QuotedAt
            },
            bookingId = x.BookingId, declineReason = x.DeclineReason, createdAt = x.CreatedAt, version = x.Version
        };
    }

    private static void MapQuotes(RouteGroupBuilder g)
    {
        var s = g.MapGroup("/seller/quote-requests").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("bookings");

        s.MapGet("", async (HttpContext ctx, AppDbContext db, string? status, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!;
            var q = QuotesFor(db, a).AsNoTracking();
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<QuoteStatus>(status, true, out var st)) return Validation.Invalid("status", "Unknown status.");
                q = q.Where(x => x.Status == st);
            }
            var total = await q.CountAsync();
            var size = Math.Clamp(pageSize ?? 25, 1, 100); var pg = Math.Max(page ?? 1, 1);
            var rows = await q.OrderByDescending(x => x.CreatedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Json(new { items = rows.Select(x => QuoteDto(x, a, false)), total, page = pg, pageSize = size }, BJson.Opts);
        }).RequireActiveAccount();

        s.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            var x = await QuotesFor(db, a).AsNoTracking().FirstOrDefaultAsync(q => q.Id == id);
            return x is null ? Results.NotFound() : Results.Json(QuoteDto(x, a, true), BJson.Opts);
        }).RequireActiveAccount();

        s.MapPost("/{id:guid}/quote", async (Guid id, SendQuoteIn r, HttpContext ctx, AppDbContext db, QuoteService svc, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var x = await QuotesFor(db, a).FirstOrDefaultAsync(q => q.Id == id);
            if (x is null) return Results.NotFound();
            if (!a.CanWorkOnBrand(x.BrandId, true) && !(a.Role >= AccountRole.Admin)) return AccessExtensions.Forbidden();
            if (a.Role < AccountRole.Manager) return AccessExtensions.Forbidden();
            var res = await svc.SendAsync(x, a, r);
            if (!res.Ok) return ToResult(res);
            await audit.WriteAsync("quote.sent", a.UserId, null, $"quote={id};seller={a.SellerId}");
            return Results.Json(QuoteDto(x, a, true), BJson.Opts);
        }).RequireActiveAccount();

        s.MapPost("/{id:guid}/decline", async (Guid id, DeclineQuoteIn r, HttpContext ctx, AppDbContext db, QuoteService svc, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var x = await QuotesFor(db, a).FirstOrDefaultAsync(q => q.Id == id);
            if (x is null) return Results.NotFound();
            if (a.Role < AccountRole.Manager || !a.CanWorkOnBrand(x.BrandId, true)) return AccessExtensions.Forbidden();
            var res = await svc.DeclineAsync(x, a, r.Reason);
            if (!res.Ok) return ToResult(res);
            await audit.WriteAsync("quote.declined_by_seller", a.UserId, null, $"quote={id};seller={a.SellerId}");
            return Results.Json(QuoteDto(x, a, true), BJson.Opts);
        }).RequireActiveAccount();
    }
}

/// <summary>Quote requests for by-job services priced "quote required".</summary>
public class QuoteService(AppDbContext db, CalendarService cal, BookingService bookings, Outbox outbox, TimeProvider time)
{
    public async Task<BRes> CreateRequestAsync(QuoteIn r)
    {
        if (r.MarketplaceQuoteId is not { Length: > 0 and <= 64 } qid) return BRes.Invalid("marketplaceQuoteId is required (up to 64 characters).", "marketplace_id");
        var existing = await db.QuoteRequests.AsNoTracking().FirstOrDefaultAsync(x => x.MarketplaceQuoteId == qid);
        if (existing is not null) return new BRes(true, 200, "exists", "", new { created = false, quoteId = existing.Id, marketplaceQuoteId = qid, status = existing.Status.ToString() });
        if (r.ServiceId is not { } sid) return BRes.Invalid("serviceId is required.", "invalid");
        var (p, d) = await bookings.ServiceAsync(sid);
        if (p is null || d is null) return BRes.Fail(404, "service_not_found", "Service not found.");
        if (d.PricingModel != PricingModel.Quote) return BRes.Invalid("This service has a fixed or hourly price; book it directly.", "not_quote_service");
        if (string.IsNullOrWhiteSpace(r.Buyer?.DisplayName)) return BRes.Invalid("buyer.displayName is required.", "buyer_required");
        if (string.IsNullOrWhiteSpace(r.Details) || r.Details.Length > 4000) return BRes.Invalid("Describe the job (up to 4000 characters).", "details");
        var loc = BookingService.ParseLocation(r.Location);
        if (loc is null) return BRes.Invalid("Choose online, customer or seller as the location.", "location");
        if (!(loc switch { LocationKind.Online => d.Online, LocationKind.Customer => d.AtCustomer, _ => d.AtSeller })) return BRes.Invalid("This service isn't offered at that location.", "location_not_offered");
        if (loc == LocationKind.Customer && !BookingService.InServiceArea(d, r.Address?.PostalCode, r.Address?.Lat, r.Address?.Lng)) return BRes.Invalid("The address is outside the service area.", "outside_service_area");
        var preferred = (r.PreferredTimes ?? []).Select(BJson.Utc).Take(10).ToList();
        var now = time.UtcNow();
        var x = new QuoteRequest
        {
            SellerId = p.SellerId, BrandId = p.BrandId, ProductId = p.Id, MarketplaceQuoteId = qid, BuyerName = r.Buyer!.DisplayName!.Trim()[..Math.Min(200, r.Buyer.DisplayName.Trim().Length)],
            BuyerEmail = r.Buyer.RelayEmail?.Trim(), BuyerPhone = r.Buyer.Phone?.Trim(), Details = r.Details.Trim(), Location = loc.Value,
            AddressJson = r.Address is null ? null : BJson.Ser(r.Address), PostalCode = r.Address?.PostalCode?.Trim(), PreferredTimesJson = BJson.Ser(preferred), ServiceTitle = p.Title, CreatedAt = now, UpdatedAt = now
        };
        db.QuoteRequests.Add(x);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.QuoteRequests.AsNoTracking().FirstOrDefaultAsync(q => q.MarketplaceQuoteId == qid);
            if (winner is not null) return new BRes(true, 200, "exists", "", new { created = false, quoteId = winner.Id, marketplaceQuoteId = qid, status = winner.Status.ToString() });
            throw;
        }
        return new BRes(true, 201, "created", "", new { created = true, quoteId = x.Id, marketplaceQuoteId = qid, status = x.Status.ToString() });
    }

    public async Task<BRes> SendAsync(QuoteRequest x, AccountAccess a, SendQuoteIn r)
    {
        if (r.Version is { } v && v != x.Version) return BRes.Conflict("This request changed. Reload and try again.", "version_conflict");
        if (x.Status is not (QuoteStatus.Requested or QuoteStatus.Quoted)) return BRes.Conflict($"A {x.Status} quote request can't be quoted.");
        var now = time.UtcNow();
        if (r.Price is not { } price || price < 0 || price > 1_000_000m || decimal.Round(price, 2) != price) return BRes.Invalid("Enter a price between 0 and 1,000,000.", "price");
        if (r.DurationMinutes is not { } dur || dur is < 5 or > 1440) return BRes.Invalid("Enter the estimated duration (5 minutes to 24 hours).", "duration");
        if (r.ExpiresAt is not { } exp || BJson.Utc(exp) <= now || BJson.Utc(exp) > now.AddDays(60)) return BRes.Invalid("The quote must expire in the future (within 60 days).", "expires");
        if (r.Note is { Length: > 2000 }) return BRes.Invalid("The note is too long.", "note");
        var slots = (r.Slots ?? []).Select(BJson.Utc).Distinct().OrderBy(s => s).ToList();
        if (slots.Count is 0 or > 10) return BRes.Invalid("Propose between 1 and 10 start times.", "slots");
        var (p, d) = await bookings.ServiceAsync(x.ProductId);
        if (p is null || d is null) return BRes.Fail(409, "service_unavailable", "The service is no longer active.");
        var providers = await cal.EligibleQuery(x.ProductId).Select(m => m.Id).ToListAsync();
        var provider = r.ProviderId ?? (providers.Count == 1 ? providers[0] : (Guid?)null);
        if (provider is null || !providers.Contains(provider.Value)) return BRes.Invalid("Choose a provider who is eligible for this service.", "provider");
        var timing = BookingService.Timing(d, dur);
        var c = await cal.LoadAsync(x.SellerId, provider.Value, slots[0].AddDays(-2), slots[^1].AddDays(2), now);
        foreach (var s in slots)
            if (!SlotEngine.IsBookable(c, timing, now, s, dur, x.Location == LocationKind.Customer) || !SlotEngine.Slots(c, timing, now, s, s.AddSeconds(1), dur, x.Location == LocationKind.Customer).Contains(s))
                return BRes.Invalid($"{s:u} isn't free for that provider.", "slot_unavailable");
        x.QuotePriceCents = Money.ToCents(price); x.QuoteDurationMinutes = dur; x.SlotsJson = BJson.Ser(slots); x.QuoteProviderId = provider; x.QuoteExpiresAt = BJson.Utc(exp);
        x.QuoteNote = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim(); x.QuotedAt = now; x.QuotedBy = a.UserId; x.Status = QuoteStatus.Quoted; x.UpdatedAt = now; x.Version++;
        outbox.Enqueue("quote.sent", x.SellerId, x.Id.ToString(), new
        {
            quote = new { quoteId = x.Id, marketplaceQuoteId = x.MarketplaceQuoteId }, serviceId = x.ProductId, priceCents = x.QuotePriceCents, durationMinutes = dur, slots, providerId = provider,
            expiresAt = x.QuoteExpiresAt, note = x.QuoteNote, currency = "USD"
        });
        return await SaveQuote(x);
    }

    public async Task<BRes> DeclineAsync(QuoteRequest x, AccountAccess a, string? reason)
    {
        if (x.Status is not (QuoteStatus.Requested or QuoteStatus.Quoted)) return BRes.Conflict($"A {x.Status} quote request can't be declined.");
        x.Status = QuoteStatus.Declined; x.DeclineReason = reason?.Trim() is { Length: > 0 } rs ? rs[..Math.Min(500, rs.Length)] : null; x.UpdatedAt = time.UtcNow(); x.Version++;
        outbox.Enqueue("quote.declined", x.SellerId, x.Id.ToString(), new { quote = new { quoteId = x.Id, marketplaceQuoteId = x.MarketplaceQuoteId }, reason = x.DeclineReason, by = "seller" });
        return await SaveQuote(x);
    }

    private async Task<BRes> SaveQuote(QuoteRequest x)
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return BRes.Conflict("This request changed at the same time. Reload and try again.", "version_conflict"); }
        return BRes.Success();
    }

    /// <summary>Buyer accepts the quote with one of the proposed slots: the booking is created (slot reserved) and the quote is closed. Idempotent on the booking id.</summary>
    public async Task<BRes> AcceptAsync(string marketplaceQuoteId, QuoteAcceptIn r)
    {
        var x = await db.QuoteRequests.FirstOrDefaultAsync(q => q.MarketplaceQuoteId == marketplaceQuoteId);
        if (x is null) return BRes.Fail(404, "quote_not_found", "Quote not found.");
        if (x.Status == QuoteStatus.Accepted && x.BookingId is not null && r.MarketplaceBookingId is { } mb0 && await db.Bookings.AnyAsync(b => b.Id == x.BookingId && b.MarketplaceBookingId == mb0))
        {
            var done = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == x.BookingId);
            return new BRes(true, 200, "exists", "", new { created = false, booking = BookingService.Summary(done) });
        }
        if (x.Status == QuoteStatus.Accepted && x.BookingId is not null)
        {
            // accepting while the quote's hold is live returns that same hold (C13)
            var now0 = time.UtcNow();
            var live = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == x.BookingId && b.Status == BookingStatus.Hold && b.HoldExpiresAt > now0);
            if (live is not null) return new BRes(true, 200, "exists", "", new { created = false, booking = BookingService.Summary(live) });
        }
        if (x.Status == QuoteStatus.Expired) return BRes.Conflict("This quote has expired.", "quote_expired");
        if (x.Status != QuoteStatus.Quoted) return BRes.Conflict($"A {x.Status} quote can't be accepted.", "quote_not_open");
        if (x.QuoteExpiresAt is { } e && BJson.Utc(e) <= time.UtcNow()) return BRes.Conflict("This quote has expired.", "quote_expired");
        if (r.Start is not { } start) return BRes.Invalid("Choose one of the proposed times.", "start");
        var slots = BJson.From<List<DateTime>>(x.SlotsJson) ?? [];
        if (!slots.Select(BJson.Utc).Contains(BJson.Utc(start))) return BRes.Invalid("That time wasn't part of the quote.", "slot_not_offered");
        var req = new BookingIn(r.MarketplaceBookingId, x.ProductId, x.QuoteProviderId, start, null, x.Location.ToString(), r.AddOnIds, r.Buyer ?? new BuyerIn(x.BuyerName, x.BuyerEmail, x.BuyerPhone),
            string.IsNullOrEmpty(x.AddressJson) ? null : BJson.From<AddressIn>(x.AddressJson), r.IntakeAnswers);
        var res = await bookings.CreateAsync(req, hold: true, quote: x);
        if (!res.Ok) return res;
        return res;
    }

    public async Task<BRes> BuyerDeclineAsync(string marketplaceQuoteId, string? reason)
    {
        var x = await db.QuoteRequests.FirstOrDefaultAsync(q => q.MarketplaceQuoteId == marketplaceQuoteId);
        if (x is null) return BRes.Fail(404, "quote_not_found", "Quote not found.");
        if (x.Status == QuoteStatus.Withdrawn) return BRes.Success();
        if (x.Status is not (QuoteStatus.Requested or QuoteStatus.Quoted)) return BRes.Conflict($"A {x.Status} quote can't be declined.", "quote_not_open");
        x.Status = QuoteStatus.Withdrawn; x.DeclineReason = reason?.Trim() is { Length: > 0 } rs ? rs[..Math.Min(500, rs.Length)] : null; x.UpdatedAt = time.UtcNow(); x.Version++;
        return await SaveQuote(x);
    }
}

public record QuoteIn(string? MarketplaceQuoteId, Guid? ServiceId, BuyerIn? Buyer, string? Details, string? Location, AddressIn? Address, List<DateTime>? PreferredTimes);
public record QuoteAcceptIn(string? MarketplaceBookingId, DateTime? Start, List<string>? AddOnIds, BuyerIn? Buyer, List<IntakeAnswer>? IntakeAnswers);
