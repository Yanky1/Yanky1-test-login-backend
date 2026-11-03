using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Tests;

public class BookingFlowTests : IClassFixture<SvcFactory>
{
    private readonly SvcFactory _f;
    public BookingFlowTests(SvcFactory f) => _f = f;

    private async Task RunJobs() => Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().PostAsync("/api/dev/bookings/maintenance/run", null)).StatusCode);

    // ---- signed inbound API -------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Availability_lists_slots_with_provider_zone_price_and_duration()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var s = await Svc.Slots(b, Svc.Local(b.Tz, wed, 0), Svc.Local(b.Tz, wed.AddDays(1), 0));
        var slots = s["slots"]!.AsArray();
        Assert.Equal(7, slots.Count);        // 120 minute service, hourly grid in 9-17: starts 9..15
        Assert.Equal(Svc.Local(b.Tz, wed, 9), (DateTime)slots[0]!["startAt"]!);
        Assert.Equal(Svc.Local(b.Tz, wed, 11), (DateTime)slots[0]!["endAt"]!);
        Assert.Equal(12000 + 1000, (int)s["priceCents"]!);      // 2 h x $60 + $10 travel
        Assert.Equal(Svc.Ny, (string)s["providers"]![0]!["timeZone"]!);
        Assert.Equal(b.OwnerMember.ToString(), (string)slots[0]!["providerIds"]![0]!);
        // an address outside the service area, and a location the service doesn't offer
        var outside = await _f.NewClient().SendAsync(Orders.Signed(_f, "GET", $"/api/internal/v1/services/{b.Service}/availability?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}&location=customer&postalCode=99999", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outside.StatusCode);
        var longer = await Svc.Slots(b, Svc.Local(b.Tz, wed, 0), Svc.Local(b.Tz, wed.AddDays(1), 0), "&location=online&minutes=240");
        Assert.Equal(5, longer["slots"]!.AsArray().Count);
        var bad = await _f.NewClient().SendAsync(Orders.Signed(_f, "GET", $"/api/internal/v1/services/{b.Service}/availability?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}&location=online&minutes=45", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.NewClient().SendAsync(Orders.Signed(_f, "GET", $"/api/internal/v1/services/{Guid.NewGuid()}/availability?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}", null))).StatusCode);
    }

    [Fact]
    public async Task Inactive_services_are_not_bookable()
    {
        var b = await Svc.NewBiz(_f, activate: false);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var r = await _f.NewClient().SendAsync(Orders.Signed(_f, "GET", $"/api/internal/v1/services/{b.Service}/availability?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}&location=online", null));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var book = await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 10)));
        Assert.Equal(HttpStatusCode.NotFound, book.StatusCode);
    }

    [Fact]
    public async Task Inbound_requests_must_be_signed_fresh_and_unreplayed()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var body = Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 10));
        var c = _f.NewClient();
        var unsigned = await c.SendAsync(Orders.Signed(_f, "POST", "/api/internal/v1/bookings", body, sign: false));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode); Assert.Equal("unsigned", await unsigned.ErrCode());
        var wrong = await c.SendAsync(Orders.Signed(_f, "POST", "/api/internal/v1/bookings", body, secret: "not-the-secret"));
        Assert.Equal("bad_signature", await wrong.ErrCode());
        var stale = await c.SendAsync(Orders.Signed(_f, "POST", "/api/internal/v1/bookings", body, ts: _f.Time.GetUtcNow().ToUnixTimeSeconds() - 3600));
        Assert.Equal("stale", await stale.ErrCode());
        var nonce = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(Orders.Signed(_f, "POST", "/api/internal/v1/bookings", body, nonce))).StatusCode);
        var replay = await c.SendAsync(Orders.Signed(_f, "POST", "/api/internal/v1/bookings", body, nonce));
        Assert.Equal("replayed", await replay.ErrCode());
        // a seller bearer token opens nothing here
        var seller = await b.Owner.Post("/api/internal/v1/bookings", body);
        Assert.Equal(HttpStatusCode.Unauthorized, seller.StatusCode);
        // availability is signed too
        var avail = await c.GetAsync($"/api/internal/v1/services/{b.Service}/availability?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}");
        Assert.Equal(HttpStatusCode.Unauthorized, avail.StatusCode);
    }

    [Fact]
    public async Task Creating_a_booking_is_idempotent_on_the_marketplace_id()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday); var start = Svc.Local(b.Tz, wed, 10); var mid = Svc.BookingId();
        var first = await Svc.Post(b, "/bookings", Svc.Body(b, mid, start));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var again = await Svc.Post(b, "/bookings", Svc.Body(b, mid, start));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((bool)(await Orders.Node(again))["created"]!);
        Assert.Equal(1, _f.WithDb(db => db.Bookings.Count(x => x.MarketplaceBookingId == mid)));
        Assert.Single(Svc.Events(_f, b.Seller, "booking.confirmed"));
        var s = await Orders.Node(first);
        Assert.Equal("Confirmed", (string)s["booking"]!["status"]!);
        Assert.StartsWith("BK-", (string)s["booking"]!["number"]!);
        Assert.Equal(13000, (int)s["booking"]!["totalCents"]!);
        // the same slot under another marketplace id is refused
        var other = await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode); Assert.Equal("slot_unavailable", await other.ErrCode());
    }

    [Fact]
    public async Task Parallel_requests_for_the_same_slot_produce_exactly_one_booking()
    {
        var b = await Svc.NewBiz(_f);
        var start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 11);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start))).StatusCode)));
        Assert.Equal(1, results.Count(r => r == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(r => r == HttpStatusCode.Conflict));
        Assert.Equal(1, _f.WithDb(db => db.Bookings.Count(x => x.ProviderId == b.OwnerMember && x.StartUtc == start)));
    }

    [Fact]
    public async Task Overlapping_slots_with_a_different_start_also_conflict_and_back_to_back_works()
    {
        var b = await Svc.NewBiz(_f, d => d["slotGranularityMinutes"] = 30);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 10)))).StatusCode);     // 10-12
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 11)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 9, 30)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 13)))).StatusCode);   // 12:00 would still collide with the 30 min travel buffer after the 10-12 job
    }

    [Fact]
    public async Task Starts_outside_hours_off_the_grid_or_inside_the_notice_period_are_refused()
    {
        var b = await Svc.NewBiz(_f, d => d["minNoticeHours"] = 72);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday, 1);
        var farWed = Svc.Next(_f, DayOfWeek.Wednesday, 10);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, farWed, 20)))).StatusCode);      // after hours
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, farWed, 10, 15)))).StatusCode);  // off the hourly grid
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Saturday, 10), 10)))).StatusCode);   // closed day
        var soon = _f.Time.GetUtcNow().UtcDateTime.AddHours(30);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), new DateTime(soon.Year, soon.Month, soon.Day, soon.Hour, 0, 0, DateTimeKind.Utc)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, farWed, 10)))).StatusCode);
        _ = wed;
    }

    [Fact]
    public async Task Request_validation_covers_area_location_addons_intake_and_buyer()
    {
        var b = await Svc.NewBiz(_f, d => d["intake"] = new JsonArray(new JsonObject { ["id"] = "pets", ["label"] = "Any pets?", ["type"] = "yesno", ["required"] = true }));
        var start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 10);
        async Task<(HttpStatusCode, string?)> Try(object body) { var r = await Svc.Post(b, "/bookings", body); return (r.StatusCode, await r.ErrCode()); }
        var ok = new[] { new { id = "pets", answer = "no" } };
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "outside_service_area"), await Try(Svc.Body(b, Svc.BookingId(), start, postal: "99999", answers: ok)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "intake_required"), await Try(Svc.Body(b, Svc.BookingId(), start)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "intake_invalid"), await Try(Svc.Body(b, Svc.BookingId(), start, answers: new[] { new { id = "pets", answer = "maybe" } })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "add_on"), await Try(Svc.Body(b, Svc.BookingId(), start, addOns: ["ghost"], answers: ok)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "location"), await Try(Svc.Body(b, Svc.BookingId(), start, location: "moon", answers: ok)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "location_not_offered"), await Try(Svc.Body(b, Svc.BookingId(), start, location: "seller", answers: ok)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "marketplace_id"), await Try(Svc.Body(b, "bad id!", start, answers: ok)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "duration"), await Try(Svc.Body(b, Svc.BookingId(), start, minutes: 45, answers: ok)));
        var good = await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start, addOns: ["deep"], answers: ok));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.Equal(12000 + 2500 + 1000, (int)(await Orders.Node(good))["booking"]!["totalCents"]!);
        Assert.Equal(150, (int)(await Orders.Node(good))["booking"]!["durationMinutes"]!);     // add-on adds 30 minutes
        // by-the-prefix postal code (112*) is accepted
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday, 10), 10), postal: "11299", answers: ok))).StatusCode);
    }

    // ---- holds -----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_hold_reserves_the_slot_until_it_expires_and_expiry_frees_it()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday); var start = Svc.Local(b.Tz, wed, 10); var mid = Svc.BookingId();
        var hold = await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start));
        Assert.Equal(HttpStatusCode.Created, hold.StatusCode);
        var h = await Orders.Node(hold);
        Assert.Equal("Hold", (string)h["booking"]!["status"]!);
        Assert.NotNull(h["booking"]!["holdExpiresAt"]);
        Assert.Null(h["booking"]!["onlineInstructions"]);
        // the slot disappears and nobody else can take it
        var during = await Svc.Slots(b, Svc.Local(b.Tz, wed, 0), Svc.Local(b.Tz, wed.AddDays(1), 0));
        Assert.DoesNotContain(during["slots"]!.AsArray(), s => (DateTime)s!["startAt"]! == start);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start))).StatusCode);
        // a hold never shows up in the seller's lists
        Assert.Equal(0, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings")))["total"]!);
        // after 11 minutes the slot is free again, even before the job marks the hold expired
        _f.Time.Advance(TimeSpan.FromMinutes(11));
        var after = await Svc.Slots(b, Svc.Local(b.Tz, wed, 0), Svc.Local(b.Tz, wed.AddDays(1), 0));
        Assert.Contains(after["slots"]!.AsArray(), s => (DateTime)s!["startAt"]! == start);
        await RunJobs();
        Assert.Equal(BookingStatus.Expired, Svc.Row(_f, Guid.Parse((string)h["booking"]!["bookingId"]!)).Status);
        Assert.Single(Svc.Events(_f, b.Seller, "booking.hold_expired"));
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start))).StatusCode);
    }

    [Fact]
    public async Task Confirming_a_live_hold_books_it_and_an_expired_hold_can_only_be_confirmed_if_the_slot_is_still_free()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var mid = Svc.BookingId(); var start = Svc.Local(b.Tz, wed, 10);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start, "online"))).StatusCode);
        var c1 = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"));
        Assert.Equal(HttpStatusCode.OK, c1.StatusCode);
        Assert.Equal("Confirmed", (string)(await Orders.Node(c1))["booking"]!["status"]!);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online"))).StatusCode);    // idempotent
        // expired hold, slot still free -> confirm works
        var mid2 = Svc.BookingId(); var start2 = Svc.Local(b.Tz, wed, 14);
        await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid2, start2, "online"));
        _f.Time.Advance(TimeSpan.FromMinutes(30)); await RunJobs();
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid2}/confirm", Svc.Body(b, mid2, start2, "online"))).StatusCode);
        // expired hold whose slot was taken meanwhile -> 409
        var mid3 = Svc.BookingId(); var start3 = Svc.Local(b.Tz, wed, 16);
        await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid3, start3, "online", minutes: 60));
        _f.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start3, "online", minutes: 60))).StatusCode);
        var late = await Svc.Post(b, $"/bookings/{mid3}/confirm", Svc.Body(b, mid3, start3, "online"));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); Assert.Equal("slot_unavailable", await late.ErrCode());
        Assert.Equal(HttpStatusCode.NotFound, (await Svc.Post(b, "/bookings/nope/confirm", Svc.Body(b, "nope", start3, "online"))).StatusCode);
    }

    // ---- instant vs request to book --------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Request_to_book_waits_for_approval_and_approving_confirms_it()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (mid, id, start) = await Svc.Book(b);
        Assert.Equal("Requested", (string)(await Svc.Detail(b.Owner, id))["status"]!);
        Assert.NotNull((await Svc.Detail(b.Owner, id))["approvalDeadline"]);
        Assert.Single(Svc.Events(_f, b.Seller, "booking.requested")); Assert.Empty(Svc.Events(_f, b.Seller, "booking.confirmed"));
        // the pending request still blocks the slot
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start))).StatusCode);
        var tabs = await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings?tab=requests"));
        Assert.Equal(1, (int)tabs["total"]!);
        Assert.Equal(1, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings/counts")))["requests"]!);
        var ok = await Svc.Act(b.Owner, id, "approve");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Confirmed", (string)(await ProdApi.Node(ok))["status"]!);
        var ev = Svc.Events(_f, b.Seller, "booking.confirmed").Single().Data;
        Assert.False((bool)ev["auto"]!);
        Assert.Equal(mid, (string)ev["booking"]!["marketplaceBookingId"]!);
        Assert.Equal(0, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings/counts")))["requests"]!);
        Assert.Equal(1, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings?tab=upcoming")))["total"]!);
    }

    [Fact]
    public async Task Declining_a_request_frees_the_slot_and_tells_the_marketplace()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (_, id, start) = await Svc.Book(b);
        var r = await Svc.Act(b.Owner, id, "decline", new { reason = "Fully booked" });
        Assert.Equal("Declined", (string)(await ProdApi.Node(r))["status"]!);
        var ev = Svc.Events(_f, b.Seller, "booking.declined").Single().Data;
        Assert.Equal("Fully booked", (string)ev["reason"]!); Assert.False((bool)ev["auto"]!);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start))).StatusCode);
        Assert.Equal(1, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings?tab=cancelled")))["total"]!);
    }

    [Fact]
    public async Task Unanswered_requests_are_auto_declined_after_the_approval_time()
    {
        var b = await Svc.NewBiz(_f, d => { d["bookingMode"] = "request"; d["approvalHours"] = 12; });
        var (_, id, start) = await Svc.Book(b);
        _f.Time.Advance(TimeSpan.FromHours(11)); await RunJobs();
        Assert.Equal(BookingStatus.Requested, Svc.Row(_f, id).Status);
        _f.Time.Advance(TimeSpan.FromHours(2));
        var late = await Svc.Act(b.Owner, id, "approve");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); Assert.Equal("request_expired", await late.ErrCode());
        await RunJobs();
        Assert.Equal(BookingStatus.Declined, Svc.Row(_f, id).Status);
        var ev = Svc.Events(_f, b.Seller, "booking.declined").Single().Data;
        Assert.True((bool)ev["auto"]!); Assert.Equal("no_response", (string)ev["reason"]!);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start))).StatusCode);
    }

    [Fact]
    public async Task Proposing_another_time_waits_for_the_buyer_who_can_accept_or_decline()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (mid, id, start) = await Svc.Book(b);
        var newStart = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 14);
        var p = await Svc.Act(b.Owner, id, "propose", new { start = newStart });
        Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        Assert.Equal(newStart, (DateTime)(await Svc.Detail(b.Owner, id))["proposedStartAt"]!);
        Assert.Single(Svc.Events(_f, b.Seller, "booking.time_proposed"));
        Assert.Equal(start, Svc.Row(_f, id).StartUtc);
        var acc = await Svc.Post(b, $"/bookings/{mid}/proposal-responses", new { accept = true });
        Assert.Equal(HttpStatusCode.OK, acc.StatusCode);
        var d = await Svc.Detail(b.Owner, id);
        Assert.Equal("Confirmed", (string)d["status"]!); Assert.Equal(newStart, (DateTime)d["startAt"]!);
        Assert.Null(d["proposedStartAt"]);
        // answering when nothing is proposed is a conflict; a declined proposal cancels the request
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, $"/bookings/{mid}/proposal-responses", new { accept = true })).StatusCode);
        var (mid2, id2, _) = await Svc.Book(b, 10, 1);
        await Svc.Act(b.Owner, id2, "propose", new { start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday).AddDays(7), 15) });
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid2}/proposal-responses", new { accept = false })).StatusCode);
        Assert.Equal(BookingStatus.CancelledByBuyer, Svc.Row(_f, id2).Status);
        // a proposed time that isn't free is refused
        var (_, id3, _) = await Svc.Book(b, 10, 2);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, id3, "propose", new { start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 14) })).StatusCode);
    }

    // ---- reschedule + cancel -----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Seller_reschedule_moves_the_booking_and_asks_for_buyer_consent()
    {
        var b = await Svc.NewBiz(_f);
        var (_, id, old) = await Svc.Book(b);
        var to = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Thursday), 13);
        var r = await Svc.Act(b.Owner, id, "reschedule", new { start = to });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var d = await ProdApi.Node(r);
        Assert.Equal("Rescheduled", (string)d["status"]!); Assert.Equal(to, (DateTime)d["startAt"]!);
        var ev = Svc.Events(_f, b.Seller, "booking.rescheduled").Single().Data;
        Assert.True((bool)ev["requiresBuyerConsent"]!); Assert.Equal("seller", (string)ev["initiatedBy"]!); Assert.Equal(old, (DateTime)ev["previousStartAt"]!);
        // the old slot is free again, the new one is taken
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), old))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), to))).StatusCode);
        // into a slot that is busy, and into the past
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, id, "reschedule", new { start = old })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Svc.Act(b.Owner, id, "reschedule", new { start = _f.Time.GetUtcNow().UtcDateTime.AddDays(-1) })).StatusCode);
    }

    [Fact]
    public async Task Buyer_reschedule_requests_follow_the_policy_limits()
    {
        var b = await Svc.NewBiz(_f, d => { d["maxReschedules"] = 1; d["cancelFreeHours"] = 24; });
        var (mid, id, _) = await Svc.Book(b, 10, 0, minDays: 5);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday, 5);
        var r1 = await Svc.Post(b, $"/bookings/{mid}/reschedule-requests", new { requestId = "R1", newStart = Svc.Local(b.Tz, wed, 14) });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal("Rescheduled", (string)(await Svc.Detail(b.Owner, id))["status"]!);
        Assert.Equal(1, (int)(await Svc.Detail(b.Owner, id))["rescheduleCount"]!);
        // idempotent on the request id
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/reschedule-requests", new { requestId = "R1", newStart = Svc.Local(b.Tz, wed, 14) })).StatusCode);
        var r2 = await Svc.Post(b, $"/bookings/{mid}/reschedule-requests", new { requestId = "R2", newStart = Svc.Local(b.Tz, wed, 12) });
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode); Assert.Equal("reschedule_limit", await r2.ErrCode());
        // inside the free window nothing can be rescheduled any more
        var b2 = await Svc.NewBiz(_f);
        var (mid2, _, start2) = await Svc.Book(b2, 10, 0, minDays: 2);
        _f.Time.Advance(start2 - _f.Time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(5));
        var late = await Svc.Post(b2, $"/bookings/{mid2}/reschedule-requests", new { requestId = "R9", newStart = Svc.Local(b2.Tz, Svc.Next(_f, DayOfWeek.Thursday), 14) });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); Assert.Equal("too_late", await late.ErrCode());
    }

    [Fact]
    public async Task In_request_mode_a_buyer_reschedule_waits_for_the_seller_to_decide()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (mid, id, start) = await Svc.Book(b, 10, 0, minDays: 5);
        await Svc.Act(b.Owner, id, "approve");
        var wed = Svc.Next(_f, DayOfWeek.Wednesday, 5);
        var want = Svc.Local(b.Tz, wed, 14);
        var rq = await Svc.Post(b, $"/bookings/{mid}/reschedule-requests", new { requestId = "R1", newStart = want });
        Assert.True(rq.StatusCode == HttpStatusCode.OK, await rq.Content.ReadAsStringAsync());
        var d = await Svc.Detail(b.Owner, id);
        Assert.Equal(start, (DateTime)d["startAt"]!); Assert.Equal(want, (DateTime)d["rescheduleRequestedStartAt"]!);
        Assert.True((bool)d["actions"]!["decideReschedule"]!);
        Assert.Equal(1, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings?tab=requests")))["total"]!);
        var ok = await Svc.Act(b.Owner, id, "approve-reschedule");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(want, (DateTime)(await Svc.Detail(b.Owner, id))["startAt"]!);
        Assert.Equal("approved", (string)Svc.Events(_f, b.Seller, "booking.reschedule_decided").Single().Data["decision"]!);
        // decline path
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/reschedule-requests", new { requestId = "R2", newStart = Svc.Local(b.Tz, wed, 11) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "decline-reschedule", new { note = "Sorry" })).StatusCode);
        Assert.Equal(want, (DateTime)(await Svc.Detail(b.Owner, id))["startAt"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, id, "approve-reschedule")).StatusCode);
    }

    [Fact]
    public async Task Cancellation_fees_follow_the_policy_snapshot_taken_at_booking_time()
    {
        var b = await Svc.NewBiz(_f);     // free until 24 h before, then 50 %
        // more than 24 h before: free
        var (mid1, id1, _) = await Svc.Book(b, 10, 0, minDays: 5);
        var c1 = await Svc.Post(b, $"/bookings/{mid1}/cancellations", new { requestId = "C1", reason = "changed_mind" });
        Assert.Equal(HttpStatusCode.OK, c1.StatusCode);
        Assert.Equal(0, Svc.Row(_f, id1).CancellationFeeCents);
        Assert.Equal(BookingStatus.CancelledByBuyer, Svc.Row(_f, id1).Status);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid1}/cancellations", new { requestId = "C1", reason = "changed_mind" })).StatusCode);   // idempotent
        // inside the window: 50 % of the total (13000) = 6500, even though the seller later changes the policy
        var (mid2, id2, start2) = await Svc.Book(b, 10, 1, minDays: 5);
        var d = Svc.BaseDetails(x => x["cancelFeePercent"] = 0);
        Assert.Equal(HttpStatusCode.OK, (await Svc.PutDetails(b.Owner, b.Service, d)).StatusCode);
        _f.Time.Advance(start2 - _f.Time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(10));
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid2}/cancellations", new { reason = "sick" })).StatusCode);
        Assert.Equal(6500, Svc.Row(_f, id2).CancellationFeeCents);
        var ev = Svc.Events(_f, b.Seller, "booking.cancelled").Last().Data;
        Assert.Equal(6500, (int)ev["cancellationFeeCents"]!); Assert.Equal(6500, (int)ev["refundDueCents"]!); Assert.Equal("buyer", (string)ev["cancelledBy"]!);
        // the slot is free again
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start2))).StatusCode);
    }

    [Fact]
    public async Task Seller_cancellations_and_pending_requests_never_cost_the_buyer()
    {
        var b = await Svc.NewBiz(_f);
        var (_, id, start) = await Svc.Book(b, 10, 0, minDays: 3);
        _f.Time.Advance(start - _f.Time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Svc.Act(b.Owner, id, "cancel", new { reason = "" })).StatusCode);
        var r = await Svc.Act(b.Owner, id, "cancel", new { reason = "Provider sick" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(BookingStatus.CancelledBySeller, Svc.Row(_f, id).Status); Assert.Equal(0, Svc.Row(_f, id).CancellationFeeCents);
        var ev = Svc.Events(_f, b.Seller, "booking.cancelled").Single().Data;
        Assert.Equal("seller", (string)ev["cancelledBy"]!); Assert.Equal(13000, (int)ev["refundDueCents"]!);
        // pending request cancelled by the buyer inside the window: no fee
        var rb = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (mid2, id2, _) = await Svc.Book(rb, 10, 0, minDays: 3);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(rb, $"/bookings/{mid2}/cancellations", new { reason = "x" })).StatusCode);
        Assert.Equal(0, Svc.Row(_f, id2).CancellationFeeCents);
    }

    // ---- start / complete / no-show --------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Hourly_completion_with_extra_time_requests_an_additional_charge_without_changing_the_price()
    {
        var b = await Svc.NewBiz(_f);                      // $60/h, 30 min increments, booked 120 min = $120
        var (_, id, start) = await Svc.Book(b);
        _f.Time.Advance(start - _f.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(5));
        Assert.Equal("InProgress", (string)(await ProdApi.Node(await Svc.Act(b.Owner, id, "start")))["status"]!);
        var bad = await Svc.Act(b.Owner, id, "complete", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);        // hourly work needs the time actually worked
        var done = await Svc.Act(b.Owner, id, "complete", new { actualMinutes = 190 });      // billed in 30 min steps: 210 min -> 90 extra minutes = $90
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var d = await ProdApi.Node(done);
        Assert.Equal("Completed", (string)d["status"]!);
        Assert.Equal(12000, (int)d["pricing"]!["priceCents"]!);          // the booked price is untouched
        Assert.Equal(9000, (int)d["pricing"]!["additionalChargeCents"]!);
        var ev = Svc.Events(_f, b.Seller, "booking.additional_charge_requested").Single().Data;
        Assert.Equal(9000, (int)ev["amountCents"]!); Assert.Equal(90, (int)ev["extraMinutes"]!); Assert.Equal(210, (int)ev["billedMinutes"]!); Assert.Equal(190, (int)ev["actualMinutes"]!);
        Assert.Equal(190, (int)Svc.Events(_f, b.Seller, "booking.completed").Single().Data["actualMinutes"]!);
    }

    [Fact]
    public async Task Completion_within_the_booked_time_or_for_a_fixed_price_job_creates_no_extra_charge()
    {
        var b = await Svc.NewBiz(_f);
        var (_, id, start) = await Svc.Book(b);
        _f.Time.Advance(start - _f.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        await Svc.Act(b.Owner, id, "start");
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "complete", new { actualMinutes = 100 })).StatusCode);
        Assert.Empty(Svc.Events(_f, b.Seller, "booking.additional_charge_requested"));
        var fixedBiz = await Svc.NewBiz(_f, d => { d["pricingModel"] = "fixed"; d["price"] = 99; d["minMinutes"] = null; d["maxMinutes"] = null; });
        var (_, fid, fstart) = await Svc.Book(fixedBiz);
        _f.Time.Advance(fstart - _f.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        await Svc.Act(fixedBiz.Owner, fid, "start");
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(fixedBiz.Owner, fid, "complete", new { })).StatusCode);
        Assert.Empty(Svc.Events(_f, fixedBiz.Seller, "booking.additional_charge_requested"));
        Assert.Equal(9900 + 1000, Svc.Row(_f, fid).TotalCents);
    }

    [Fact]
    public async Task No_show_is_only_possible_after_the_start_and_records_the_policy_fee()
    {
        var b = await Svc.NewBiz(_f, d => d["noShowFeePercent"] = 40);
        var (_, id, start) = await Svc.Book(b);
        var early = await Svc.Act(b.Owner, id, "no-show");
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode); Assert.Equal("too_early", await early.ErrCode());
        _f.Time.Advance(start - _f.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "no-show")).StatusCode);
        Assert.Equal(5200, Svc.Row(_f, id).CancellationFeeCents);
        Assert.Equal(5200, (int)Svc.Events(_f, b.Seller, "booking.no_show").Single().Data["noShowFeeCents"]!);
        Assert.Equal(1, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings?tab=past")))["total"]!);
    }

    [Fact]
    public async Task Every_status_transition_is_checked_and_illegal_ones_answer_409()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (_, req, _) = await Svc.Book(b, 10, 0);                     // Requested
        foreach (var a in new[] { "start", "complete", "no-show", "reschedule", "approve-reschedule" })
            Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, req, a, new { start = _f.Time.GetUtcNow().UtcDateTime.AddDays(9), actualMinutes = 60 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, req, "approve")).StatusCode);           // Confirmed
        foreach (var a in new[] { "approve", "decline", "propose", "complete" })
            Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, req, a, new { start = _f.Time.GetUtcNow().UtcDateTime.AddDays(9), actualMinutes = 60 })).StatusCode);
        var before = Svc.Events(_f, b.Seller).Count;
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, req, "approve")).StatusCode);
        Assert.Equal(before, Svc.Events(_f, b.Seller).Count);              // a rejected action writes nothing to the outbox
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, req, "cancel", new { reason = "x" })).StatusCode);   // Cancelled by seller: terminal
        foreach (var a in new[] { "approve", "decline", "start", "complete", "no-show", "cancel", "reschedule", "reassign" })
            Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, req, a, new { reason = "y", start = _f.Time.GetUtcNow().UtcDateTime.AddDays(9), providerId = b.OwnerMember })).StatusCode);
        // the state machine itself
        Assert.True(Solvelt.Api.Bookings.BookingStateMachine.Can(BookingStatus.Hold, Solvelt.Api.Bookings.BookingStateMachine.Act.ConfirmHold));
        Assert.False(Solvelt.Api.Bookings.BookingStateMachine.Can(BookingStatus.Completed, Solvelt.Api.Bookings.BookingStateMachine.Act.CancelByBuyer));
        Assert.False(Solvelt.Api.Bookings.BookingStateMachine.Can(BookingStatus.InProgress, Solvelt.Api.Bookings.BookingStateMachine.Act.CancelBySeller));
        Assert.Empty(Solvelt.Api.Bookings.BookingStateMachine.Allowed(BookingStatus.Declined));
        Assert.Empty(Solvelt.Api.Bookings.BookingStateMachine.Allowed(BookingStatus.Expired));
        Assert.Empty(Solvelt.Api.Bookings.BookingStateMachine.Allowed(BookingStatus.NoShow));
    }

    [Fact]
    public async Task Optimistic_concurrency_rejects_stale_versions()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (_, id, _) = await Svc.Book(b);
        var v = (int)(await Svc.Detail(b.Owner, id))["version"]!;
        var stale = await Svc.Act(b.Owner, id, "approve", new { version = v - 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Equal("version_conflict", await stale.ErrCode());
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "approve", new { version = v })).StatusCode);
        Assert.Equal(v + 1, (int)(await Svc.Detail(b.Owner, id))["version"]!);
    }

    // ---- providers, reassignment, assignment rules ------------------------------------------------------------------------------------------
    private async Task<(Api api, Guid member)> SecondProvider(Biz b, string role = "Manager")
    {
        var (api, email) = await TeamHelpers.JoinAs(_f, b.Owner, role);
        var mem = Svc.MemberOf(_f, b.Seller, email);
        await Svc.MakeProvider(b.Owner, mem);
        await Svc.SetHours(b.Owner, mem, Svc.WeekdayHours());
        Assert.Equal(HttpStatusCode.OK, (await Svc.SetProviders(b.Owner, b.Service, b.OwnerMember, mem)).StatusCode);
        return (api, mem);
    }

    [Fact]
    public async Task Reassigning_needs_an_eligible_free_provider()
    {
        var b = await Svc.NewBiz(_f, d => d["assignmentRule"] = "leastBusy");
        var (_, p2) = await SecondProvider(b);
        var (_, id, start) = await Svc.Book(b);
        var res = await Svc.Act(b.Owner, id, "reassign", new { providerId = p2 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(p2, Svc.Row(_f, id).ProviderId);
        Assert.Equal(b.OwnerMember, Guid.Parse((string)Svc.Events(_f, b.Seller, "booking.provider_changed").Single().Data["previousProviderId"]!));
        // same provider, ineligible provider, busy provider
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Svc.Act(b.Owner, id, "reassign", new { providerId = p2 })).StatusCode);
        var (_, p3) = (0, Svc.MemberOf(_f, b.Seller));
        var (_, other) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager");
        var ineligible = Svc.MemberOf(_f, b.Seller, other);
        await Svc.MakeProvider(b.Owner, ineligible);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Svc.Act(b.Owner, id, "reassign", new { providerId = ineligible })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start, provider: b.OwnerMember))).StatusCode);   // owner now has the slot booked
        var busy = await Svc.Act(b.Owner, id, "reassign", new { providerId = p3 });
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode); Assert.Equal("provider_busy", await busy.ErrCode());
    }

    [Fact]
    public async Task Auto_assignment_picks_the_least_busy_or_rotates_and_buyer_choice_demands_a_provider()
    {
        var least = await Svc.NewBiz(_f, d => d["assignmentRule"] = "leastBusy");
        var (_, p2) = await SecondProvider(least);
        // owner gets a booking first, so the next auto-assigned booking goes to the other provider
        var b1 = Svc.Local(least.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 10);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(least, "/bookings", Svc.Body(least, Svc.BookingId(), b1, provider: least.OwnerMember))).StatusCode);
        var auto = new { marketplaceBookingId = Svc.BookingId(), serviceId = least.Service, start = Svc.Local(least.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 14), location = "online", buyer = new { displayName = "Jane B." } };
        var a = await Svc.Post(least, "/bookings", auto);
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(p2.ToString(), (string)(await Orders.Node(a))["booking"]!["providerId"]!);

        var rr = await Svc.NewBiz(_f, d => d["assignmentRule"] = "roundRobin");
        var (_, rp2) = await SecondProvider(rr);
        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var r = await Svc.Post(rr, "/bookings", new { marketplaceBookingId = Svc.BookingId(), serviceId = rr.Service, start = Svc.Local(rr.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 9 + i * 2), location = "online", buyer = new { displayName = "J" } });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            seen.Add((string)(await Orders.Node(r))["booking"]!["providerId"]!);
        }
        Assert.Equal(2, seen.Distinct().Count());
        Assert.Equal(seen[0], seen[2]); Assert.Equal(seen[1], seen[3]);

        var choice = await Svc.NewBiz(_f);
        await SecondProvider(choice);
        var none = await Svc.Post(choice, "/bookings", new { marketplaceBookingId = Svc.BookingId(), serviceId = choice.Service, start = Svc.Local(choice.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 10), location = "online", buyer = new { displayName = "J" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode); Assert.Equal("provider_required", await none.ErrCode());
        _ = rp2;
    }

    [Fact]
    public async Task Each_provider_has_their_own_calendar_and_time_zone()
    {
        var b = await Svc.NewBiz(_f);
        var (_, email) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager");
        var p2 = Svc.MemberOf(_f, b.Seller, email);
        await Svc.MakeProvider(b.Owner, p2, "America/Los_Angeles");
        await Svc.SetHours(b.Owner, p2, Svc.WeekdayHours());
        await Svc.SetProviders(b.Owner, b.Service, b.OwnerMember, p2);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var s = await Svc.Slots(b, Svc.Local(b.Tz, wed, 0), Svc.Local(b.Tz, wed.AddDays(2), 0), "&location=online");
        var ny9 = Svc.Local(Svc.Ny, wed, 9); var la9 = Svc.Local("America/Los_Angeles", wed, 9);
        var at = (DateTime t) => s["slots"]!.AsArray().FirstOrDefault(x => (DateTime)x!["startAt"]! == t);
        Assert.Equal([b.OwnerMember.ToString()], at(ny9)!["providerIds"]!.AsArray().Select(x => (string)x!).Where(x => x == b.OwnerMember.ToString()).ToArray());
        Assert.Contains(p2.ToString(), at(la9)!["providerIds"]!.AsArray().Select(x => (string)x!));
        Assert.DoesNotContain(p2.ToString(), at(ny9)!["providerIds"]!.AsArray().Select(x => (string)x!));      // 9am New York is 6am for the LA provider
        // booking one provider leaves the other free at the same time
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 13), "online", provider: b.OwnerMember))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 13), "online", provider: p2))).StatusCode);
    }

    [Fact]
    public async Task Turning_a_provider_off_is_refused_while_they_have_upcoming_bookings()
    {
        var b = await Svc.NewBiz(_f);
        await Svc.Book(b);
        var r = await b.Owner.Put($"/api/v1/seller/providers/{b.OwnerMember}", new { isProvider = false });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("has_bookings", await r.ErrCode());
    }

    // ---- privacy -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Customer_address_and_phone_are_only_visible_to_the_assigned_provider_and_owners_and_admins()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (_, p2) = await SecondProvider(b);
        var (admin, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Admin");
        var (mgr, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager");          // not a provider, not assigned
        var (viewer, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Viewer");
        var (mid, id, _) = await Svc.Book(b);
        // before confirmation nobody sees address or phone
        var req = await Svc.Detail(b.Owner, id);
        Assert.False((bool)req["customer"]!["visible"]!); Assert.Null(req["customer"]!["address"]); Assert.Null(req["customer"]!["phone"]);
        await Svc.Act(b.Owner, id, "approve");
        var owner = await Svc.Detail(b.Owner, id);
        Assert.True((bool)owner["customer"]!["visible"]!);
        Assert.Equal("1 Main St", (string)owner["customer"]!["address"]!["line1"]!);
        Assert.Equal("+1 555 010 0000", (string)owner["customer"]!["phone"]!);
        Assert.True((bool)(await Svc.Detail(admin, id))["customer"]!["visible"]!);
        foreach (var other in new[] { mgr, viewer })
        {
            var d = await Svc.Detail(other, id);
            Assert.False((bool)d["customer"]!["visible"]!); Assert.Null(d["customer"]!["address"]); Assert.Null(d["customer"]!["phone"]);
            Assert.DoesNotContain("1 Main St", d.ToJsonString()); Assert.DoesNotContain("555 010", d.ToJsonString());
        }
        // reassigned to the manager-provider: now THEY see it and the owner (an Owner) still does
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "reassign", new { providerId = p2 })).StatusCode);
        Assert.True((bool)(await Svc.Detail(b.Owner, id))["customer"]!["visible"]!);
        _ = mid;
    }

    [Fact]
    public async Task Customer_data_is_redacted_after_the_retention_period()
    {
        var b = await Svc.NewBiz(_f);
        var (_, id, start) = await Svc.Book(b);
        _f.Time.Advance(start - _f.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        await Svc.Act(b.Owner, id, "start"); await Svc.Act(b.Owner, id, "complete", new { actualMinutes = 120 });
        await RunJobs();
        Assert.True((bool)(await Svc.Detail(b.Owner, id))["customer"]!["visible"]!);          // still within the retention window
        _f.Time.Advance(TimeSpan.FromDays(31)); await RunJobs();
        var d = await Svc.Detail(b.Owner, id);
        Assert.False((bool)d["customer"]!["visible"]!); Assert.True((bool)d["customer"]!["redacted"]!);
        var row = Svc.Row(_f, id);
        Assert.Equal("Redacted", row.BuyerName); Assert.Null(row.AddressJson); Assert.Null(row.BuyerPhone); Assert.Null(row.BuyerEmail); Assert.NotNull(row.PiiRedactedAt);
        Assert.Equal(13000, row.TotalCents);                                                    // money records stay
        Assert.Contains(d["timeline"]!.AsArray(), e => (string)e!["type"]! == "booking.redacted");
    }

    [Fact]
    public async Task The_online_meeting_link_is_only_revealed_to_a_confirmed_buyer()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var hold = await Orders.Node(await Svc.Post(b, "/bookings/holds", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 10), "online")));
        Assert.DoesNotContain("meet.example", hold.ToJsonString());
        var created = await Orders.Node(await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 13), "online")));
        Assert.Equal("https://meet.example.test/room-42", (string)created["booking"]!["onlineInstructions"]!);
        Assert.Contains(Svc.Events(_f, b.Seller, "booking.confirmed"), e => (string?)e.Data["onlineInstructions"] == "https://meet.example.test/room-42");
        // a request-to-book stays hidden until approved
        var rb = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var req = await Orders.Node(await Svc.Post(rb, "/bookings", Svc.Body(rb, Svc.BookingId(), Svc.Local(rb.Tz, wed, 10), "online")));
        Assert.Null(req["booking"]!["onlineInstructions"]);
        Assert.DoesNotContain("meet.example", Svc.Events(_f, rb.Seller, "booking.requested").Single().Data.ToJsonString());
        await Svc.Act(rb.Owner, Guid.Parse((string)req["booking"]!["bookingId"]!), "approve");
        Assert.Contains("meet.example", Svc.Events(_f, rb.Seller, "booking.confirmed").Single().Data.ToJsonString());
    }

    // ---- outbox ------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Booking_events_are_signed_and_delivered_through_the_outbox()
    {
        var b = await Svc.NewBiz(_f);
        var (mid, _, _) = await Svc.Book(b);
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().PostAsync("/api/dev/outbox/dispatch", null)).StatusCode);
        var sink = await Orders.Node(await _f.NewClient().GetAsync("/api/dev/outbox"));
        var mine = sink.AsArray().Where(x => (string)x!["type"]! == "booking.confirmed" && ((string)x["body"]!).Contains(mid)).ToList();
        var one = Assert.Single(mine);
        Assert.StartsWith("v1=", (string)one!["signature"]!);
        Assert.True(Signing.Verify(OrdersFactory.OutSecret, (string)one["timestamp"]! + "." + (string)one["body"]!, (string)one["signature"]!));
    }

    // ---- ICS ---------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_ics_feed_is_private_revocable_hashed_and_minimal()
    {
        var b = await Svc.NewBiz(_f);
        var (mid, id, start) = await Svc.Book(b);
        var created = await ProdApi.Node(await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/ics"));
        var token = (string)created["token"]!;
        Assert.StartsWith("ics_", token); Assert.True(token.Length >= 40);
        // stored hashed, never in clear
        var stored = _f.WithDb(db => db.ProviderProfiles.AsNoTracking().First(p => p.MembershipId == b.OwnerMember).IcsTokenHash);
        Assert.NotNull(stored); Assert.Equal(64, stored!.Length); Assert.DoesNotContain(token, stored);
        var c = _f.NewClient();
        var feed = await c.GetAsync($"/api/calendar/feeds/{token}.ics");
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.StartsWith("text/calendar", feed.Content.Headers.ContentType!.ToString());
        Assert.Contains("no-store", feed.Headers.CacheControl!.ToString());
        var ics = await feed.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", ics); Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains($"DTSTART:{start:yyyyMMdd'T'HHmmss'Z'}", ics);
        Assert.Contains("Home cleaning service", ics);
        // minimal data: no buyer name, address, phone or e-mail
        foreach (var secret in new[] { "Jane", "Main St", "555 010", "relay-j", "11201", "Brooklyn" }) Assert.DoesNotContain(secret, ics);
        // unknown / malformed tokens and the bearer token are no keys
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/calendar/feeds/ics_" + new string('a', 43) + ".ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/calendar/feeds/short")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/calendar/feeds/" + b.Owner.Session!.AccessToken)).StatusCode);
        // regenerating revokes the old link
        var fresh = (string)(await ProdApi.Node(await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/ics")))["token"]!;
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/calendar/feeds/{token}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/calendar/feeds/{fresh}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.Owner.Delete($"/api/v1/seller/providers/{b.OwnerMember}/ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/calendar/feeds/{fresh}.ics")).StatusCode);
        // a cancelled booking disappears from the feed
        var again = (string)(await ProdApi.Node(await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/ics")))["token"]!;
        await Svc.Act(b.Owner, id, "cancel", new { reason = "x" });
        Assert.DoesNotContain("BEGIN:VEVENT", await (await c.GetAsync($"/api/calendar/feeds/{again}.ics")).Content.ReadAsStringAsync());
        _ = mid;
    }

    [Fact]
    public async Task The_account_feed_shows_every_provider_and_only_owners_and_admins_create_it()
    {
        var b = await Svc.NewBiz(_f);
        await Svc.Book(b);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var (mgr, p2Member) = await SecondProvider(b);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Post("/api/v1/seller/calendar/ics")).StatusCode);
        await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 14), provider: p2Member));
        var token = (string)(await ProdApi.Node(await b.Owner.Post("/api/v1/seller/calendar/ics")))["token"]!;
        var ics = await _f.NewClient().GetStringAsync($"/api/calendar/feeds/{token}.ics");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ics, "BEGIN:VEVENT").Count);
        // a provider can only create their own feed; others' feeds are 404
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Post($"/api/v1/seller/providers/{b.OwnerMember}/ics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/v1/seller/providers/{p2Member}/ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.Owner.Delete("/api/v1/seller/calendar/ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.NewClient().GetAsync($"/api/calendar/feeds/{token}.ics")).StatusCode);
    }

    // ---- permissions: IDOR, roles, brand scope, provider-only-own ---------------------------------------------------------------------------------
    [Fact]
    public async Task Other_accounts_never_see_or_touch_bookings_calendars_or_quotes()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (_, id, _) = await Svc.Book(b);
        var (other, _) = await ProdApi.Approved(_f);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get($"/api/v1/seller/bookings/{id}")).StatusCode);
        foreach (var a in new[] { "approve", "decline", "propose", "reschedule", "cancel", "start", "complete", "no-show", "reassign", "approve-reschedule", "decline-reschedule" })
            Assert.Equal(HttpStatusCode.NotFound, (await Svc.Act(other, id, a, new { reason = "x", start = _f.Time.GetUtcNow().UtcDateTime.AddDays(9), providerId = b.OwnerMember, actualMinutes = 5 })).StatusCode);
        Assert.Equal(0, (int)(await ProdApi.Node(await other.Get("/api/v1/seller/bookings")))["total"]!);
        var cal = await ProdApi.Node(await other.Get($"/api/v1/seller/calendar?from={Svc.Iso(_f.Time.GetUtcNow().UtcDateTime)}&to={Svc.Iso(_f.Time.GetUtcNow().UtcDateTime.AddDays(30))}"));
        Assert.Empty(cal["bookings"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get($"/api/v1/seller/providers/{b.OwnerMember}/availability")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post($"/api/v1/seller/providers/{b.OwnerMember}/blocks", new { kind = "busy", start = _f.Time.GetUtcNow().UtcDateTime.AddDays(2), end = _f.Time.GetUtcNow().UtcDateTime.AddDays(3) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post($"/api/v1/seller/providers/{b.OwnerMember}/ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get($"/api/v1/seller/quote-requests/{Guid.NewGuid()}")).StatusCode);
        // the unknown ids of one's own account behave the same as foreign ones
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.Get($"/api/v1/seller/bookings/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Brand_scope_hides_other_brands_bookings_from_managers()
    {
        var b = await Svc.NewBiz(_f);                         // service in brand 1
        var brand2 = await TeamHelpers.NewBrand(b.Owner, "Second brand");
        var svc2 = await Svc.NewService(b.Owner, "Gutter cleaning service", brand2);
        Assert.Equal(HttpStatusCode.OK, (await Svc.PutDetails(b.Owner, svc2, Svc.BaseDetails())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Svc.SetProviders(b.Owner, svc2, b.OwnerMember)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Owner.Post($"/api/v1/seller/products/{svc2}/status", new { status = "active" })).StatusCode);
        var b2 = b with { Service = svc2 };
        var (_, id1, _) = await Svc.Book(b);
        var (_, id2, _) = await Svc.Book(b2, 14);
        var (mgrX, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager", [b.Brand]);
        var (mgrY, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager", [brand2]);
        Assert.Equal(HttpStatusCode.OK, (await mgrX.Get($"/api/v1/seller/bookings/{id1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgrX.Get($"/api/v1/seller/bookings/{id2}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgrY.Get($"/api/v1/seller/bookings/{id1}")).StatusCode);
        Assert.Equal(1, (int)(await ProdApi.Node(await mgrX.Get("/api/v1/seller/bookings")))["total"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Svc.Act(mgrX, id2, "cancel", new { reason = "no" })).StatusCode);
        var cal = await ProdApi.Node(await mgrX.Get($"/api/v1/seller/calendar?from={Svc.Iso(_f.Time.GetUtcNow().UtcDateTime)}&to={Svc.Iso(_f.Time.GetUtcNow().UtcDateTime.AddDays(40))}"));
        Assert.Equal(id1.ToString(), (string)cal["bookings"]!.AsArray().Single()!["id"]!);
        // a manager of the brand can act on it, an owner on everything
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(mgrX, id1, "cancel", new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id2, "cancel", new { reason = "no" })).StatusCode);
    }

    [Fact]
    public async Task Viewers_read_but_cannot_act_and_providers_only_see_and_act_on_their_own_bookings()
    {
        var b = await Svc.NewBiz(_f);
        var (viewerReader, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Viewer");           // not a provider: read-only on every brand
        var (provApi, p2) = await SecondProvider(b, "Viewer");                                 // a Viewer who is a provider: own bookings only
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var (_, ownersId, _) = await Svc.Book(b);
        var mid2 = Svc.BookingId();
        var theirs = await Orders.Node(await Svc.Post(b, "/bookings", Svc.Body(b, mid2, Svc.Local(b.Tz, wed, 14), provider: p2)));
        var theirId = Guid.Parse((string)theirs["booking"]!["bookingId"]!);
        // plain viewer: reads everything in scope, acts on nothing
        Assert.Equal(2, (int)(await ProdApi.Node(await viewerReader.Get("/api/v1/seller/bookings")))["total"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await Svc.Act(viewerReader, ownersId, "cancel", new { reason = "x" })).StatusCode);
        Assert.False((bool)(await Svc.Detail(viewerReader, ownersId))["actions"]!["cancel"]!);
        // provider-viewer: only their own booking is visible, the owner's is a 404
        var list = await ProdApi.Node(await provApi.Get("/api/v1/seller/bookings"));
        Assert.Equal(1, (int)list["total"]!); Assert.Equal(theirId.ToString(), (string)list["items"]![0]!["id"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await provApi.Get($"/api/v1/seller/bookings/{ownersId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Svc.Act(provApi, ownersId, "cancel", new { reason = "x" })).StatusCode);
        var cal = await ProdApi.Node(await provApi.Get($"/api/v1/seller/calendar?from={Svc.Iso(_f.Time.GetUtcNow().UtcDateTime)}&to={Svc.Iso(_f.Time.GetUtcNow().UtcDateTime.AddDays(30))}"));
        Assert.Single(cal["bookings"]!.AsArray());
        Assert.Single(cal["providers"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await provApi.Get($"/api/v1/seller/providers/{b.OwnerMember}/availability")).StatusCode);
        // ...but they can act on theirs, even though Viewers normally can't write
        Assert.True((bool)(await Svc.Detail(provApi, theirId))["actions"]!["cancel"]!);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(provApi, theirId, "cancel", new { reason = "Can't make it" })).StatusCode);
        // owner and admin see both providers in the combined view
        var combined = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/calendar?from={Svc.Iso(_f.Time.GetUtcNow().UtcDateTime)}&to={Svc.Iso(_f.Time.GetUtcNow().UtcDateTime.AddDays(30))}"));
        Assert.True((bool)combined["combined"]!); Assert.Equal(2, combined["providers"]!.AsArray().Count);
    }

    [Fact]
    public async Task The_calendar_returns_bookings_blocks_hours_and_the_provider_time_zone()
    {
        var b = await Svc.NewBiz(_f);
        var (_, id, start) = await Svc.Book(b);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/blocks", new { kind = "busy", start = Svc.Local(b.Tz, wed, 15), end = Svc.Local(b.Tz, wed, 16), note = "Lunch" });
        var cal = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/calendar?from={Svc.Iso(wed.AddDays(-1))}&to={Svc.Iso(wed.AddDays(2))}&providerId={b.OwnerMember}"));
        Assert.Equal(Svc.Ny, (string)cal["providers"]![0]!["timeZone"]!);
        var booking = cal["bookings"]!.AsArray().Single()!;
        Assert.Equal(id.ToString(), (string)booking["id"]!); Assert.Equal(start, (DateTime)booking["startAt"]!); Assert.Equal("Confirmed", (string)booking["status"]!);
        Assert.Single(cal["blocks"]!.AsArray()); Assert.NotEmpty(cal["workingHours"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.Get($"/api/v1/seller/calendar?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(400))}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.Get($"/api/v1/seller/calendar?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(-1))}")).StatusCode);
        // brand and service filters
        var none = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/calendar?from={Svc.Iso(wed.AddDays(-1))}&to={Svc.Iso(wed.AddDays(2))}&serviceId={Guid.NewGuid()}"));
        Assert.Empty(none["bookings"]!.AsArray());
    }

    [Fact]
    public async Task Booking_detail_shows_timeline_pricing_policy_intake_and_actions()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 10);
        var mid = Svc.BookingId();
        var r = await Svc.Post(b, "/bookings", Svc.Body(b, mid, start, addOns: ["deep"], answers: [new { id = "pets", answer = "yes" }]));
        var id = Guid.Parse((string)(await Orders.Node(r))["booking"]!["bookingId"]!);
        var d = await Svc.Detail(b.Owner, id);
        Assert.Equal(6000, (int)d["pricing"]!["rateCents"]!); Assert.Equal(2500, (int)d["pricing"]!["addOnsCents"]!); Assert.Equal(1000, (int)d["pricing"]!["travelFeeCents"]!);
        Assert.Equal(24, (int)d["policy"]!["cancelFreeHours"]!); Assert.Equal(50, (int)d["policy"]!["cancelFeePercent"]!);
        Assert.Equal("Any pets?", (string)d["intake"]![0]!["question"]!); Assert.Equal("yes", (string)d["intake"]![0]!["answer"]!);
        Assert.Equal("Deep clean", (string)d["addOns"]![0]!["name"]!);
        Assert.Equal("Clear the area first.", (string)d["requirements"]!);
        Assert.Equal("booking.requested", (string)d["timeline"]!.AsArray().Last()!["type"]!);
        Assert.True((bool)d["actions"]!["approve"]!); Assert.False((bool)d["actions"]!["start"]!);
        Assert.Equal(Svc.Ny, (string)d["providerTimeZone"]!);
    }

    [Fact]
    public async Task Approvals_cancellations_reschedules_reassignments_and_completions_are_audited()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (_, id, start) = await Svc.Book(b);
        await Svc.Act(b.Owner, id, "approve");
        await Svc.Act(b.Owner, id, "reschedule", new { start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Thursday), 13) });
        _f.Time.Advance(TimeSpan.FromDays(30));
        var (_, id2, start2) = await Svc.Book(b, 10, 0);
        await Svc.Act(b.Owner, id2, "approve");
        await Svc.Act(b.Owner, id2, "cancel", new { reason = "x" });
        var types = _f.WithDb(db => db.AuditEvents.AsNoTracking().Select(a => a.Type).ToList());
        Assert.Contains("booking.approved", types); Assert.Contains("booking.rescheduled", types); Assert.Contains("booking.cancelled_by_seller", types);
        var detail = _f.WithDb(db => db.AuditEvents.AsNoTracking().First(a => a.Type == "booking.approved").Detail);
        Assert.DoesNotContain("Jane", detail ?? ""); Assert.DoesNotContain("Main St", detail ?? "");
        _ = start; _ = start2;
    }

    // ---- simulator ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_simulator_queries_slots_books_reschedules_and_cancels_as_a_buyer()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var slots = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/dev/services/{b.Service}/slots?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}&location=online"));
        Assert.NotEmpty(slots["slots"]!.AsArray());
        var first = (DateTime)slots["slots"]![0]!["startAt"]!;
        var booked = await b.Owner.Post("/api/v1/seller/dev/bookings", new { serviceId = b.Service, start = first, location = "online", mode = "book" });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var mid = (string)(await ProdApi.Node(booked))["booking"]!["marketplaceBookingId"]!;
        var moved = await b.Owner.Post($"/api/v1/seller/dev/bookings/{mid}/events", new { type = "reschedule", start = Svc.Local(b.Tz, wed.AddDays(7), 14) });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var cancelled = await b.Owner.Post($"/api/v1/seller/dev/bookings/{mid}/events", new { type = "cancel" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var hold = await b.Owner.Post("/api/v1/seller/dev/bookings", new { serviceId = b.Service, start = first, location = "online", mode = "hold" });
        Assert.Equal("Hold", (string)(await ProdApi.Node(hold))["booking"]!["status"]!);
        var (other, _) = await ProdApi.Approved(_f);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post("/api/v1/seller/dev/bookings", new { serviceId = b.Service, start = first, location = "online" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get($"/api/v1/seller/dev/services/{b.Service}/slots?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post($"/api/v1/seller/dev/bookings/{mid}/events", new { type = "cancel" })).StatusCode);
    }

    // ---- reminders ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Providers_get_a_day_before_and_an_hour_before_reminder_by_email_and_the_buyer_gets_an_event()
    {
        var b = await Svc.NewBiz(_f);
        var (_, id, start) = await Svc.Book(b);
        var email = _f.WithDb(db => (from m in db.Memberships join u in db.Users on m.UserId equals u.Id where m.Id == b.OwnerMember select u.Email).First())!;
        _f.Time.Advance(start - _f.Time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(23)); await RunJobs();
        Assert.Single(_f.Mails(email, "booking-reminder"));
        await RunJobs();
        Assert.Single(_f.Mails(email, "booking-reminder"));                                        // only once
        _f.Time.Advance(TimeSpan.FromMinutes(22 * 60 + 30)); await RunJobs();
        Assert.Equal(2, _f.Mails(email, "booking-reminder").Count);
        Assert.Equal(["day_before", "hour_before"], Svc.Events(_f, b.Seller, "booking.reminder").Select(e => (string)e.Data["kind"]!).ToArray());
        Assert.DoesNotContain("Jane", _f.Mails(email, "booking-reminder").Last().Text);              // no customer data in the mail
        // opting out of the hour-before mail
        var b2 = await Svc.NewBiz(_f);
        Assert.Equal(HttpStatusCode.OK, (await b2.Owner.Put($"/api/v1/seller/providers/{b2.OwnerMember}", new { reminderDayBefore = false, reminderHourBefore = false })).StatusCode);
        var (_, _, s2) = await Svc.Book(b2);
        var email2 = _f.WithDb(db => (from m in db.Memberships join u in db.Users on m.UserId equals u.Id where m.Id == b2.OwnerMember select u.Email).First())!;
        _f.Time.Advance(s2 - _f.Time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(30)); await RunJobs();
        Assert.Empty(_f.Mails(email2, "booking-reminder"));
        _ = id;
    }
}
