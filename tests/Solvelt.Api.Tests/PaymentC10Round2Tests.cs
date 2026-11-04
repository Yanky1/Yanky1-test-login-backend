using System.Net;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

/// <summary>Second verification round of C10: cross-order Stripe events in the multi-seller branch, booking and lower-case currencies, exact dispute ids in flags.</summary>
public class PaymentC10Round2Tests
{
    private sealed record Split(Shop A, Shop B, string Mid, Guid IdA, string NumA, Guid IdB, string NumB);

    /// <summary>One marketplace order, two seller orders (sales 2400 and 3500), captured under charge ch_{mid}.</summary>
    private static async Task<Split> TwoSellers(ApiFactory f)
    {
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        var mid = Orders.Mid();
        var body = Orders.OrderBody(f, mid, [Orders.Line(a.Seller, a.Brand, a.Product, 1, 2000, "LA"), Orders.Line(b.Seller, b.Brand, b.Product, 1, 3000, "LB")],
            [new { sellerId = a.Seller, brandId = a.Brand, amountCents = 400 }, new { sellerId = b.Seller, brandId = b.Brand, amountCents = 500 }]);
        Assert.Equal(HttpStatusCode.Created, (await Orders.Inbound(f, "/api/internal/v1/orders", body)).StatusCode);
        var (ia, na) = f.WithDb(db => db.Orders.AsNoTracking().Where(o => o.MarketplaceOrderId == mid && o.SellerId == a.Seller).Select(o => new ValueTuple<Guid, string>(o.Id, o.Number)).First());
        var (ib, nb) = f.WithDb(db => db.Orders.AsNoTracking().Where(o => o.MarketplaceOrderId == mid && o.SellerId == b.Seller).Select(o => new ValueTuple<Guid, string>(o.Id, o.Number)).First());
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, mid)).StatusCode);
        return new(a, b, mid, ia, na, ib, nb);
    }

    private static Task<HttpResponseMessage> Share(ApiFactory f, string dp, Split s, bool a, long amount) =>
        Pay.Event(f, "dispute.created", new { disputeId = dp, marketplaceOrderId = s.Mid, sellerOrderNumber = a ? s.NumA : s.NumB, amountCents = amount, feeCents = 1500L });

    private static object StripeDp(string id, string charge, long amount, string status = "needs_response") => new { id, charge, amount, currency = "usd", status, reason = "fraudulent" };
    private static List<PaymentDispute> Rows(ApiFactory f, string dp) => f.WithDb(db => db.PaymentDisputes.AsNoTracking().Where(d => d.ExternalId == dp).ToList());
    private static List<ReconciliationFlag> Flags(ApiFactory f, string kind) => f.WithDb(db => db.ReconciliationFlags.AsNoTracking().Where(x => x.Kind == kind).ToList());
    private static UnallocatedDispute? Item(ApiFactory f, string dp) => f.WithDb(db => db.UnallocatedDisputes.AsNoTracking().FirstOrDefault(u => u.ExternalId == dp));
    private static long Journals(ApiFactory f, params Guid[] sellers) => f.WithDb(db => db.LedgerJournals.Count(j => sellers.Contains(j.SellerId) && (j.Type == "dispute" || j.Type == "dispute_fee" || j.Type == "dispute_won")));

    // 1 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_Stripe_event_on_another_marketplace_orders_charge_never_changes_the_rows_of_a_split_dispute()
    {
        using var f = new PayFactory();
        var m1 = await TwoSellers(f); var m2 = await TwoSellers(f);
        await Share(f, "dp_ra", m1, true, 2400); await Share(f, "dp_ra", m1, false, 3500);
        var before = Journals(f, m1.A.Seller, m1.B.Seller);
        // Stripe reports dp_ra "lost" on the charge of ANOTHER marketplace order (M2)
        var r = await Pay.Hook(f, "charge.dispute.closed", StripeDp("dp_ra", "ch_" + m2.Mid, 5900, "lost"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 10);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.All(Rows(f, "dp_ra"), x => Assert.Equal("needs_response", x.Status));                  // the rows of M1 are untouched
        Assert.Null(Item(f, "dp_ra"));                                                                // nothing was recorded toward an allocation
        var flag = Assert.Single(Flags(f, "dispute_unattributed"));
        Assert.Null(flag.ResolvedAt); Assert.Contains("dispute dp_ra ", flag.Detail);
        Assert.Equal(before, Journals(f, m1.A.Seller, m1.B.Seller));                                  // and no money moved
        Assert.Equal(0, Journals(f, m2.A.Seller, m2.B.Seller));
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_ra", "ch_" + m2.Mid, 5900, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 20);
        Assert.Single(Flags(f, "dispute_unattributed"));                                              // a later delivery does not raise it again
        Assert.All(Rows(f, "dp_ra"), x => Assert.Equal("needs_response", x.Status));
    }

    [Fact]
    public async Task A_Stripe_event_on_a_two_seller_charge_does_not_touch_a_single_sellers_dispute_or_count_it_as_allocated()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o1 = await Pay.Paid(s);                                                                    // M1: one seller
        var m3 = await TwoSellers(f);                                                                  // M3: two sellers
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", new { disputeId = "dp_rb", marketplaceOrderId = o1.Mid, sellerOrderNumber = o1.Number, amountCents = 1000L, feeCents = 1500L })).StatusCode);
        var r = await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_rb", "ch_" + m3.Mid, 5900, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 10);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("needs_response", Assert.Single(Rows(f, "dp_rb")).Status);                        // M1's row did not become under_review
        Assert.Null(Item(f, "dp_rb"));                                                                // no unallocated item that counts M1's 1000
        Assert.Single(Flags(f, "dispute_unattributed"));
        Assert.Equal(0, Journals(f, m3.A.Seller, m3.B.Seller));
    }

    [Fact]
    public async Task A_charge_the_portal_does_not_know_cannot_contradict_the_rows_so_the_dispute_id_decides()
    {
        using var f = new PayFactory();
        var m1 = await TwoSellers(f);
        await Share(f, "dp_rc", m1, true, 2400); await Share(f, "dp_rc", m1, false, 3500);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_rc", "ch_never_captured", 5900, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 10)).StatusCode);
        Assert.All(Rows(f, "dp_rc"), x => Assert.Equal("under_review", x.Status));
        Assert.Empty(Flags(f, "dispute_unattributed"));
    }

    // 2 ------------------------------------------------------------------------------------------------------------------------------------------
    private static async Task<(Shop S, string Mb)> CapturedBooking(ApiFactory f)
    {
        var s = await Orders.NewShop(f);
        var bid = Guid.NewGuid(); var mb = "MB-" + Guid.NewGuid().ToString("N")[..10]; var now = f.Time.GetUtcNow().UtcDateTime;
        f.WithDb(db => { db.Bookings.Add(new Booking { Id = bid, SellerId = s.Seller, BrandId = s.Brand, ProductId = s.Product, Number = "BK-" + mb[3..9], MarketplaceBookingId = mb, Status = BookingStatus.Completed, TotalCents = 10000, CompletedAt = now, UpdatedAt = now }); db.SaveChanges(); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "booking", marketplaceBookingId = mb, paymentRef = "ch_" + mb, currency = "USD" })).StatusCode);
        return (s, mb);
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("eur")]
    public async Task A_booking_dispute_in_another_currency_than_the_booking_is_rejected(string currency)
    {
        using var f = new PayFactory();
        var (s, mb) = await CapturedBooking(f);
        var r = await Pay.Event(f, "dispute.created", new { disputeId = "dp_bk", marketplaceBookingId = mb, amountCents = 1000L, feeCents = 1500L, currency });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("currency_mismatch", await Orders.Code(r));
        Assert.Empty(Rows(f, "dp_bk")); Assert.Equal(0, Journals(f, s.Seller));
        // the booking's own currency (any case) and an omitted currency are fine, and are booked in USD
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", new { disputeId = "dp_bk", marketplaceBookingId = mb, amountCents = 1000L, feeCents = 1500L, currency = "usd" })).StatusCode);
        Assert.Equal("USD", Assert.Single(Rows(f, "dp_bk")).Currency);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.updated", new { disputeId = "dp_bk", marketplaceBookingId = mb, status = "under_review" })).StatusCode);
    }

    // 3 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_lower_case_currency_is_compared_like_any_other_and_a_malformed_one_is_a_422()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        object Dispute(string id, string? cur) => new { disputeId = id, marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 1000L, feeCents = 1500L, currency = cur };
        var eur = await Pay.Event(f, "dispute.created", Dispute("dp_lc", "eur"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "currency_mismatch"), (eur.StatusCode, await Orders.Code(eur)));
        Assert.Empty(Rows(f, "dp_lc"));
        foreach (var bad in new[] { "EU", "12$", "euro", " " + "e1r" })
        {
            var r = await Pay.Event(f, "dispute.created", Dispute("dp_lc", bad));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_currency"), (r.StatusCode, await Orders.Code(r)));
        }
        Assert.Empty(Rows(f, "dp_lc"));
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", Dispute("dp_lc", " usd "))).StatusCode);   // the order's currency, any case, padded
        Assert.Equal("USD", Assert.Single(Rows(f, "dp_lc")).Currency);

        // refunds
        object Refund(string id, string? cur) => new { refundId = id, marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 100L, currency = cur };
        var refEur = await Pay.Event(f, "refund.processed", Refund("re_lc", "eur"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "currency_mismatch"), (refEur.StatusCode, await Orders.Code(refEur)));
        var refBad = await Pay.Event(f, "refund.processed", Refund("re_lc", "x"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_currency"), (refBad.StatusCode, await Orders.Code(refBad)));
        Assert.Equal(0, f.WithDb(db => db.LedgerJournals.Count(j => j.Type == "refund")));
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "refund.processed", Refund("re_lc", "usd"))).StatusCode);
        Assert.Equal(1, f.WithDb(db => db.LedgerJournals.Count(j => j.Type == "refund")));
    }

    [Fact]
    public async Task A_lower_case_currency_on_a_capture_is_compared_with_the_order_and_a_booking_capture_normalises_it()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var (mid, _, number, _) = await Orders.Place(s);
        var eur = await Pay.Event(f, "payment.captured", new { kind = "order", marketplaceOrderId = mid, sellerOrderNumber = number, paymentRef = "ch_lc", currency = "eur" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "currency_mismatch"), (eur.StatusCode, await Orders.Code(eur)));
        Assert.Equal(0, f.WithDb(db => db.LedgerJournals.Count(j => j.Type == "sale")));
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "order", marketplaceOrderId = mid, sellerOrderNumber = number, paymentRef = "ch_lc", currency = "usd" })).StatusCode);
        // a booking capture accepts "usd" and books it as USD
        var b = await Orders.NewShop(f);
        var bid = Guid.NewGuid(); var mb = "MB-" + Guid.NewGuid().ToString("N")[..10]; var now = f.Time.GetUtcNow().UtcDateTime;
        f.WithDb(db => { db.Bookings.Add(new Booking { Id = bid, SellerId = b.Seller, BrandId = b.Brand, ProductId = b.Product, Number = "BK-" + mb[3..9], MarketplaceBookingId = mb, Status = BookingStatus.Completed, TotalCents = 10000, CompletedAt = now, UpdatedAt = now }); db.SaveChanges(); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "booking", marketplaceBookingId = mb, paymentRef = "ch_bk", currency = "usd" })).StatusCode);
        Assert.Equal("USD", f.WithDb(db => db.LedgerJournals.Single(j => j.BookingId == bid && j.Type == "sale").Currency));
        var none = await Pay.Event(f, "payment.captured", new { kind = "booking", marketplaceBookingId = mb, paymentRef = "ch_bk2", currency = (string?)null });
        Assert.Equal("invalid_currency", await Orders.Code(none));                                    // still required for bookings
    }

    // minor --------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_flag_for_dp_12_does_not_suppress_the_flag_for_dp_1()
    {
        using var f = new PayFactory();
        // Stripe disputes on a charge the portal does not know and without any allocation: one unattributed flag per dispute id
        await Pay.Hook(f, "charge.dispute.created", StripeDp("dp_12", "ch_nobody", 100));
        await Pay.Hook(f, "charge.dispute.created", StripeDp("dp_1", "ch_nobody", 100));
        Assert.Equal(2, Flags(f, "dispute_unattributed").Count);
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_1", "ch_nobody", 100, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_12", "ch_nobody", 100, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        Assert.Equal(2, Flags(f, "dispute_unattributed").Count);                                      // and replays still raise nothing new
    }

    [Fact]
    public async Task The_won_unallocated_flag_of_dp_w12_does_not_suppress_the_one_of_dp_w1()
    {
        using var f = new PayFactory();
        var m1 = await TwoSellers(f); var m2 = await TwoSellers(f);
        await Pay.Hook(f, "charge.dispute.closed", StripeDp("dp_w12", "ch_" + m1.Mid, 5900, "won"));
        Pay.Age(f, 2);
        await Pay.Reconcile(f);
        Assert.Single(Flags(f, "dispute_won_unallocated"));
        await Pay.Hook(f, "charge.dispute.closed", StripeDp("dp_w1", "ch_" + m2.Mid, 5900, "won"), created: f.Time.GetUtcNow().ToUnixTimeSeconds());
        Pay.Age(f, 2);
        await Pay.Reconcile(f);
        var flags = Flags(f, "dispute_won_unallocated");
        Assert.Equal(2, flags.Count);
        Assert.Contains(flags, x => x.Detail.Contains("dispute dp_w1 "));
        Assert.Contains(flags, x => x.Detail.Contains("dispute dp_w12 "));
    }
}
