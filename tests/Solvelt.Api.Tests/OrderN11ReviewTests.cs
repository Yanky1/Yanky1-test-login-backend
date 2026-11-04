using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>Brief N11: the review of C7-C9 (shipping overflow, method length, separate flooring of items and tax, legacy refund rows) and its two test gaps.</summary>
public class OrderN11ReviewTests(PayFactory f) : IClassFixture<PayFactory>
{
    private static async Task<(string Mid, Guid Id, string Number)> Place(Shop s, int qty, long price, long discount, long lineTax, long shipping, long shipTax, string? speed = null, string? method = null)
    {
        var mid = Mid();
        var r = await Inbound(s.F, "/api/internal/v1/orders", OrderBody(s.F, mid, [Line(s.Seller, s.Brand, s.Product, qty, price, "L1", tax: lineTax, discount: discount)],
            [new { sellerId = s.Seller, brandId = s.Brand, amountCents = shipping, taxCents = shipTax, speed, method }]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var o = (await Node(r))["orders"]![0]!;
        return (mid, Guid.Parse((string)o["sellerOrderId"]!), (string)o["number"]!);
    }

    private static async Task Deliver(Shop s, Guid id) { await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver")); }
    private static JsonNode Ev(ApiFactory f, string mid, string type, int i = 0) => JsonNode.Parse(OutboxFor(f, mid).Where(m => m.EventType == type).ElementAt(i).PayloadJson)!["data"]!;

    // ---- 1: overflow --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_group_whose_summed_shipping_or_tax_exceeds_the_cap_and_too_many_entries_are_422()
    {
        var s = await NewShop(f);
        object Entry(long amount, long tax) => new { sellerId = s.Seller, brandId = s.Brand, amountCents = amount, taxCents = tax };
        async Task<HttpStatusCode> Try(params object[] entries) =>
            (await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 1, 1000, "L1")], entries))).StatusCode;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Enumerable.Range(0, 10).Select(_ => Entry(1_000_000_000, 1_000_000_000)).ToArray()));   // the reviewer's repro: a sum of 1e10
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Entry(600_000_000, 1), Entry(600_000_000, 1)));                                        // the amount alone adds up past the cap
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Entry(100, 600_000_000), Entry(100, 600_000_000)));                                    // so does the tax
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Enumerable.Range(0, 51).Select(_ => Entry(1, 0)).ToArray()));                          // at most 50 entries
        Assert.Equal(HttpStatusCode.Created, await Try(Enumerable.Range(0, 50).Select(_ => Entry(1, 0)).ToArray()));                                      // 50 are fine
        Assert.Equal(HttpStatusCode.Created, await Try(Entry(1_000_000_000, 1_000_000_000)));                                                              // a single entry at the cap is fine
    }

    [Fact]
    public void Prorations_use_128_bit_arithmetic_so_large_valid_amounts_cannot_overflow()
    {
        Assert.Equal(9_000_000_000_000_000_000L, OrderSupport.Prorate(9_000_000_000_000_000_000L, 10_000, 10_000));                // total x qty = 9e22: far beyond a long
        Assert.Equal(1_000_000_000_000_000L, OrderSupport.Fee(1_000_000_000_000_000L, 10_000));                                    // 1e15 x 10 000 basis points = 1e19 > long.MaxValue
        var o = new SellerOrder { ShippingCents = 10_000_000_000, ShippingTaxCents = 10_000_000_000 };                              // a legacy order the old code accepted
        Assert.Equal(5_000_000_000, OrderSupport.ShippingTaxShare(o, 0, 0, 5_000_000_000));                                         // the reviewer's repro gave -534023222
    }

    // ---- 2: method length ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_shipping_method_of_64_characters_is_stored_and_65_is_a_422_for_a_group_and_for_the_order()
    {
        var s = await NewShop(f);
        var ok = await Place(s, 1, 1000, 0, 0, 500, 0, method: new string('m', 64));
        Assert.Equal(new string('m', 64), f.WithDb(db => db.Orders.AsNoTracking().Single(o => o.Id == ok.Id)).ShippingMethod);
        var bodyGroup = OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 1, 1000, "L1")], [new { sellerId = s.Seller, brandId = s.Brand, amountCents = 500L, method = new string('m', 65) }]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Inbound(f, "/api/internal/v1/orders", bodyGroup)).StatusCode);
        var order = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 1, 1000, "L1")], [new { sellerId = s.Seller, brandId = s.Brand, amountCents = 500L }]), J))!;
        order["shippingMethod"] = new string('m', 65);
        var r = await Inbound(f, "/api/internal/v1/orders", order);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);                                                            // the order-level method had no length check at all
    }

    // ---- 3: items and tax prorated separately ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tiny_line_refunded_one_unit_at_a_time_never_gives_a_negative_part_and_every_refund_processed_is_accepted()
    {
        var s = await NewShop(f);
        await Pay.Fresh(s);
        var (mid, id, number) = await Place(s, qty: 3, price: 1, discount: 2, lineTax: 2, shipping: 30, shipTax: 0);   // items 3 - 2 = 1, tax 2
        await Deliver(s, id);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, mid, number)).StatusCode);
        var line = await LineId(s.Owner, id);
        var parts = new List<(long Items, long Tax)>();
        for (var i = 0; i < 3; i++)
        {
            await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } }, shippingCents = 10 }));
            var e = Ev(f, mid, "refund.issued", i);
            var amount = (long)e["amountCents"]!; var tax = (long)e["taxCents"]!; var ship = (long)e["shippingCents"]!;
            var items = amount - tax - ship;
            Assert.True(items >= 0 && tax >= 0, $"refund {i}: items {items}, tax {tax}");
            parts.Add((items, tax));
            Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, mid, number, items, ship)).StatusCode);                          // refund.processed accepts every step
        }
        Assert.Equal([0L, 0L, 1L], parts.Select(p => p.Items));                                                                     // floor, floor, remainder
        Assert.Equal([0L, 0L, 2L], parts.Select(p => p.Tax));
    }

    // ---- 4: legacy refund rows ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_backfill_gives_legacy_refund_lines_their_tax_share_so_the_next_refund_does_not_report_it_again_and_running_it_twice_changes_nothing()
    {
        var s = await NewShop(f);
        var (mid, id, number) = await Place(s, qty: 2, price: 1000, discount: 0, lineTax: 100, shipping: 0, shipTax: 0);
        await Deliver(s, id);
        var line = await LineId(s.Owner, id);
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        Assert.Equal(50, (long)Ev(f, mid, "refund.issued", 0)["taxCents"]!);
        f.WithDb(db => db.RefundLines.Where(r => r.OrderLineId == line).ExecuteUpdate(u => u.SetProperty(r => r.TaxCents, 0L)));          // what a row written before the OrderShippingTax migration looks like
        f.WithDb(db => db.Database.ExecuteSqlRaw(RefundTaxBackfill.Sql));                                                                     // exactly what the migration runs
        Assert.Equal(50, f.WithDb(db => db.RefundLines.AsNoTracking().Single(r => r.OrderLineId == line).TaxCents));
        f.WithDb(db => db.Database.ExecuteSqlRaw(RefundTaxBackfill.Sql));
        Assert.Equal(50, f.WithDb(db => db.RefundLines.AsNoTracking().Single(r => r.OrderLineId == line).TaxCents));
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        Assert.Equal(50, (long)Ev(f, mid, "refund.issued", 1)["taxCents"]!);                                                                  // not 100
    }

    [Fact]
    public async Task Without_the_backfill_a_legacy_row_would_make_the_next_refund_report_the_earlier_tax_again()
    {
        var s = await NewShop(f);
        var (mid, id, _) = await Place(s, qty: 2, price: 1000, discount: 0, lineTax: 100, shipping: 0, shipTax: 0);
        await Deliver(s, id);
        var line = await LineId(s.Owner, id);
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        f.WithDb(db => db.RefundLines.Where(r => r.OrderLineId == line).ExecuteUpdate(u => u.SetProperty(r => r.TaxCents, 0L)));
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        Assert.Equal(100, (long)Ev(f, mid, "refund.issued", 1)["taxCents"]!);                                                                 // the documented gap, which the migration closes
    }

    // ---- the two test gaps ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_packing_slip_shows_the_groups_own_speed_and_deliver_by_date()
    {
        var s = await NewShop(f);
        var (_, id, _) = await Place(s, 1, 1000, 0, 0, 500, 0, speed: "overnight");
        var slip = await Node(await s.Owner.Get($"/api/v1/seller/orders/{id}/packing-slip"));
        Assert.Equal("overnight", (string)slip["shippingSpeed"]!);
        var o = f.WithDb(db => db.Orders.AsNoTracking().Single(x => x.Id == id));
        Assert.Equal(o.DeliverByAt, ((DateTime)slip["deliverByAt"]!).ToUniversalTime());
    }

    [Fact]
    public async Task A_remainder_cancel_after_a_partial_refund_of_the_same_line_takes_the_remainder_not_a_proration()
    {
        var s = await NewShop(f);
        var (mid, id, _) = await Place(s, qty: 3, price: 1000, discount: 0, lineTax: 100, shipping: 0, shipTax: 0);     // line 3000 + 100 tax
        await Ok(Act(s.Owner, id, "accept"));
        var line = await LineId(s.Owner, id);
        await Ok(Act(s.Owner, id, "ship", Ship(line, 1)));
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));      // refund 1 unit: items 1000, tax 33
        await Ok(Act(s.Owner, id, "cancel", new { reason = "other" }));                                                                 // the two unshipped units
        var c = Ev(f, mid, "order.cancelled");
        Assert.Equal(2067, (long)c["refundDueCents"]!);                                                                                  // 3100 - 1033; a proration would give 2066
        Assert.Equal(67, (long)c["taxCents"]!);                                                                                          // 100 - 33; a proration would give 66
        Assert.Equal(3100, f.WithDb(db => db.Refunds.Where(r => r.OrderId == id).Sum(r => r.AmountCents)));                              // together they are exactly what was paid
    }
}
