using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the orchestrator's review of the services backend: each one fails when its fix is reverted.</summary>
public class ServiceReviewTests : IClassFixture<SvcFactory>
{
    private readonly SvcFactory _f;
    public ServiceReviewTests(SvcFactory f) => _f = f;

    private DateTime Now => _f.Time.GetUtcNow().UtcDateTime;
    private Task RunJobs() => _f.NewClient().PostAsync("/api/dev/bookings/maintenance/run", null);
    private void GoTo(DateTime utc) => _f.Time.Advance(utc - Now);

    // 1 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_pending_buyer_reschedule_cannot_move_a_completed_booking()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (mid, id, start) = await Svc.Book(b, 10, 0, minDays: 5);
        await Svc.Act(b.Owner, id, "approve");
        var want = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday, 5), 14);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/reschedule-requests", new { requestId = "R1", newStart = want })).StatusCode);
        GoTo(start.AddMinutes(1));
        await Svc.Act(b.Owner, id, "start");
        Assert.Null(Svc.Row(_f, id).RescheduleRequestedStartUtc);                      // starting clears the pending request
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "complete", new { actualMinutes = 120 })).StatusCode);
        foreach (var a in new[] { "approve-reschedule", "decline-reschedule" })
            Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, id, a)).StatusCode);
        var row = Svc.Row(_f, id);
        Assert.Equal(BookingStatus.Completed, row.Status); Assert.Equal(start, row.StartUtc);
        Assert.Empty(Svc.Events(_f, b.Seller, "booking.reschedule_decided"));
    }

    [Fact]
    public async Task A_pending_buyer_reschedule_is_dropped_by_no_show_and_cannot_be_answered_afterwards()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (mid, id, start) = await Svc.Book(b, 10, 0, minDays: 5);
        await Svc.Act(b.Owner, id, "approve");
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/reschedule-requests", new { requestId = "R1", newStart = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday, 5), 14) })).StatusCode);
        GoTo(start.AddMinutes(30));
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "no-show")).StatusCode);
        Assert.Null(Svc.Row(_f, id).RescheduleRequestedStartUtc);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Act(b.Owner, id, "approve-reschedule")).StatusCode);
        Assert.Equal(BookingStatus.NoShow, Svc.Row(_f, id).Status);
    }

    // 2 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task One_quote_can_only_be_accepted_once_even_in_parallel()
    {
        var b = await Svc.NewBiz(_f, d => { d["pricingModel"] = "quote"; d["price"] = null; d["minMinutes"] = null; d["maxMinutes"] = null; d["incrementMinutes"] = 60; });
        var qid = "Q-" + Guid.NewGuid().ToString("N")[..10];
        var created = await Svc.Post(b, "/quote-requests", new
        {
            marketplaceQuoteId = qid, serviceId = b.Service, buyer = new { displayName = "Jane" }, details = "Fix the sink", location = "customer",
            address = new { line1 = "1 Main St", postalCode = "11201" }
        });
        var id = Guid.Parse((string)(await Orders.Node(created))["quoteId"]!);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var s1 = Svc.Local(b.Tz, wed, 9); var s2 = Svc.Local(b.Tz, wed, 14);
        var sent = await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/quote", new { price = 200m, durationMinutes = 120, slots = new[] { s1, s2 }, providerId = b.OwnerMember, expiresAt = Now.AddDays(3) });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
            (await Svc.Post(b, $"/quotes/{qid}/accept", new { marketplaceBookingId = Svc.BookingId(), start = i % 2 == 0 ? s1 : s2 })).StatusCode)));
        Assert.Equal(1, results.Count(r => r == HttpStatusCode.Created));
        Assert.All(results.Where(r => r != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r));
        Assert.Equal(1, _f.WithDb(db => db.Bookings.Count(x => x.QuoteId == id)));
        Assert.Equal(QuoteStatus.Accepted, _f.WithDb(db => db.QuoteRequests.First(x => x.Id == id).Status));
        Assert.Equal(1, _f.WithDb(db => db.Bookings.Count(x => x.ProductId == b.Service)));
    }

    // 3 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Add_on_time_is_not_charged_again_when_the_work_takes_exactly_the_booked_time()
    {
        var b = await Svc.NewBiz(_f);            // 120 min + "deep" (+30 min) = 150 booked
        var (_, exact, s1) = await Svc.Book(b, 9, 0, addOns: ["deep"]);
        var (_, over, s2) = await Svc.Book(b, 9, 1, addOns: ["deep"]);
        GoTo(s1.AddMinutes(1)); await Svc.Act(b.Owner, exact, "start");
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, exact, "complete", new { actualMinutes = 150 })).StatusCode);
        Assert.Empty(Svc.Events(_f, b.Seller, "booking.additional_charge_requested"));
        Assert.Equal(0, Svc.Row(_f, exact).AdditionalChargeCents);
        GoTo(s2.AddMinutes(1)); await Svc.Act(b.Owner, over, "start");
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, over, "complete", new { actualMinutes = 180 })).StatusCode);
        var ev = Svc.Events(_f, b.Seller, "booking.additional_charge_requested").Single().Data;
        Assert.Equal(30, (int)ev["extraMinutes"]!); Assert.Equal(3000, (int)ev["amountCents"]!);
    }

    // 4 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Customer_data_of_a_booking_that_was_never_confirmed_stays_hidden_even_after_a_cancellation()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (mid, id, _) = await Svc.Book(b, 10, 0);                                    // Requested
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/cancellations", new { reason = "x" })).StatusCode);
        var d = await Svc.Detail(b.Owner, id);
        Assert.Equal("CancelledByBuyer", (string)d["status"]!);
        Assert.False((bool)d["customer"]!["visible"]!); Assert.Null(d["customer"]!["phone"]); Assert.Null(d["customer"]!["address"]);
        Assert.DoesNotContain("1 Main St", d.ToJsonString());
        // a hold the buyer abandons
        var wed = Svc.Next(_f, DayOfWeek.Wednesday, 8);
        var hmid = Svc.BookingId();
        await Svc.Post(b, "/bookings/holds", Svc.Body(b, hmid, Svc.Local(b.Tz, wed, 10)));
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{hmid}/cancellations", new { reason = "x" })).StatusCode);
        var hid = _f.WithDb(db => db.Bookings.First(x => x.MarketplaceBookingId == hmid).Id);
        var h = await Svc.Detail(b.Owner, hid);
        Assert.False((bool)h["customer"]!["visible"]!); Assert.Null(h["customer"]!["phone"]); Assert.Null(h["customer"]!["address"]);
        // confirmed, then cancelled: still visible until redaction
        var (_, cid, _) = await Svc.Book(b, 14, 1);
        await Svc.Act(b.Owner, cid, "approve"); await Svc.Act(b.Owner, cid, "cancel", new { reason = "x" });
        Assert.True((bool)(await Svc.Detail(b.Owner, cid))["customer"]!["visible"]!);
    }

    // 5 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Managers_do_not_see_quote_addresses_and_viewers_do_not_see_intake_answers()
    {
        var q = await Svc.NewBiz(_f, d => { d["pricingModel"] = "quote"; d["price"] = null; d["minMinutes"] = null; d["maxMinutes"] = null; d["incrementMinutes"] = 60; });
        var created = await Svc.Post(q, "/quote-requests", new
        {
            marketplaceQuoteId = "Q-" + Guid.NewGuid().ToString("N")[..10], serviceId = q.Service, buyer = new { displayName = "Jane", phone = "+1 555 010 0000" },
            details = "Fix the sink", location = "customer", address = new { line1 = "1 Main St", postalCode = "11201" }
        });
        var qid = (string)(await Orders.Node(created))["quoteId"]!;
        var (mgr, _) = await TeamHelpers.JoinAs(_f, q.Owner, "Manager");
        var owner = await ProdApi.Node(await q.Owner.Get($"/api/v1/seller/quote-requests/{qid}"));
        Assert.Equal("1 Main St", (string)owner["address"]!["line1"]!);
        var m = await ProdApi.Node(await mgr.Get($"/api/v1/seller/quote-requests/{qid}"));
        Assert.Null(m["address"]); Assert.Null(m["phone"]);
        // intake answers on a pending request
        var b = await Svc.NewBiz(_f, d => { d["bookingMode"] = "request"; d["intake"] = new JsonArray(new JsonObject { ["id"] = "pets", ["label"] = "Any pets?", ["type"] = "yesno", ["required"] = false }); });
        var start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 10);
        var r = await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start, answers: [new { id = "pets", answer = "yes" }]));
        var id = Guid.Parse((string)(await Orders.Node(r))["booking"]!["bookingId"]!);
        var (viewer, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Viewer");
        var v = await Svc.Detail(viewer, id);
        Assert.Null(v["intake"]![0]!["answer"]);
        Assert.Equal("yes", (string)(await Svc.Detail(b.Owner, id))["intake"]![0]!["answer"]!);
    }

    // 6 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Suspended_sellers_take_no_new_bookings_or_quote_requests()
    {
        var b = await Svc.NewBiz(_f);
        var q = await Svc.NewBiz(_f, d => { d["pricingModel"] = "quote"; d["price"] = null; d["minMinutes"] = null; d["maxMinutes"] = null; d["incrementMinutes"] = 60; });
        var admin = new Api(_f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var start = Svc.Local(b.Tz, wed, 10);
        var held = Svc.BookingId();
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings/holds", Svc.Body(b, held, start))).StatusCode);       // works before the suspension
        foreach (var seller in new[] { b.Seller, q.Seller })
            Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/sellers/{seller}/status", new { status = "Suspended" }).Code());
        var avail = await _f.NewClient().SendAsync(Orders.Signed(_f, "GET", $"/api/internal/v1/services/{b.Service}/availability?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}&location=online", null));
        Assert.Equal(HttpStatusCode.NotFound, avail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Svc.Post(b, "/bookings/holds", Svc.Body(b, Svc.BookingId(), start.AddHours(3)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), start.AddHours(3)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Svc.Post(b, $"/bookings/{held}/confirm", Svc.Body(b, held, start))).StatusCode);   // an existing hold of a suspended seller can't be confirmed
        var quote = await Svc.Post(q, "/quote-requests", new { marketplaceQuoteId = "Q-s1", serviceId = q.Service, buyer = new { displayName = "J" }, details = "x", location = "online" });
        Assert.Equal(HttpStatusCode.NotFound, quote.StatusCode);
    }

    // 7 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_suspended_members_ics_feed_stops_and_members_with_upcoming_bookings_cannot_be_removed()
    {
        var b = await Svc.NewBiz(_f);
        var (mgr, email) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager");
        var mem = Svc.MemberOf(_f, b.Seller, email);
        await Svc.MakeProvider(b.Owner, mem); await Svc.SetHours(b.Owner, mem, Svc.WeekdayHours());
        await Svc.SetProviders(b.Owner, b.Service, b.OwnerMember, mem);
        var token = (string)(await ProdApi.Node(await mgr.Post($"/api/v1/seller/providers/{mem}/ics")))["token"]!;
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().GetAsync($"/api/calendar/feeds/{token}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Owner.Patch($"/api/v1/team/members/{mem}", new { status = "Suspended" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.NewClient().GetAsync($"/api/calendar/feeds/{token}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Owner.Patch($"/api/v1/team/members/{mem}", new { status = "Active" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().GetAsync($"/api/calendar/feeds/{token}.ics")).StatusCode);
        // a booking assigned to the member blocks removal (and leaving) until it is reassigned
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var r = await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 10), provider: mem));
        var id = Guid.Parse((string)(await Orders.Node(r))["booking"]!["bookingId"]!);
        var del = await b.Owner.Delete($"/api/v1/team/members/{mem}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode); Assert.Equal("has_upcoming_bookings", await del.ErrCode());
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.Post("/api/v1/team/leave")).StatusCode);
        Assert.True(_f.WithDb(db => db.Memberships.Any(m => m.Id == mem)));
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "reassign", new { providerId = b.OwnerMember })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.Owner.Delete($"/api/v1/team/members/{mem}")).StatusCode);
    }

    // 8 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task After_redaction_buyer_free_text_is_in_no_table()
    {
        var b = await Svc.NewBiz(_f);
        var (mid, id, _) = await Svc.Book(b);
        const string secret = "door-code-7731";
        await Svc.Post(b, $"/bookings/{mid}/messages", new { messageId = "M-" + Guid.NewGuid().ToString("N")[..8], text = "my " + secret });
        await b.Owner.Post($"/api/v1/seller/bookings/{id}/messages", new { text = "ok, " + secret });
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/cancellations", new { reason = "sick, " + secret })).StatusCode);
        // deliver the events first: pending ones must stay intact for the marketplace
        for (var i = 0; i < 40 && _f.WithDb(db => db.Outbox.Any(o => o.AggregateId == id.ToString() && o.Status == OutboxStatus.Pending)); i++)
            await _f.NewClient().PostAsync("/api/dev/outbox/dispatch", null);
        _f.Time.Advance(TimeSpan.FromDays(31));
        await RunJobs();
        var dump = _f.WithDb(db => string.Join("\n",
            db.Bookings.Where(x => x.Id == id).Select(x => x.CancelReason + x.DeclineReason + x.BuyerName).ToList()
                .Concat(db.BookingEvents.Where(x => x.BookingId == id).Select(x => x.Text).ToList())
                .Concat(db.BookingMessages.Where(x => x.BookingId == id).Select(x => x.Text).ToList())
                .Concat(db.Outbox.Where(x => x.AggregateId == id.ToString()).Select(x => x.PayloadJson).ToList())));
        Assert.DoesNotContain(secret, dump);
        Assert.Contains("redacted", dump);
    }

    [Fact]
    public async Task Quote_free_text_is_redacted_too()
    {
        var q = await Svc.NewBiz(_f, d => { d["pricingModel"] = "quote"; d["price"] = null; d["minMinutes"] = null; d["maxMinutes"] = null; d["incrementMinutes"] = 60; });
        var created = await Svc.Post(q, "/quote-requests", new { marketplaceQuoteId = "Q-" + Guid.NewGuid().ToString("N")[..10], serviceId = q.Service, buyer = new { displayName = "J" }, details = "x", location = "online" });
        var id = Guid.Parse((string)(await Orders.Node(created))["quoteId"]!);
        await q.Owner.Post($"/api/v1/seller/quote-requests/{id}/decline", new { reason = "call me on 555-0100-secret" });
        for (var i = 0; i < 40 && _f.WithDb(db => db.Outbox.Any(o => o.AggregateId == id.ToString() && o.Status == OutboxStatus.Pending)); i++)
            await _f.NewClient().PostAsync("/api/dev/outbox/dispatch", null);
        _f.Time.Advance(TimeSpan.FromDays(31)); await RunJobs();
        var dump = _f.WithDb(db => string.Join("\n", db.QuoteRequests.Where(x => x.Id == id).Select(x => x.DeclineReason + x.QuoteNote).ToList().Concat(db.Outbox.Where(x => x.AggregateId == id.ToString()).Select(x => x.PayloadJson).ToList())));
        Assert.DoesNotContain("555-0100-secret", dump);
    }

    // 9 ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Parallel_confirms_of_one_hold_all_answer_ok_with_the_same_booking_and_one_event()
    {
        var b = await Svc.NewBiz(_f);
        var mid = Svc.BookingId(); var start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 10);
        Assert.Equal(HttpStatusCode.Created, (await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start))).StatusCode);
        var res = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            var r = await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start));
            return (r.StatusCode, Id: (string?)(await Orders.Node(r))["booking"]?["bookingId"]);
        })));
        Assert.All(res, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        Assert.Single(res.Select(x => x.Id).Distinct());
        Assert.Equal(1, _f.WithDb(db => db.Bookings.Count(x => x.MarketplaceBookingId == mid)));
        Assert.Single(Svc.Events(_f, b.Seller, "booking.confirmed"));
    }

    // extra ------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_availability_rate_limit_is_per_service_not_per_client_address()
    {
        // the policy key is the service id: two services never share a bucket (the marketplace is one caller for every shopper)
        var f = new SvcFactory(); f.Extra["RateLimit:AvailabilityPerMinute"] = "3";
        using var _ = f;
        var b = await Svc.NewBiz(f);
        var b2 = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday);
        async Task<HttpStatusCode> Ask(Biz x) => (await f.NewClient().SendAsync(Orders.Signed(f, "GET", $"/api/internal/v1/services/{x.Service}/availability?from={Svc.Iso(wed)}&to={Svc.Iso(wed.AddDays(1))}&location=online", null))).StatusCode;
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add(await Ask(b));
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.OK, await Ask(b2));
    }
}
