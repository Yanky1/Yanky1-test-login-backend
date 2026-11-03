using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Teams;
using Solvelt.Api.Products;
using Solvelt.Api.Services;

namespace Solvelt.Api.Bookings;

public record BuyerIn(string? DisplayName, string? RelayEmail, string? Phone);
public record AddressIn(string? Name, string? Line1, string? Line2, string? City, string? Region, string? PostalCode, string? Country, double? Lat, double? Lng);
public record BookingIn(string? MarketplaceBookingId, Guid? ServiceId, Guid? ProviderId, DateTime? Start, int? DurationMinutes, string? Location, List<string>? AddOnIds,
    BuyerIn? Buyer, AddressIn? Address, List<IntakeAnswer>? IntakeAnswers);
public record Actor(string Kind, Guid? UserId = null)
{
    public static readonly Actor Buyer = new("buyer"), System = new("system");
    public static Actor Seller(Guid userId) => new("seller", userId);
}

/// <summary>Creates holds and bookings (no double booking), and applies every lifecycle action. All state changes go through <see cref="BookingStateMachine"/>.</summary>
public partial class BookingService(AppDbContext db, CalendarService cal, Outbox outbox, IOptions<BookingOptions> opt, TimeProvider time)
{
    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,64}$")] private static partial Regex IdRe();

    // ---- helpers --------------------------------------------------------------------------------------------------------------
    public static LocationKind? ParseLocation(string? s) => s?.Trim().ToLowerInvariant() switch { "online" => LocationKind.Online, "customer" => LocationKind.Customer, "seller" => LocationKind.Seller, _ => null };

    public static long Prorate(long rateCentsPerHour, int minutes) => (long)Math.Round(rateCentsPerHour * (decimal)minutes / 60m, 0, MidpointRounding.AwayFromZero);

    public static BookingPolicy PolicyOf(ServiceDetails d) => new(d.CancelFreeHours, d.CancelFeePercent, d.MaxReschedules, d.NoShowFeePercent, d.IncrementMinutes, d.ApprovalHours);

    public static object Summary(Booking b, string? onlineInstructions = null) => new
    {
        bookingId = b.Id, number = b.Number, marketplaceBookingId = b.MarketplaceBookingId, status = b.Status.ToString(), serviceId = b.ProductId, providerId = b.ProviderId,
        location = b.Location.ToString().ToLowerInvariant(), startAt = BJson.Utc(b.StartUtc), endAt = BJson.Utc(b.EndUtc), durationMinutes = b.DurationMinutes,
        holdExpiresAt = b.HoldExpiresAt, approvalDeadline = b.ApprovalDeadline, priceCents = b.PriceCents, addOnsCents = b.AddOnsCents, travelFeeCents = b.TravelFeeCents,
        totalCents = b.TotalCents, cancellationFeeCents = b.CancellationFeeCents, additionalChargeCents = b.AdditionalChargeCents, proposedStartAt = b.ProposedStartUtc,
        rescheduleCount = b.RescheduleCount, onlineInstructions = b.Location == LocationKind.Online && b.Status is BookingStatus.Confirmed or BookingStatus.Rescheduled or BookingStatus.InProgress ? onlineInstructions : null
    };

    private async Task<string?> InstructionsAsync(Booking b) =>
        b.Location == LocationKind.Online ? await db.ServiceDetails.AsNoTracking().Where(d => d.ProductId == b.ProductId).Select(d => d.OnlineInstructions).FirstOrDefaultAsync() : null;

    private Dictionary<string, object?> Data(Booking b, object? extra = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["booking"] = new { bookingId = b.Id, number = b.Number, marketplaceBookingId = b.MarketplaceBookingId },
            ["status"] = b.Status.ToString(), ["serviceId"] = b.ProductId, ["providerId"] = b.ProviderId, ["location"] = b.Location.ToString().ToLowerInvariant(),
            ["startAt"] = BJson.Utc(b.StartUtc), ["endAt"] = BJson.Utc(b.EndUtc), ["totalCents"] = b.TotalCents
        };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) d[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        return d;
    }

    private void Event(Booking b, string type, Actor actor, string? text = null) =>
        db.BookingEvents.Add(new BookingEvent { BookingId = b.Id, At = time.UtcNow(), Type = type, Actor = actor.Kind, Text = text is { Length: > 1000 } ? text[..1000] : text });

    private async Task<bool> ClaimCalendarAsync(Guid providerId) =>
        await db.ProviderProfiles.Where(p => p.MembershipId == providerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CalendarSeq, p => p.CalendarSeq + 1)) == 1;

    private async Task<string> NextNumberAsync(DateTime now)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var year = now.Year;
            var cur = await db.BookingCounters.AsNoTracking().Where(c => c.Year == year).Select(c => (long?)c.Next).FirstOrDefaultAsync();
            if (cur is null)
            {
                db.BookingCounters.Add(new BookingCounter { Year = year, Next = 1 });
                try { await db.SaveChangesAsync(); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
                continue;
            }
            var n = cur.Value;
            if (await db.BookingCounters.Where(c => c.Year == year && c.Next == n).ExecuteUpdateAsync(s => s.SetProperty(c => c.Next, n + 1)) == 1) return $"BK-{year}-{n:D6}";
        }
        throw new InvalidOperationException("Could not allocate a booking number");
    }

    public static ServiceTiming Timing(ServiceDetails d, int? duration = null) =>
        new(duration ?? d.DefaultDurationMinutes, d.BufferBeforeMinutes, d.BufferAfterMinutes, d.SlotGranularityMinutes, d.MinNoticeHours * 60, d.MaxAdvanceDays, d.TravelBufferMinutes);

    public static bool InServiceArea(ServiceDetails d, string? postal, double? lat, double? lng)
    {
        var area = BJson.Area(d);
        var p = postal?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(p) && area.PostalCodes is { } codes && codes.Any(c => c.EndsWith('*') ? p.StartsWith(c[..^1]) : c == p)) return true;
        if (area.Radius is { Distance: > 0, Lat: { } blat, Lng: { } blng } r && lat is { } la && lng is { } ln)
        {
            var km = HaversineKm(blat, blng, la, ln);
            return km <= (r.Unit == "mi" ? r.Distance.Value * 1.609344 : r.Distance.Value);
        }
        return false;
    }

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1); var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371.0088 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    // ---- service lookup + pricing -----------------------------------------------------------------------------------------
    public async Task<(Product? P, ServiceDetails? D)> ServiceAsync(Guid id, bool requireActive = true)
    {
        var p = await db.Products.AsNoTracking().Include(x => x.Service).FirstOrDefaultAsync(x => x.Id == id && x.Type == ListingType.Service);
        if (p?.Service is null || (requireActive && p.Status != ProductStatus.Active)) return (null, null);
        // suspended, rejected or closed sellers take no new bookings (same rule as order ingest)
        var st = await db.Sellers.AsNoTracking().Where(s => s.Id == p.SellerId).Select(s => (SellerStatus?)s.Status).FirstOrDefaultAsync();
        if (st is null || !AccountContextService.Usable(st.Value)) return (null, null);
        return (p, p.Service);
    }

    public record Plan(LocationKind Location, int Minutes, int BaseMinutes, long Price, long AddOnsCents, long Travel, string AddOnsJson, string? AddressJson, string? Postal, string IntakeJson);

    /// <summary>Validates the request against the service and prices it. <paramref name="quote"/> overrides price and duration (accepted quotes).</summary>
    public BRes PlanFor(ServiceDetails d, BookingIn r, bool final, QuoteRequest? quote = null)
    {
        var loc = ParseLocation(r.Location);
        if (loc is null) return BRes.Invalid("Choose online, customer or seller as the location.", "location");
        var offered = loc switch { LocationKind.Online => d.Online, LocationKind.Customer => d.AtCustomer, _ => d.AtSeller };
        if (!offered) return BRes.Invalid("This service isn't offered at that location.", "location_not_offered");
        string? addressJson = null; string? postal = r.Address?.PostalCode?.Trim();
        if (loc == LocationKind.Customer)
        {
            if (!InServiceArea(d, postal, r.Address?.Lat, r.Address?.Lng)) return BRes.Invalid("The address is outside the service area.", "outside_service_area");
            if (final && string.IsNullOrWhiteSpace(r.Address?.Line1)) return BRes.Invalid("The customer's address is required.", "address_required");
        }
        if (r.Address is { } ad && loc == LocationKind.Customer)
            addressJson = BJson.Ser(new { name = Cut(ad.Name, 200), line1 = Cut(ad.Line1, 200), line2 = Cut(ad.Line2, 200), city = Cut(ad.City, 100), region = Cut(ad.Region, 100), postalCode = Cut(postal, 16), country = Cut(ad.Country, 2), lat = ad.Lat, lng = ad.Lng });
        var baseMinutes = d.DefaultDurationMinutes; long price;
        switch (d.PricingModel)
        {
            case PricingModel.Hourly:
                baseMinutes = r.DurationMinutes ?? d.DefaultDurationMinutes;
                var min = d.MinMinutes ?? d.IncrementMinutes; var max = d.MaxMinutes ?? 1440;
                if (baseMinutes < min || baseMinutes > max || baseMinutes % d.IncrementMinutes != 0) return BRes.Invalid($"Choose a duration between {min} and {max} minutes in steps of {d.IncrementMinutes}.", "duration");
                price = Prorate(d.PriceCents ?? 0, baseMinutes);
                break;
            case PricingModel.Fixed: price = d.PriceCents ?? 0; break;
            default:
                if (quote is null || quote.QuotePriceCents is null) return BRes.Invalid("This service is priced by quote.", "quote_required");
                price = quote.QuotePriceCents.Value; baseMinutes = quote.QuoteDurationMinutes ?? d.DefaultDurationMinutes; break;
        }
        var chosen = new List<AddOn>();
        var all = BJson.AddOns(d);
        foreach (var id in (r.AddOnIds ?? []).Distinct())
        {
            var a = all.FirstOrDefault(x => x.Id == id);
            if (a is null) return BRes.Invalid($"Unknown add-on '{id}'.", "add_on");
            chosen.Add(a);
        }
        // intake answers
        var intake = BJson.Intake(d); var answers = new List<IntakeAnswer>();
        foreach (var q in intake)
        {
            var ans = r.IntakeAnswers?.FirstOrDefault(x => x.Id == q.Id)?.Answer?.Trim();
            if (string.IsNullOrEmpty(ans)) { if (final && q.Required) return BRes.Invalid($"Please answer: {q.Label}", "intake_required"); continue; }
            if (ans.Length > 1000) return BRes.Invalid($"The answer to '{q.Label}' is too long.", "intake_invalid");
            if (q.Type == "choice" && q.Options?.Contains(ans) != true) return BRes.Invalid($"Choose one of the options for '{q.Label}'.", "intake_invalid");
            if (q.Type == "yesno") { ans = ans.ToLowerInvariant(); if (ans is not ("yes" or "no")) return BRes.Invalid($"Answer yes or no: {q.Label}", "intake_invalid"); }
            answers.Add(new IntakeAnswer(q.Id, ans));
        }
        var extraMinutes = chosen.Sum(a => a.ExtraMinutes);
        var travel = loc == LocationKind.Customer ? d.TravelFeeCents : 0;
        return BRes.Success(new Plan(loc.Value, baseMinutes + extraMinutes, baseMinutes, price, chosen.Sum(a => a.PriceCents), travel, BJson.Ser(chosen), addressJson, Cut(postal, 16), BJson.Ser(answers)));
    }

    private static string? Cut(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > n ? s.Trim()[..n] : s.Trim();

    // ---- availability ------------------------------------------------------------------------------------------------------
    public record SlotOut(DateTime StartAt, DateTime EndAt, List<Guid> ProviderIds);

    public async Task<BRes> AvailabilityAsync(Guid serviceId, DateTime from, DateTime to, Guid? providerId, string? location, string? postal, double? lat, double? lng, int? minutes, List<string>? addOnIds)
    {
        var (p, d) = await ServiceAsync(serviceId);
        if (p is null || d is null) return BRes.Fail(404, "service_not_found", "Service not found.");
        from = BJson.Utc(from); to = BJson.Utc(to);
        if (to <= from) return BRes.Invalid("'to' must be after 'from'.", "range");
        if ((to - from).TotalDays > opt.Value.MaxAvailabilityDays) return BRes.Invalid($"Ask for at most {opt.Value.MaxAvailabilityDays} days at a time.", "range");
        var plan = PlanFor(d, new BookingIn(null, serviceId, providerId, null, minutes, location ?? (d.Online ? "online" : d.AtSeller ? "seller" : "customer"), addOnIds, null,
            new AddressIn(null, null, null, null, null, postal, null, lat, lng), null), final: false);
        if (!plan.Ok) return plan;
        var pl = (Plan)plan.Data!;
        var eligible = await cal.EligibleQuery(serviceId).Select(m => m.Id).ToListAsync();
        if (providerId is { } pid) eligible = eligible.Where(x => x == pid).ToList();
        var now = time.UtcNow();
        var byStart = new SortedDictionary<DateTime, List<Guid>>();
        foreach (var prov in eligible)
        {
            var c = await cal.LoadAsync(p.SellerId, prov, from.AddDays(-2), to.AddDays(2), now);
            foreach (var s in SlotEngine.Slots(c, Timing(d, pl.Minutes), now, from, to, pl.Minutes, pl.Location == LocationKind.Customer))
                (byStart.TryGetValue(s, out var l) ? l : byStart[s] = []).Add(prov);
        }
        var zones = new Dictionary<Guid, string>();
        foreach (var prov in eligible) zones[prov] = (await cal.ZoneAsync(p.SellerId, prov)).Id;
        return BRes.Success(new
        {
            serviceId, durationMinutes = pl.Minutes, priceCents = pl.Price + pl.AddOnsCents + pl.Travel, location = pl.Location.ToString().ToLowerInvariant(),
            providers = eligible.Select(e => new { providerId = e, timeZone = zones[e] }),
            slots = byStart.Select(kv => new SlotOut(kv.Key, kv.Key.AddMinutes(pl.Minutes), kv.Value))
        });
    }

    // ---- create (hold / direct) ----------------------------------------------------------------------------------------------
    private async Task<List<Guid>> OrderCandidatesAsync(ServiceDetails d, List<Guid> candidates, DateTime now)
    {
        if (candidates.Count <= 1) return candidates;
        if (d.AssignmentRule == AssignmentRule.RoundRobin)
        {
            var sorted = candidates.OrderBy(x => x).ToList();
            var start = d.RoundRobinCursor % sorted.Count;
            return sorted.Skip(start).Concat(sorted.Take(start)).ToList();
        }
        var horizon = now.AddDays(30);
        var counts = await db.Bookings.AsNoTracking().Where(b => candidates.Contains(b.ProviderId) && b.StartUtc >= now && b.StartUtc < horizon && CalendarService.BlockingStatuses.Contains(b.Status))
            .GroupBy(b => b.ProviderId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        return candidates.OrderBy(c => counts.FirstOrDefault(x => x.Key == c)?.N ?? 0).ThenBy(c => c).ToList();
    }

    /// <summary>Creates a hold (hold:true) or a booking in one step. Idempotent on the marketplace booking id.</summary>
    public async Task<BRes> CreateAsync(BookingIn r, bool hold, QuoteRequest? quote = null)
    {
        if (r.MarketplaceBookingId is not { } mid || !IdRe().IsMatch(mid)) return BRes.Invalid("marketplaceBookingId is required (1-64 characters: letters, digits, _ . : -).", "marketplace_id");
        var existing = await db.Bookings.FirstOrDefaultAsync(b => b.MarketplaceBookingId == mid);
        if (existing is not null)
        {
            if (hold || existing.Status != BookingStatus.Hold) return Existing(existing);
            return await ConfirmHoldAsync(mid, r);
        }
        if (r.ServiceId is not { } sid || r.Start is not { } startRaw) return BRes.Invalid("serviceId and start are required.", "invalid");
        var (p, d) = await ServiceAsync(sid);
        if (p is null || d is null) return BRes.Fail(404, "service_not_found", "Service not found.");
        if (!hold && !BuyerOk(r.Buyer)) return BRes.Invalid("buyer.displayName is required.", "buyer_required");
        var plan = PlanFor(d, r, final: !hold, quote);
        if (!plan.Ok) return plan;
        var pl = (Plan)plan.Data!;
        var start = BJson.Utc(startRaw);
        var now = time.UtcNow();
        var eligible = await cal.EligibleQuery(sid).Select(m => m.Id).ToListAsync();
        List<Guid> candidates;
        if (quote?.QuoteProviderId is { } qp) candidates = eligible.Contains(qp) ? [qp] : [];
        else if (r.ProviderId is { } pid) candidates = eligible.Contains(pid) ? [pid] : [];
        else if (d.AssignmentRule == AssignmentRule.BuyerChoice && eligible.Count > 1) return BRes.Invalid("Choose a provider.", "provider_required");
        else candidates = eligible;
        if (candidates.Count == 0) return BRes.Fail(409, "no_provider", "No provider is available for this service.");

        foreach (var prov in await OrderCandidatesAsync(d, candidates, now))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (!await ClaimCalendarAsync(prov)) { await tx.RollbackAsync(); continue; }
            if (!await SlotFreeAsync(p.SellerId, prov, d, start, pl, null, quote is not null, now)) { await tx.RollbackAsync(); continue; }
            var status = hold ? BookingStatus.Hold : quote is not null || d.BookingMode == BookingMode.Instant ? BookingStatus.Confirmed : BookingStatus.Requested;
            var b = Build(p, d, r, pl, prov, start, now, status);
            b.Number = await NextNumberAsync(now);
            if (quote is not null) b.QuoteId = quote.Id;
            if (quote is not null)
            {
                // close the quote INSIDE the booking transaction: only one accept can move it Quoted -> Accepted
                var closed = await db.QuoteRequests.Where(q => q.Id == quote.Id && q.Status == QuoteStatus.Quoted)
                    .ExecuteUpdateAsync(u => u.SetProperty(q => q.Status, QuoteStatus.Accepted).SetProperty(q => q.BookingId, b.Id).SetProperty(q => q.UpdatedAt, now).SetProperty(q => q.Version, q => q.Version + 1));
                if (closed == 0) { await tx.RollbackAsync(); db.ChangeTracker.Clear(); return BRes.Conflict("This quote was already accepted or is no longer open.", "quote_not_open"); }
            }
            db.Bookings.Add(b);
            Event(b, hold ? "booking.hold" : status == BookingStatus.Requested ? "booking.requested" : "booking.confirmed", Actor.Buyer, hold ? $"Slot held until {b.HoldExpiresAt:u}" : null);
            if (!hold) Emit(b, d, status);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(); db.ChangeTracker.Clear();
                var winner = await db.Bookings.FirstOrDefaultAsync(x => x.MarketplaceBookingId == mid);
                if (winner is not null) return Existing(winner);
                throw;
            }
            await tx.CommitAsync();
            if (d.AssignmentRule == AssignmentRule.RoundRobin) await db.ServiceDetails.Where(x => x.ProductId == sid).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoundRobinCursor, x => x.RoundRobinCursor + 1));
            return new BRes(true, 201, "created", "", new { created = true, booking = Summary(b, d.OnlineInstructions) });
        }
        db.ChangeTracker.Clear();
        return BRes.Fail(409, "slot_unavailable", "That time is no longer available.");
    }

    private static bool BuyerOk(BuyerIn? b) => !string.IsNullOrWhiteSpace(b?.DisplayName);

    private BRes Existing(Booking b) => new(true, 200, "exists", "", new { created = false, booking = Summary(b, null) });

    private Booking Build(Product p, ServiceDetails d, BookingIn r, Plan pl, Guid provider, DateTime start, DateTime now, BookingStatus status)
    {
        var timing = Timing(d, pl.Minutes);
        var block = SlotEngine.Footprint(timing, start, pl.Minutes, pl.Location == LocationKind.Customer, out var svc)[0];
        return new Booking
        {
            SellerId = p.SellerId, BrandId = p.BrandId, ProductId = p.Id, ProviderId = provider, MarketplaceBookingId = r.MarketplaceBookingId!, Status = status, Location = pl.Location,
            StartUtc = svc.StartUtc, EndUtc = svc.EndUtc, BlockStartUtc = block.StartUtc, BlockEndUtc = block.EndUtc, DurationMinutes = pl.Minutes,
            HoldExpiresAt = status == BookingStatus.Hold ? now.AddMinutes(opt.Value.HoldMinutes) : null,
            ApprovalDeadline = status == BookingStatus.Requested ? now.AddHours(d.ApprovalHours) : null,
            BuyerName = Cut(r.Buyer?.DisplayName, 200) ?? "Buyer", BuyerEmail = Cut(r.Buyer?.RelayEmail, 254), BuyerPhone = Cut(r.Buyer?.Phone, 40),
            AddressJson = pl.AddressJson, PostalCode = pl.Postal, IntakeAnswersJson = pl.IntakeJson, AddOnsJson = pl.AddOnsJson, ServiceTitle = p.Title, PolicyJson = BJson.Ser(PolicyOf(d)),
            PricingModel = d.PricingModel, RateCents = d.PricingModel == PricingModel.Hourly ? d.PriceCents ?? 0 : 0,
            PriceCents = pl.Price, AddOnsCents = pl.AddOnsCents, TravelFeeCents = pl.Travel, TotalCents = pl.Price + pl.AddOnsCents + pl.Travel,
            ConfirmedAt = status == BookingStatus.Confirmed ? now : null, CreatedAt = now, UpdatedAt = now
        };
    }

    private void Emit(Booking b, ServiceDetails d, BookingStatus status)
    {
        if (status == BookingStatus.Requested) outbox.Enqueue("booking.requested", b.SellerId, b.Id.ToString(), Data(b, new { approvalDeadline = b.ApprovalDeadline }));
        else outbox.Enqueue("booking.confirmed", b.SellerId, b.Id.ToString(), Data(b, new { onlineInstructions = b.Location == LocationKind.Online ? d.OnlineInstructions : null, auto = true }));
    }

    /// <summary>Checks (inside the provider's calendar lock) that the slot is on the grid and free.</summary>
    private async Task<bool> SlotFreeAsync(Guid sellerId, Guid provider, ServiceDetails d, DateTime start, Plan pl, Guid? excludeBooking, bool ignoreNotice, DateTime now)
    {
        var c = await cal.LoadAsync(sellerId, provider, start.AddDays(-2), start.AddMinutes(pl.Minutes).AddDays(2), now, excludeBooking);
        var timing = Timing(d, pl.Minutes);
        var atCustomer = pl.Location == LocationKind.Customer;
        if (!SlotEngine.IsBookable(c, timing, now, start, pl.Minutes, atCustomer, ignoreNoticeAndWindow: ignoreNotice)) return false;
        // the start must lie on the slot grid of its working interval
        return SlotEngine.Slots(c, ignoreNotice ? timing with { MinNoticeMinutes = 0, MaxAdvanceDays = 3650 } : timing, now, start, start.AddSeconds(1), pl.Minutes, atCustomer).Contains(start);
    }

    // ---- confirm a hold ------------------------------------------------------------------------------------------------------
    public async Task<BRes> ConfirmHoldAsync(string mid, BookingIn r)
    {
        var b = await db.Bookings.FirstOrDefaultAsync(x => x.MarketplaceBookingId == mid);
        if (b is null) return BRes.Fail(404, "booking_not_found", "Booking not found.");
        if (b.Status is BookingStatus.Confirmed or BookingStatus.Requested or BookingStatus.Rescheduled) return Existing(b);
        if (b.Status is not (BookingStatus.Hold or BookingStatus.Expired)) return BRes.Conflict($"A {b.Status} booking can't be confirmed.");
        if (!BuyerOk(r.Buyer)) return BRes.Invalid("buyer.displayName is required.", "buyer_required");
        var (p, d) = await ServiceAsync(b.ProductId);
        if (p is null || d is null) return BRes.Fail(409, "service_unavailable", "The service is no longer available.");
        var plan = PlanFor(d, r with { Location = b.Location.ToString(), DurationMinutes = null }, final: true, quote: null);
        // keep the held duration/price: only intake, add-ons check and address come from the confirm request
        if (!plan.Ok && d.PricingModel != PricingModel.Quote) return plan;
        var pl = plan.Data as Plan;
        await using var tx = await db.Database.BeginTransactionAsync();
        if (!await ClaimCalendarAsync(b.ProviderId)) { await tx.RollbackAsync(); return BRes.Fail(409, "slot_unavailable", "That time is no longer available."); }
        // everything below is decided INSIDE the provider lock: re-read the booking (a parallel confirm may have won) and take the time now
        await db.Entry(b).ReloadAsync();
        if (b.Status is BookingStatus.Confirmed or BookingStatus.Requested or BookingStatus.Rescheduled) { await tx.RollbackAsync(); return Existing(b); }
        if (b.Status is not (BookingStatus.Hold or BookingStatus.Expired)) { await tx.RollbackAsync(); return BRes.Conflict($"A {b.Status} booking can't be confirmed."); }
        var now = time.UtcNow();
        var wasLive = b.Status == BookingStatus.Hold && b.HoldExpiresAt > now;
        if (!wasLive)
        {
            var held = new Plan(b.Location, b.DurationMinutes, b.DurationMinutes, b.PriceCents, b.AddOnsCents, b.TravelFeeCents, b.AddOnsJson, b.AddressJson, b.PostalCode, b.IntakeAnswersJson);
            if (!await SlotFreeAsync(b.SellerId, b.ProviderId, d, BJson.Utc(b.StartUtc), held, b.Id, false, now)) { await tx.RollbackAsync(); db.ChangeTracker.Clear(); return BRes.Fail(409, "slot_unavailable", "The hold expired and the time was taken."); }
        }
        if (pl is not null)
        {
            b.AddressJson = pl.AddressJson ?? b.AddressJson; b.PostalCode = pl.Postal ?? b.PostalCode; b.IntakeAnswersJson = pl.IntakeJson;
        }
        b.BuyerName = Cut(r.Buyer?.DisplayName, 200) ?? b.BuyerName; b.BuyerEmail = Cut(r.Buyer?.RelayEmail, 254); b.BuyerPhone = Cut(r.Buyer?.Phone, 40);
        var status = d.BookingMode == BookingMode.Instant ? BookingStatus.Confirmed : BookingStatus.Requested;
        b.Status = status; b.HoldExpiresAt = null; b.ApprovalDeadline = status == BookingStatus.Requested ? now.AddHours(d.ApprovalHours) : null;
        if (status == BookingStatus.Confirmed) b.ConfirmedAt ??= now;
        b.Version++; b.UpdatedAt = now;
        Event(b, status == BookingStatus.Requested ? "booking.requested" : "booking.confirmed", Actor.Buyer);
        Emit(b, d, status);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(); db.ChangeTracker.Clear();
            // a parallel confirm of the same hold got there first: answer idempotently with what it created
            var cur = await db.Bookings.FirstOrDefaultAsync(x => x.MarketplaceBookingId == mid);
            if (cur is not null && cur.Status is BookingStatus.Confirmed or BookingStatus.Requested or BookingStatus.Rescheduled) return Existing(cur!);
            return BRes.Conflict("The booking changed at the same time. Try again.", "version_conflict");
        }
        await tx.CommitAsync();
        return new BRes(true, 200, "confirmed", "", new { created = false, booking = Summary(b, d.OnlineInstructions) });
    }

    // ---- lifecycle actions ------------------------------------------------------------------------------------------------------
    private BRes Illegal(Booking b, BookingStateMachine.Act a) => BRes.Conflict($"A {b.Status} booking can't do that ({a}).");

    private async Task<BRes> SaveAsync(Booking b, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null)
    {
        b.Version++; b.UpdatedAt = time.UtcNow();
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { if (tx is not null) await tx.RollbackAsync(); db.ChangeTracker.Clear(); return BRes.Conflict("This booking was changed somewhere else. Reload and try again.", "version_conflict"); }
        if (tx is not null) await tx.CommitAsync();
        return BRes.Success(Summary(b, await InstructionsAsync(b)));
    }

    private static BRes? CheckVersion(Booking b, int? version) => version is { } v && v != b.Version ? BRes.Conflict("This booking was changed somewhere else. Reload and try again.", "version_conflict") : null;

    public async Task<BRes> ApproveAsync(Booking b, Actor actor, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Approve)) return Illegal(b, BookingStateMachine.Act.Approve);
        if (b.ApprovalDeadline is { } dl && dl <= time.UtcNow()) return BRes.Conflict("The approval time has passed; the request is being declined.", "request_expired");
        b.Status = BookingStatus.Confirmed; b.ConfirmedAt ??= time.UtcNow(); b.ApprovalDeadline = null; b.ProposedStartUtc = null;
        Event(b, "booking.approved", actor);
        outbox.Enqueue("booking.confirmed", b.SellerId, b.Id.ToString(), Data(b, new { onlineInstructions = await InstructionsAsync(b), auto = false }));
        return await SaveAsync(b);
    }

    public async Task<BRes> DeclineAsync(Booking b, Actor actor, string? reason, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Decline)) return Illegal(b, BookingStateMachine.Act.Decline);
        b.Status = BookingStatus.Declined; b.DeclineReason = Cut(reason, 500); b.ApprovalDeadline = null; b.ProposedStartUtc = null;
        Event(b, "booking.declined", actor, b.DeclineReason);
        outbox.Enqueue("booking.declined", b.SellerId, b.Id.ToString(), Data(b, new { reason = b.DeclineReason, auto = false }));
        return await SaveAsync(b);
    }

    public async Task<BRes> AutoDeclineAsync(Booking b)
    {
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.AutoDecline)) return Illegal(b, BookingStateMachine.Act.AutoDecline);
        b.Status = BookingStatus.Declined; b.DeclineReason = "The seller didn't respond in time."; b.ApprovalDeadline = null; b.ProposedStartUtc = null;
        Event(b, "booking.auto_declined", Actor.System, b.DeclineReason);
        outbox.Enqueue("booking.declined", b.SellerId, b.Id.ToString(), Data(b, new { reason = "no_response", auto = true }));
        return await SaveAsync(b);
    }

    public async Task<BRes> ExpireHoldAsync(Booking b)
    {
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.ExpireHold)) return Illegal(b, BookingStateMachine.Act.ExpireHold);
        b.Status = BookingStatus.Expired;
        Event(b, "booking.hold_expired", Actor.System);
        outbox.Enqueue("booking.hold_expired", b.SellerId, b.Id.ToString(), Data(b));
        return await SaveAsync(b);
    }

    private async Task<(ServiceDetails? D, Plan? Pl)> Reload(Booking b)
    {
        var d = await db.ServiceDetails.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == b.ProductId);
        if (d is null) return (null, null);
        return (d, new Plan(b.Location, b.DurationMinutes, b.DurationMinutes, b.PriceCents, b.AddOnsCents, b.TravelFeeCents, b.AddOnsJson, b.AddressJson, b.PostalCode, b.IntakeAnswersJson));
    }

    private void Move(Booking b, ServiceDetails d, DateTime start)
    {
        var block = SlotEngine.Footprint(Timing(d, b.DurationMinutes), start, b.DurationMinutes, b.Location == LocationKind.Customer, out var svc)[0];
        b.StartUtc = svc.StartUtc; b.EndUtc = svc.EndUtc; b.BlockStartUtc = block.StartUtc; b.BlockEndUtc = block.EndUtc;
    }

    /// <summary>Moves a booking to <paramref name="newStart"/> if the provider is free (inside the calendar lock).</summary>
    private async Task<(BRes? Err, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? Tx, ServiceDetails? D)> MoveLockedAsync(Booking b, Guid provider, DateTime newStart, bool ignoreNotice)
    {
        var (d, pl) = await Reload(b);
        if (d is null) return (BRes.Fail(409, "service_unavailable", "The service no longer exists."), null, null);
        var now = time.UtcNow();
        var tx = await db.Database.BeginTransactionAsync();
        if (!await ClaimCalendarAsync(provider) || !await SlotFreeAsync(b.SellerId, provider, d, newStart, pl!, b.Id, ignoreNotice, now))
        { await tx.RollbackAsync(); return (BRes.Fail(409, "slot_unavailable", "That time isn't available."), null, null); }
        return (null, tx, d);
    }

    public async Task<BRes> ProposeAsync(Booking b, Actor actor, DateTime newStart, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Propose)) return Illegal(b, BookingStateMachine.Act.Propose);
        newStart = BJson.Utc(newStart);
        var (err, tx, _) = await MoveLockedAsync(b, b.ProviderId, newStart, false);
        if (err is not null) return err;
        await tx!.RollbackAsync(); // proposals do not reserve the slot; it is re-checked when the buyer accepts
        b.ProposedStartUtc = newStart;
        Event(b, "booking.time_proposed", actor, $"{newStart:u}");
        outbox.Enqueue("booking.time_proposed", b.SellerId, b.Id.ToString(), Data(b, new { proposedStartAt = newStart }));
        return await SaveAsync(b);
    }

    public async Task<BRes> AcceptProposalAsync(Booking b, bool accept)
    {
        if (b.Status != BookingStatus.Requested || b.ProposedStartUtc is null) return BRes.Conflict("There is no proposed time to answer.");
        if (!accept) return await CancelAsync(b, Actor.Buyer, "declined_proposal");
        var proposed = BJson.Utc(b.ProposedStartUtc.Value);
        var (err, tx, d) = await MoveLockedAsync(b, b.ProviderId, proposed, true);
        if (err is not null) return err;
        Move(b, d!, proposed); b.Status = BookingStatus.Confirmed; b.ConfirmedAt ??= time.UtcNow(); b.ProposedStartUtc = null; b.ApprovalDeadline = null;
        Event(b, "booking.proposal_accepted", Actor.Buyer);
        outbox.Enqueue("booking.confirmed", b.SellerId, b.Id.ToString(), Data(b, new { onlineInstructions = await InstructionsAsync(b), auto = false }));
        return await SaveAsync(b, tx);
    }

    /// <summary>Seller moves a confirmed booking. The buyer is told (consent event); the buyer may cancel without a fee in reply.</summary>
    public async Task<BRes> RescheduleAsync(Booking b, Actor actor, DateTime newStart, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Reschedule)) return Illegal(b, BookingStateMachine.Act.Reschedule);
        newStart = BJson.Utc(newStart);
        if (newStart <= time.UtcNow()) return BRes.Invalid("Choose a time in the future.", "past");
        var previous = BJson.Utc(b.StartUtc);
        var (err, tx, d) = await MoveLockedAsync(b, b.ProviderId, newStart, true);
        if (err is not null) return err;
        Move(b, d!, newStart); b.Status = BookingStatus.Rescheduled; b.RescheduleRequestedStartUtc = null; b.RescheduleRequestId = null;
        Event(b, "booking.rescheduled", actor, $"{previous:u} -> {newStart:u}");
        outbox.Enqueue("booking.rescheduled", b.SellerId, b.Id.ToString(), Data(b, new { previousStartAt = previous, initiatedBy = "seller", requiresBuyerConsent = true }));
        return await SaveAsync(b, tx);
    }

    /// <summary>Buyer asks for another time: applied at once for instant-booking services, otherwise it waits for the seller.</summary>
    public async Task<BRes> BuyerRescheduleAsync(Booking b, string? requestId, DateTime newStart)
    {
        var policy = BJson.From<BookingPolicy>(b.PolicyJson) ?? new BookingPolicy(24, 0, 2, 100, 60, 24);
        if (requestId is not null && b.RescheduleRequestId == requestId) return BRes.Success(Summary(b));
        if (b.Status is not (BookingStatus.Confirmed or BookingStatus.Rescheduled)) return Illegal(b, BookingStateMachine.Act.Reschedule);
        if (b.RescheduleCount >= policy.MaxReschedules) return BRes.Conflict("This booking can't be rescheduled again.", "reschedule_limit");
        if ((BJson.Utc(b.StartUtc) - time.UtcNow()).TotalHours < policy.CancelFreeHours) return BRes.Conflict($"Bookings can only be rescheduled up to {policy.CancelFreeHours} hours before they start.", "too_late");
        newStart = BJson.Utc(newStart);
        var d = await db.ServiceDetails.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == b.ProductId);
        if (d is null) return BRes.Fail(409, "service_unavailable", "The service no longer exists.");
        if (d.BookingMode == BookingMode.Request)
        {
            var (e0, tx0, _) = await MoveLockedAsync(b, b.ProviderId, newStart, false);
            if (e0 is not null) return e0;
            await tx0!.RollbackAsync();
            b.RescheduleRequestedStartUtc = newStart; b.RescheduleRequestId = requestId;
            Event(b, "booking.reschedule_requested", Actor.Buyer, $"{newStart:u}");
            outbox.Enqueue("booking.reschedule_requested", b.SellerId, b.Id.ToString(), Data(b, new { requestedStartAt = newStart, requestId }));
            return await SaveAsync(b);
        }
        var previous = BJson.Utc(b.StartUtc);
        var (err, tx, dd) = await MoveLockedAsync(b, b.ProviderId, newStart, false);
        if (err is not null) return err;
        Move(b, dd!, newStart); b.Status = BookingStatus.Rescheduled; b.RescheduleCount++; b.RescheduleRequestId = requestId;
        Event(b, "booking.rescheduled", Actor.Buyer, $"{previous:u} -> {newStart:u}");
        outbox.Enqueue("booking.rescheduled", b.SellerId, b.Id.ToString(), Data(b, new { previousStartAt = previous, initiatedBy = "buyer", requiresBuyerConsent = false }));
        return await SaveAsync(b, tx);
    }

    public async Task<BRes> DecideRescheduleAsync(Booking b, Actor actor, bool approve, string? note, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Reschedule)) return Illegal(b, BookingStateMachine.Act.Reschedule);
        if (b.RescheduleRequestedStartUtc is not { } req) return BRes.Conflict("There is no reschedule request to answer.");
        if (!approve)
        {
            b.RescheduleRequestedStartUtc = null;
            Event(b, "booking.reschedule_declined", actor, note);
            outbox.Enqueue("booking.reschedule_decided", b.SellerId, b.Id.ToString(), Data(b, new { requestId = b.RescheduleRequestId, decision = "declined", note = Cut(note, 500) }));
            return await SaveAsync(b);
        }
        var previous = BJson.Utc(b.StartUtc);
        var (err, tx, d) = await MoveLockedAsync(b, b.ProviderId, BJson.Utc(req), true);
        if (err is not null) return err;
        Move(b, d!, BJson.Utc(req)); b.Status = BookingStatus.Rescheduled; b.RescheduleCount++; b.RescheduleRequestedStartUtc = null;
        Event(b, "booking.rescheduled", actor, $"{previous:u} -> {req:u}");
        outbox.Enqueue("booking.reschedule_decided", b.SellerId, b.Id.ToString(), Data(b, new { requestId = b.RescheduleRequestId, decision = "approved", previousStartAt = previous }));
        return await SaveAsync(b, tx);
    }

    public async Task<BRes> CancelAsync(Booking b, Actor actor, string? reason, int? version = null)
    {
        var byBuyer = actor.Kind != "seller";
        var act = byBuyer ? BookingStateMachine.Act.CancelByBuyer : BookingStateMachine.Act.CancelBySeller;
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, act)) return Illegal(b, act);
        var policy = BJson.From<BookingPolicy>(b.PolicyJson) ?? new BookingPolicy(24, 0, 2, 100, 60, 24);
        long fee = 0;
        // only a buyer cancelling a confirmed booking inside the free window pays; a pending request or a seller cancellation is always free
        if (byBuyer && b.Status is BookingStatus.Confirmed or BookingStatus.Rescheduled && (BJson.Utc(b.StartUtc) - time.UtcNow()).TotalHours < policy.CancelFreeHours)
            fee = (long)Math.Round(b.TotalCents * (decimal)policy.CancelFeePercent / 100m, 0, MidpointRounding.AwayFromZero);
        var wasHold = b.Status == BookingStatus.Hold;
        b.Status = byBuyer ? BookingStatus.CancelledByBuyer : BookingStatus.CancelledBySeller;
        b.CancelReason = Cut(reason, 500); b.CancellationFeeCents = fee; b.HoldExpiresAt = null; b.ApprovalDeadline = null; b.ProposedStartUtc = null; b.RescheduleRequestedStartUtc = null;
        Event(b, "booking.cancelled", actor, fee > 0 ? $"{b.CancelReason} (fee {fee} cents)" : b.CancelReason);
        if (!wasHold)
            outbox.Enqueue("booking.cancelled", b.SellerId, b.Id.ToString(), Data(b, new { cancelledBy = byBuyer ? "buyer" : "seller", reason = b.CancelReason, cancellationFeeCents = fee, refundDueCents = b.TotalCents - fee }));
        return await SaveAsync(b);
    }

    public async Task<BRes> StartAsync(Booking b, Actor actor, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Start)) return Illegal(b, BookingStateMachine.Act.Start);
        b.Status = BookingStatus.InProgress; b.StartedAt = time.UtcNow(); b.RescheduleRequestedStartUtc = null; b.RescheduleRequestId = null;
        Event(b, "booking.started", actor);
        outbox.Enqueue("booking.started", b.SellerId, b.Id.ToString(), Data(b, new { startedAt = b.StartedAt }));
        return await SaveAsync(b);
    }

    public async Task<BRes> CompleteAsync(Booking b, Actor actor, int? actualMinutes, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Complete)) return Illegal(b, BookingStateMachine.Act.Complete);
        var policy = BJson.From<BookingPolicy>(b.PolicyJson) ?? new BookingPolicy(24, 0, 2, 100, 60, 24);
        if (b.PricingModel == PricingModel.Hourly && actualMinutes is null) return BRes.Invalid("Enter the time actually worked (minutes).", "actual_minutes");
        if (actualMinutes is < 1 or > 1440) return BRes.Invalid("Enter between 1 and 1440 minutes.", "actual_minutes");
        long extraCents = 0; int billed = 0, extra = 0;
        if (b.PricingModel == PricingModel.Hourly && actualMinutes is { } am)
        {
            // the booked time INCLUDES the add-on minutes (already priced as add-ons): compare the work done with all of it
            var bookedBase = b.DurationMinutes;
            var inc = Math.Max(1, policy.IncrementMinutes);
            billed = (int)Math.Ceiling(am / (double)inc) * inc;
            extra = billed - bookedBase;
            if (extra > 0) extraCents = Prorate(b.RateCents, extra); else extra = 0;
        }
        b.Status = BookingStatus.Completed; b.CompletedAt = time.UtcNow(); b.RescheduleRequestedStartUtc = null; b.RescheduleRequestId = null; b.ActualMinutes = actualMinutes; b.AdditionalChargeCents = extraCents;
        Event(b, "booking.completed", actor, actualMinutes is null ? null : $"{actualMinutes} min worked");
        outbox.Enqueue("booking.completed", b.SellerId, b.Id.ToString(), Data(b, new { completedAt = b.CompletedAt, actualMinutes, billedMinutes = billed == 0 ? (int?)null : billed }));
        // the booked price is never changed here: extra time is a *request* the marketplace charges (or not)
        if (extraCents > 0)
            outbox.Enqueue("booking.additional_charge_requested", b.SellerId, b.Id.ToString(), Data(b, new { actualMinutes, billedMinutes = billed, extraMinutes = extra, rateCents = b.RateCents, amountCents = extraCents, currency = "USD" }));
        return await SaveAsync(b);
    }

    public async Task<BRes> NoShowAsync(Booking b, Actor actor, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.NoShow)) return Illegal(b, BookingStateMachine.Act.NoShow);
        if (BJson.Utc(b.StartUtc) > time.UtcNow()) return BRes.Conflict("A booking can only be marked no-show once it has started.", "too_early");
        var policy = BJson.From<BookingPolicy>(b.PolicyJson) ?? new BookingPolicy(24, 0, 2, 100, 60, 24);
        var fee = (long)Math.Round(b.TotalCents * (decimal)policy.NoShowFeePercent / 100m, 0, MidpointRounding.AwayFromZero);
        b.Status = BookingStatus.NoShow; b.CancellationFeeCents = fee; b.RescheduleRequestedStartUtc = null; b.RescheduleRequestId = null;
        Event(b, "booking.no_show", actor);
        outbox.Enqueue("booking.no_show", b.SellerId, b.Id.ToString(), Data(b, new { noShowFeeCents = fee, refundDueCents = b.TotalCents - fee }));
        return await SaveAsync(b);
    }

    public async Task<BRes> ReassignAsync(Booking b, Actor actor, Guid newProvider, int? version = null)
    {
        if (CheckVersion(b, version) is { } c) return c;
        if (!BookingStateMachine.Can(b.Status, BookingStateMachine.Act.Reassign)) return Illegal(b, BookingStateMachine.Act.Reassign);
        if (newProvider == b.ProviderId) return BRes.Invalid("The booking already has this provider.", "same_provider");
        if (!await cal.EligibleQuery(b.ProductId).AnyAsync(m => m.Id == newProvider)) return BRes.Invalid("Choose a provider who is eligible for this service.", "provider_not_eligible");
        var old = b.ProviderId;
        var (err, tx, _) = await MoveLockedAsync(b, newProvider, BJson.Utc(b.StartUtc), true);
        if (err is not null) return err.Code == "slot_unavailable" ? BRes.Conflict("The new provider isn't free at that time.", "provider_busy") : err;
        b.ProviderId = newProvider;
        Event(b, "booking.provider_changed", actor);
        outbox.Enqueue("booking.provider_changed", b.SellerId, b.Id.ToString(), Data(b, new { previousProviderId = old }));
        return await SaveAsync(b, tx);
    }
}
