using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

public class PaymentLedgerTests
{
    [Fact]
    public async Task Capture_posts_a_balanced_sale_journal_pending_equals_net()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        var r = await Pay.Capture(f, o.Mid, o.Number);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var sale = Assert.Single(Pay.Journals(f, s.Seller, "sale"));
        Assert.Equal(0, sale.Entries.Sum(e => e.AmountCents));
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal(2299, b.Pending);          // 1999 items + 500 shipping - 200 fee (round half up of 199.9)
        Assert.Equal(0, b.Available);
        Assert.Equal(200, Pay.Sum(f, s.Seller, LedgerAccount.Fees));
        Assert.Equal(-2499, Pay.Sum(f, s.Seller, LedgerAccount.Platform));
        Assert.All(sale.Entries, e => Assert.Equal("USD", e.Currency));
    }

    [Fact]
    public async Task Duplicate_event_and_a_second_capture_of_the_same_order_create_nothing_new()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, o.Mid, o.Number, eventId: "ev-1")).StatusCode);
        var dup = await Pay.Json(await Pay.Capture(f, o.Mid, o.Number, eventId: "ev-1"));
        Assert.Equal("duplicate", (string)dup["status"]!);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, o.Mid, o.Number, eventId: "ev-2")).StatusCode);   // different event id, same order
        Assert.Single(Pay.Journals(f, s.Seller));
        Assert.Equal(2299, (await Pay.Bal(f, s.Seller)).Pending);
    }

    [Fact]
    public async Task Capture_validation_unknown_order_bad_kind_bad_signature()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var nf = await Pay.Capture(f, "MP-NOPE");
        Assert.Equal(HttpStatusCode.NotFound, nf.StatusCode);
        Assert.Equal("order_not_found", await Orders.Code(nf));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Pay.Event(f, "payment.captured", new { kind = "nonsense", paymentRef = "ch_1" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Pay.Event(f, "payment.captured", new { kind = "order", marketplaceOrderId = "x" })).StatusCode); // no paymentRef
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Pay.Event(f, "nope.nope", new { })).StatusCode);
        var unsigned = await f.NewClient().SendAsync(Orders.Signed(f, "POST", "/api/internal/v1/payment-events", new { eventId = "e", type = "payment.captured", data = new { } }, sign: false));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var wrong = await Orders.Inbound(f, "/api/internal/v1/payment-events", new { eventId = "e", type = "payment.captured", data = new { } }, secret: "not-the-secret-0123456789abcdef");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(Pay.Journals(f, s.Seller));
        // a seller bearer token opens nothing here
        var bearer = new HttpRequestMessage(HttpMethod.Post, "/api/internal/v1/payment-events");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(bearer)).StatusCode);
    }

    [Fact]
    public async Task Partial_refund_before_release_reduces_pending_fee_is_returned_proportionally_last_refund_takes_the_remainder()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, o.Mid, o.Number, 1000)).StatusCode);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal(2299 - 900, b.Pending);     // 1000 refunded minus 100 fee returned (200 * 1000 / 1999 = 100.05)
        Assert.Equal(0, b.Available);
        Assert.Equal(100, Pay.Sum(f, s.Seller, LedgerAccount.Fees));

        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, o.Mid, o.Number, 999)).StatusCode);
        Assert.Equal(0, Pay.Sum(f, s.Seller, LedgerAccount.Fees));            // all 200 returned in total, never more
        Assert.Equal(500, (await Pay.Bal(f, s.Seller)).Pending);              // the shipping charge is still pending
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, o.Mid, o.Number, 0, shipping: 500)).StatusCode);
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Pending);
        Assert.All(Pay.Journals(f, s.Seller), j => Assert.Equal(0, j.Entries.Sum(e => e.AmountCents)));
    }

    [Fact]
    public async Task Refund_after_release_comes_out_of_available_duplicates_and_over_refunds_are_refused()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        Pay.Age(f, 8); await Pay.Release(f);
        Assert.Equal(2299, (await Pay.Bal(f, s.Seller)).Available);

        var re = await Pay.Refund(f, o.Mid, o.Number, 1999, refundId: "re_full");
        Assert.Equal(HttpStatusCode.OK, re.StatusCode);
        Assert.Equal(2299 - 1799, (await Pay.Bal(f, s.Seller)).Available);       // 1999 refunded minus the 200 fee returned; shipping not refunded yet
        var dup = await Pay.Refund(f, o.Mid, o.Number, 1999, refundId: "re_full");  // same refund id, new event id: still nothing new
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Equal(500, (await Pay.Bal(f, s.Seller)).Available);
        var over = await Pay.Refund(f, o.Mid, o.Number, 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal("exceeds_sale", await Orders.Code(over));
    }

    [Fact]
    public async Task Refund_needs_a_recorded_sale_and_a_clear_order()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        var r = await Pay.Refund(f, o.Mid, o.Number, 100);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("sale_not_recorded", await Orders.Code(r));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Pay.Refund(f, o.Mid, o.Number, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Pay.Refund(f, "MP-NOPE", "SO-1", 100)).StatusCode);
    }

    [Fact]
    public async Task Negative_balance_after_a_refund_of_transferred_money_is_recovered_from_the_next_sale_and_the_owner_is_told()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        var a = await Pay.Paid(s);
        Pay.Age(f, 8); await Pay.Release(f);
        await Pay.Transfers(f);
        Assert.Equal(2299, (await Pay.Bal(f, s.Seller)).Settled);

        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, a.Mid, a.Number, 1999, 500)).StatusCode);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal(-2299, b.Available);
        Assert.True(b.Negative);
        var mails = Pay.Mails(f, Pay.OwnerEmail(f, s.Seller));
        Assert.Contains(mails, m => m.Subject.Contains("negative"));
        await Pay.Relog(s);
        var summary = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/summary"));
        Assert.True((bool)summary["balances"]![0]!["negative"]!);

        // a new sale: while the available balance is not positive nothing is transferred; released funds first repay the debt
        await Pay.Paid(s, price: 3499, shipping: 0);   // net 3499 - 350 = 3149
        Assert.Equal(0, (await Pay.Transfers(f)).Created);
        Pay.Age(f, 8); await Pay.Release(f);
        Assert.Equal(-2299 + 3149, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Equal(1, (await Pay.Transfers(f)).Created);
        Assert.Equal(850, f.WithDb(db => db.PaymentTransfers.OrderBy(x => x.CreatedAt).Last().AmountCents));   // only what is left after the debt
    }

    [Fact]
    public async Task Booking_sale_policy_fee_and_extra_charge_are_ledgered_and_capped()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var bid = Guid.NewGuid(); var mb = "MB-" + Guid.NewGuid().ToString("N")[..8];
        f.WithDb(db => { db.Bookings.Add(new Booking { Id = bid, SellerId = s.Seller, BrandId = s.Brand, ProductId = s.Product, Number = "BK-1", MarketplaceBookingId = mb, Status = BookingStatus.Completed, TotalCents = 10000, CancellationFeeCents = 2000, AdditionalChargeCents = 3000, CompletedAt = f.Time.GetUtcNow().UtcDateTime, UpdatedAt = f.Time.GetUtcNow().UtcDateTime }); db.SaveChanges(); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "booking", marketplaceBookingId = mb, paymentRef = "ch_b", currency = "USD" })).StatusCode);
        Assert.Equal(9000, (await Pay.Bal(f, s.Seller)).Pending);   // 10000 - 10% fee
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "booking_extra_charge", marketplaceBookingId = mb, paymentRef = "ch_x", currency = "USD", amountCents = 2000 })).StatusCode);
        Assert.Equal(9000 + 1800, (await Pay.Bal(f, s.Seller)).Pending);
        var over = await Pay.Event(f, "payment.captured", new { kind = "booking_extra_charge", marketplaceBookingId = mb, paymentRef = "ch_y", currency = "USD", amountCents = 1500 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);   // only 1000 of the 3000 left
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "booking_no_show_fee", marketplaceBookingId = mb, paymentRef = "ch_n", currency = "USD", amountCents = 2000 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Pay.Event(f, "payment.captured", new { kind = "booking_cancellation_fee", marketplaceBookingId = mb, paymentRef = "ch_z", currency = "USD", amountCents = 1 })).StatusCode);
        Assert.Equal(new[] { "extra_charge", "policy_fee", "sale" }, Pay.Journals(f, s.Seller).Select(j => j.Type).Order());

        // booking refund before release, then release after N days from completion
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "refund.processed", new { refundId = "re_b", marketplaceBookingId = mb, amountCents = 5000 })).StatusCode);
        Pay.Age(f, 8); var rel = await Pay.Release(f);
        Assert.Equal(1, rel.Released);
        var bal = await Pay.Bal(f, s.Seller);
        Assert.Equal(0, bal.Pending);
        Assert.Equal(9000 + 1800 + 1800 - 4500, bal.Available);   // sale + extra + no-show net of fees, minus the refund of 5000 less the 500 fee returned
    }

    [Fact]
    public async Task The_ledger_is_append_only_and_unbalanced_journals_are_impossible()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Paid(s);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pay.Run(f, async sp =>
        {
            var db = sp.GetRequiredService<Solvelt.Api.Data.AppDbContext>();
            var e = await db.LedgerEntries.FirstAsync();
            e.AmountCents += 1; await db.SaveChangesAsync(); return 0;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pay.Run(f, async sp =>
        {
            var db = sp.GetRequiredService<Solvelt.Api.Data.AppDbContext>();
            db.LedgerJournals.Remove(await db.LedgerJournals.FirstAsync()); await db.SaveChangesAsync(); return 0;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pay.Run(f, async sp =>
            await sp.GetRequiredService<LedgerService>().PostAsync(s.Seller, "USD", "adjustment", "k-unbalanced", "x", [new(LedgerAccount.Available, 100, "x")])));
        Assert.All(Pay.Journals(f, s.Seller), j => Assert.Equal(0, j.Entries.Sum(e => e.AmountCents)));
    }

    [Fact]
    public async Task Ledger_endpoints_are_scoped_to_the_account_and_drill_down_to_the_order()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var other = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        await Pay.Paid(other);
        var ledger = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/ledger"));
        var item = Assert.Single(ledger["items"]!.AsArray())!;
        Assert.Equal(o.Number, (string)item["orderNumber"]!);
        Assert.Equal(2299, (long)item["amountCents"]!);
        var id = (string)item["id"]!;
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Get($"/api/v1/seller/payments/ledger/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Get($"/api/v1/seller/payments/ledger/{id}")).StatusCode);   // IDOR: 404, never 403
        // filters
        Assert.Single((await Pay.Ok(await s.Owner.Get($"/api/v1/seller/payments/ledger?orderId={o.Id}")))["items"]!.AsArray());
        Assert.Empty((await Pay.Ok(await s.Owner.Get($"/api/v1/seller/payments/ledger?orderId={Guid.NewGuid()}")))["items"]!.AsArray());
        Assert.Empty((await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/ledger?type=refund")))["items"]!.AsArray());
        Assert.Empty((await Pay.Ok(await other.Owner.Get($"/api/v1/seller/payments/ledger?orderId={o.Id}")))["items"]!.AsArray());   // someone else's order id shows nothing
    }

    [Fact]
    public async Task Refunding_in_thirds_returns_exactly_the_fee_that_was_charged()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s, price: 1000, shipping: 0);          // items 1000, fee 100, net 900
        Assert.Equal(100, Pay.Sum(f, s.Seller, LedgerAccount.Fees));
        await Pay.Refund(f, o.Mid, o.Number, 333);                     // fee back: 33.3 -> 33
        Assert.Equal(67, Pay.Sum(f, s.Seller, LedgerAccount.Fees));
        await Pay.Refund(f, o.Mid, o.Number, 333);
        Assert.Equal(34, Pay.Sum(f, s.Seller, LedgerAccount.Fees));
        await Pay.Refund(f, o.Mid, o.Number, 334);                     // the last refund takes the rounding remainder (34, not 33)
        Assert.Equal(0, Pay.Sum(f, s.Seller, LedgerAccount.Fees));
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Pending);
    }

    [Fact]
    public async Task Concurrent_postings_of_one_key_create_exactly_one_journal()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var seller = s.Seller;
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Pay.Run(f, async sp =>
        {
            var l = sp.GetRequiredService<LedgerService>();
            var j = await l.PostAsync(seller, "USD", "adjustment", "k-race", "race", [new(LedgerAccount.Available, 100, "adjustment"), new(LedgerAccount.Platform, -100, "adjustment")]);
            return j is not null && await l.SaveAsync();
        }))));
        Assert.Equal(1, results.Count(r => r));
        Assert.Single(Pay.Journals(f, seller));
        Assert.Equal(100, (await Pay.Bal(f, seller)).Available);
    }
}
