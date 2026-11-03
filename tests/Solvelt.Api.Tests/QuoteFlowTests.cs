using System.Net;
using System.Text.Json.Nodes;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class QuoteFlowTests : IClassFixture<SvcFactory>
{
    private readonly SvcFactory _f;
    public QuoteFlowTests(SvcFactory f) => _f = f;

    private Task<Biz> QuoteBiz() => Svc.NewBiz(_f, d => { d["pricingModel"] = "quote"; d["price"] = null; d["minMinutes"] = null; d["maxMinutes"] = null; d["incrementMinutes"] = 60; });

    private static object QuoteBody(Biz b, string qid, string location = "customer", string postal = "11201") => new
    {
        marketplaceQuoteId = qid, serviceId = b.Service, buyer = new { displayName = "Jane Buyer", relayEmail = "relay-j@relay.solvelt.example", phone = "+1 555 010 0000" },
        details = "The kitchen sink leaks and the tap is loose.", location, address = new { line1 = "1 Main St", city = "Brooklyn", postalCode = postal, country = "US" }
    };

    private async Task<(string Qid, Guid Id)> Request(Biz b)
    {
        var qid = "Q-" + Guid.NewGuid().ToString("N")[..10];
        var r = await Svc.Post(b, "/quote-requests", QuoteBody(b, qid));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (qid, Guid.Parse((string)(await Orders.Node(r))["quoteId"]!));
    }

    private object Quote(Biz b, DateTime[] slots, decimal price = 250m, int minutes = 180, int expiresInHours = 48) =>
        new { price, durationMinutes = minutes, slots, providerId = b.OwnerMember, expiresAt = _f.Time.GetUtcNow().UtcDateTime.AddHours(expiresInHours), note = "Includes parts." };

    [Fact]
    public async Task A_quote_request_reaches_the_inbox_the_seller_sends_a_quote_and_the_buyer_accepts()
    {
        var b = await QuoteBiz();
        var (qid, id) = await Request(b);
        // appears in the seller's requests inbox and in the badge count
        var list = await ProdApi.Node(await b.Owner.Get("/api/v1/seller/quote-requests?status=requested"));
        Assert.Contains(list["items"]!.AsArray(), i => (string)i!["id"]! == id.ToString());
        Assert.Equal(1, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings/counts")))["quoteRequests"]!);
        var d = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/quote-requests/{id}"));
        Assert.Equal("The kitchen sink leaks and the tap is loose.", (string)d["details"]!);
        Assert.Equal("1 Main St", (string)d["address"]!["line1"]!);
        // idempotent on the marketplace quote id
        var again = await Svc.Post(b, "/quote-requests", QuoteBody(b, qid));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); Assert.False((bool)(await Orders.Node(again))["created"]!);

        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var s1 = Svc.Local(b.Tz, wed, 10); var s2 = Svc.Local(b.Tz, wed, 14);
        var sent = await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/quote", Quote(b, [s1, s2]));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var ev = Svc.Events(_f, b.Seller, "quote.sent").Single().Data;
        Assert.Equal(25000, (int)ev["priceCents"]!); Assert.Equal(180, (int)ev["durationMinutes"]!); Assert.Equal(2, ev["slots"]!.AsArray().Count); Assert.Equal("Includes parts.", (string)ev["note"]!);
        Assert.Equal(0, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings/counts")))["quoteRequests"]!);

        // the buyer accepts one of the proposed slots: booking created, slot reserved
        var mid = Svc.BookingId();
        var acc = await Svc.Post(b, $"/quotes/{qid}/accept", new { marketplaceBookingId = mid, start = s2 });
        Assert.Equal(HttpStatusCode.Created, acc.StatusCode);
        var booking = (await Orders.Node(acc))["booking"]!;
        Assert.Equal("Confirmed", (string)booking["status"]!);
        Assert.Equal(25000 + 1000, (int)booking["totalCents"]!);              // quoted price + travel fee
        Assert.Equal(180, (int)booking["durationMinutes"]!);
        var q = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/quote-requests/{id}"));
        Assert.Equal("Accepted", (string)q["status"]!); Assert.Equal((string)booking["bookingId"]!, (string)q["bookingId"]!);
        // the slot is reserved: another quote cannot offer it any more
        var (_, id2) = await Request(b);
        var clash = await b.Owner.Post($"/api/v1/seller/quote-requests/{id2}/quote", Quote(b, [s2.AddHours(1)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode); Assert.Equal("slot_unavailable", await clash.ErrCode());
        // accepting again with the same booking id is idempotent, with another it is refused
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/quotes/{qid}/accept", new { marketplaceBookingId = mid, start = s2 })).StatusCode);
        var other = await Svc.Post(b, $"/quotes/{qid}/accept", new { marketplaceBookingId = Svc.BookingId(), start = s1 });
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode); Assert.Equal("quote_not_open", await other.ErrCode());
        // it shows up in Bookings and the seller can run the normal lifecycle on it
        Assert.Equal(1, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/bookings?tab=upcoming")))["total"]!);
    }

    [Fact]
    public async Task Quotes_are_validated_and_slots_must_be_free()
    {
        var b = await QuoteBiz();
        var (qid, id) = await Request(b);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var ok = Svc.Local(b.Tz, wed, 10);
        async Task<(HttpStatusCode, string?)> Try(object body) { var r = await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/quote", body); return (r.StatusCode, await r.ErrCode()); }
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "price"), await Try(Quote(b, [ok], price: -1)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "duration"), await Try(Quote(b, [ok], minutes: 2)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "slots"), await Try(Quote(b, [])));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "expires"), await Try(Quote(b, [ok], expiresInHours: -1)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "expires"), await Try(Quote(b, [ok], expiresInHours: 24 * 90)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "slot_unavailable"), await Try(Quote(b, [Svc.Local(b.Tz, wed, 22)])));          // after hours
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "provider"), await Try(new { price = 100m, durationMinutes = 60, slots = new[] { ok }, providerId = Guid.NewGuid(), expiresAt = DateTime.UtcNow.AddDays(2) }));
        Assert.Equal(HttpStatusCode.Created, (await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/blocks", new { kind = "busy", start = ok, end = ok.AddHours(1) })).StatusCode);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "slot_unavailable"), await Try(Quote(b, [ok])));
        // a quote can be re-sent while it is still open
        Assert.Equal(HttpStatusCode.OK, (await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/quote", Quote(b, [Svc.Local(b.Tz, wed, 14)]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/quote", Quote(b, [Svc.Local(b.Tz, wed, 13)], price: 300m))).StatusCode);
        // a slot that wasn't offered cannot be accepted
        var bad = await Svc.Post(b, $"/quotes/{qid}/accept", new { marketplaceBookingId = Svc.BookingId(), start = Svc.Local(b.Tz, wed, 14) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode); Assert.Equal("slot_not_offered", await bad.ErrCode());
    }

    [Fact]
    public async Task Expired_quotes_cannot_be_accepted_and_are_closed_by_the_job()
    {
        var b = await QuoteBiz();
        var (qid, id) = await Request(b);
        var slot = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday, 10), 10);
        Assert.Equal(HttpStatusCode.OK, (await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/quote", Quote(b, [slot], expiresInHours: 5))).StatusCode);
        _f.Time.Advance(TimeSpan.FromHours(6));
        var late = await Svc.Post(b, $"/quotes/{qid}/accept", new { marketplaceBookingId = Svc.BookingId(), start = slot });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); Assert.Equal("quote_expired", await late.ErrCode());
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().PostAsync("/api/dev/bookings/maintenance/run", null)).StatusCode);
        Assert.Equal("Expired", (string)(await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/quote-requests/{id}")))["status"]!);
        Assert.Single(Svc.Events(_f, b.Seller, "quote.expired"));
        Assert.Equal(HttpStatusCode.Conflict, (await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/quote", Quote(b, [slot]))).StatusCode);
    }

    [Fact]
    public async Task Declining_works_for_both_sides()
    {
        var b = await QuoteBiz();
        var (_, id) = await Request(b);
        var d = await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/decline", new { reason = "Out of area" });
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.Equal("Declined", (string)(await ProdApi.Node(d))["status"]!);
        Assert.Equal("Out of area", (string)Svc.Events(_f, b.Seller, "quote.declined").Single().Data["reason"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/decline", new { })).StatusCode);
        // buyer declines a sent quote
        var (qid2, id2) = await Request(b);
        await b.Owner.Post($"/api/v1/seller/quote-requests/{id2}/quote", Quote(b, [Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 10)]));
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/quotes/{qid2}/decline", new { reason = "too expensive" })).StatusCode);
        Assert.Equal("Withdrawn", (string)(await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/quote-requests/{id2}")))["status"]!);
        var late = await Svc.Post(b, $"/quotes/{qid2}/accept", new { marketplaceBookingId = Svc.BookingId(), start = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 10) });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Svc.Post(b, "/quotes/ghost/accept", new { marketplaceBookingId = "x", start = DateTime.UtcNow })).StatusCode);
    }

    [Fact]
    public async Task Quote_requests_are_only_for_quote_services_and_must_be_in_the_service_area()
    {
        var q = await QuoteBiz();
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var direct = await Svc.Post(q, "/bookings", Svc.Body(q, Svc.BookingId(), Svc.Local(q.Tz, wed, 10)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, direct.StatusCode); Assert.Equal("quote_required", await direct.ErrCode());
        var outside = await Svc.Post(q, "/quote-requests", QuoteBody(q, "Q-" + Guid.NewGuid().ToString("N")[..8], postal: "99999"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outside.StatusCode); Assert.Equal("outside_service_area", await outside.ErrCode());
        var noDetails = await Svc.Post(q, "/quote-requests", new { marketplaceQuoteId = "Q-nodetails", serviceId = q.Service, buyer = new { displayName = "J" }, details = "", location = "online" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noDetails.StatusCode);
        var hourly = await Svc.NewBiz(_f);
        var wrong = await Svc.Post(hourly, "/quote-requests", QuoteBody(hourly, "Q-wrong"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode); Assert.Equal("not_quote_service", await wrong.ErrCode());
        // unsigned
        var unsigned = await _f.NewClient().SendAsync(Orders.Signed(_f, "POST", "/api/internal/v1/quote-requests", QuoteBody(q, "Q-x"), sign: false));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    [Fact]
    public async Task Quotes_are_scoped_to_the_account_the_brand_and_the_role()
    {
        var b = await QuoteBiz();
        var (_, id) = await Request(b);
        var (other, _) = await ProdApi.Approved(_f);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get($"/api/v1/seller/quote-requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post($"/api/v1/seller/quote-requests/{id}/decline", new { })).StatusCode);
        Assert.Equal(0, (int)(await ProdApi.Node(await other.Get("/api/v1/seller/quote-requests")))["total"]!);
        var brand2 = await TeamHelpers.NewBrand(b.Owner, "Other brand");
        var (mgr2, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager", [brand2]);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr2.Get($"/api/v1/seller/quote-requests/{id}")).StatusCode);
        var (viewer, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get($"/api/v1/seller/quote-requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/seller/quote-requests/{id}/decline", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/seller/quote-requests/{id}/quote", new { })).StatusCode);
    }

    [Fact]
    public async Task Quote_requests_are_redacted_after_the_retention_period()
    {
        var b = await QuoteBiz();
        var (_, id) = await Request(b);
        await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/decline", new { });
        _f.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().PostAsync("/api/dev/bookings/maintenance/run", null)).StatusCode);
        var d = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/quote-requests/{id}"));
        Assert.Null(d["details"]); Assert.Null(d["buyerName"]); Assert.Null(d["address"]);
        Assert.Equal("Redacted", _f.WithDb(db => db.QuoteRequests.First(x => x.Id == id).BuyerName));
    }

    [Fact]
    public async Task The_simulator_requests_quotes_and_accepts_them()
    {
        var b = await QuoteBiz();
        var sim = await b.Owner.Post("/api/v1/seller/dev/quotes", new { serviceId = b.Service, details = "Fix my fence", location = "customer", postalCode = "11201" });
        Assert.Equal(HttpStatusCode.Created, sim.StatusCode);
        var node = await ProdApi.Node(sim);
        var id = Guid.Parse((string)node["quoteId"]!);
        var mq = _f.WithDb(db => db.QuoteRequests.First(x => x.Id == id).MarketplaceQuoteId);
        var slot = Svc.Local(b.Tz, Svc.Next(_f, DayOfWeek.Wednesday), 11);
        Assert.Equal(HttpStatusCode.OK, (await b.Owner.Post($"/api/v1/seller/quote-requests/{id}/quote", Quote(b, [slot]))).StatusCode);
        var acc = await b.Owner.Post($"/api/v1/seller/dev/quotes/{mq}/accept", new { start = slot });
        Assert.Equal(HttpStatusCode.Created, acc.StatusCode);
        var (other, _) = await ProdApi.Approved(_f);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post($"/api/v1/seller/dev/quotes/{mq}/decline", new { })).StatusCode);
    }
}
