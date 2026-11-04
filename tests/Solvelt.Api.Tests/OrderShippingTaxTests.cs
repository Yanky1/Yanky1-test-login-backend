using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>C8 (per-group speed and method, tax on shipping) and C9 (tax split in refund and cancel events), docs/ORDERS.md "Money in events".</summary>
public class OrderShippingTaxTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private static JsonNode Event(ApiFactory f, string mid, string type, int index = 0) =>
        JsonNode.Parse(OutboxFor(f, mid).Where(m => m.EventType == type).ElementAt(index).PayloadJson)!["data"]!;

    private static async Task<(string Mid, Guid Id)> PlaceTaxed(Shop s, int qty = 1, long price = 1000, long lineTax = 0, long shipping = 500, long shipTax = 45, string speed = "standard")
    {
        var mid = Mid();
        var r = await Inbound(s.F, "/api/internal/v1/orders", OrderBody(s.F, mid, [Line(s.Seller, s.Brand, s.Product, qty, price, "L1", tax: lineTax)],
            [new { sellerId = s.Seller, brandId = s.Brand, amountCents = shipping, taxCents = shipTax }], speed: speed));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (mid, Guid.Parse((string)(await Node(r))["orders"]![0]!["sellerOrderId"]!));
    }

    private static async Task Deliver(Shop s, Guid id)
    {
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
    }

    // ---- C8 (a): speed and method per group --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_groups_own_speed_sets_its_deliver_by_date_and_the_other_group_falls_back_to_the_order_level_speed()
    {
        var a = await NewShop(f); var b = await NewShop(f);
        var mid = Mid();
        var body = OrderBody(f, mid, [Line(a.Seller, a.Brand, a.Product, 1, 1000, "LA"), Line(b.Seller, b.Brand, b.Product, 1, 1000, "LB")],
            [new { sellerId = a.Seller, brandId = a.Brand, amountCents = 500L, speed = "overnight", method = "  Next-day air\u0007 " }, new { sellerId = b.Seller, brandId = b.Brand, amountCents = 300L }],
            speed: "expedited");
        var r = await Inbound(f, "/api/internal/v1/orders", body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var ids = (await Node(r))["orders"]!.AsArray().ToDictionary(x => Guid.Parse((string)x!["brandId"]!), x => Guid.Parse((string)x!["sellerOrderId"]!));
        var oa = f.WithDb(db => db.Orders.AsNoTracking().Single(x => x.Id == ids[a.Brand]));
        var ob = f.WithDb(db => db.Orders.AsNoTracking().Single(x => x.Id == ids[b.Brand]));
        Assert.Equal("overnight", oa.ShippingSpeed); Assert.Equal(1, (oa.DeliverByAt - oa.ShipByAt).TotalDays);          // the group's own speed
        Assert.Equal("Next-day air", oa.ShippingMethod);                                                                      // sanitised label
        Assert.Equal("expedited", ob.ShippingSpeed); Assert.Equal(2, (ob.DeliverByAt - ob.ShipByAt).TotalDays);              // no group speed: the order-level one
        Assert.Equal("Standard", ob.ShippingMethod);                                                                         // and the order-level method
        var detail = await Detail(a.Owner, oa.Id);
        Assert.Equal("overnight", (string)detail["shippingSpeed"]!); Assert.Equal("Next-day air", (string)detail["shippingMethod"]!);
    }

    [Fact]
    public async Task An_unknown_group_speed_a_bad_method_or_a_bad_tax_is_a_422_and_creates_nothing()
    {
        var s = await NewShop(f);
        foreach (var bad in new object[]
        {
            new { sellerId = s.Seller, brandId = s.Brand, amountCents = 500L, speed = "teleport" },
            new { sellerId = s.Seller, brandId = s.Brand, amountCents = 500L, method = new string('x', 81) },
            new { sellerId = s.Seller, brandId = s.Brand, amountCents = 500L, method = "\u0001\u0002" },
            new { sellerId = s.Seller, brandId = s.Brand, amountCents = 500L, taxCents = -1L },
            new { sellerId = s.Seller, brandId = s.Brand, amountCents = 0L, taxCents = 10L },            // tax on no shipping at all
        })
        {
            var mid = Mid();
            var r = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, mid, [Line(s.Seller, s.Brand, s.Product, 1, 1000, "L1")], [bad]));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.False(f.WithDb(db => db.Orders.Any(o => o.MarketplaceOrderId == mid)));
        }
    }

    [Fact]
    public async Task Repeated_entries_for_one_group_add_up_but_must_agree_on_speed()
    {
        var s = await NewShop(f);
        var ok = Mid();
        var r = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, ok, [Line(s.Seller, s.Brand, s.Product, 1, 1000, "L1")],
            [new { sellerId = s.Seller, brandId = s.Brand, amountCents = 300L, taxCents = 20L, speed = "expedited" }, new { sellerId = s.Seller, brandId = s.Brand, amountCents = 200L, taxCents = 25L }]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var o = f.WithDb(db => db.Orders.AsNoTracking().Single(x => x.MarketplaceOrderId == ok));
        Assert.Equal((500L, 45L, "expedited"), (o.ShippingCents, o.ShippingTaxCents, o.ShippingSpeed));
        var clash = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 1, 1000, "L1")],
            [new { sellerId = s.Seller, brandId = s.Brand, amountCents = 300L, speed = "expedited" }, new { sellerId = s.Seller, brandId = s.Brand, amountCents = 200L, speed = "overnight" }]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
    }

    // ---- C8 (b): tax on shipping -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Shipping_tax_is_in_the_total_but_not_in_the_fee_base_or_the_net_and_is_shown_on_the_detail_and_the_packing_slip()
    {
        var s = await NewShop(f);
        var (_, plain) = await PlaceTaxed(s, shipTax: 0);
        var (_, taxed) = await PlaceTaxed(s, shipTax: 45);
        var p = (await Detail(s.Owner, plain))["money"]!; var t = (await Detail(s.Owner, taxed))["money"]!;
        Assert.Equal(1500, (long)p["totalCents"]!);
        Assert.Equal(1545, (long)t["totalCents"]!);                                                            // + the shipping tax
        Assert.Equal((long)p["feeCents"]!, (long)t["feeCents"]!);                                              // the fee base is the items after discount only
        Assert.Equal((long)p["netCents"]!, (long)t["netCents"]!);                                              // tax is remitted by the marketplace: not the seller's money
        Assert.Equal(45, (long)t["shippingTaxCents"]!); Assert.Equal(0, (long)p["shippingTaxCents"]!);
        Assert.Equal(1545, (long)t["refundableCents"]!);
        var slip = await Node(await s.Owner.Get($"/api/v1/seller/orders/{taxed}/packing-slip"));
        Assert.Equal(500, (long)slip["shipping"]!["shippingCents"]!); Assert.Equal(45, (long)slip["shipping"]!["shippingTaxCents"]!);
    }

    [Fact]
    public async Task Replaying_the_same_order_is_a_no_op_and_a_replay_with_different_shipping_fields_returns_the_original()
    {
        var s = await NewShop(f);
        var mid = Mid();
        object Body(long tax, string speed) => OrderBody(f, mid, [Line(s.Seller, s.Brand, s.Product, 1, 1000, "L1")], [new { sellerId = s.Seller, brandId = s.Brand, amountCents = 500L, taxCents = tax, speed }]);
        var first = await Inbound(f, "/api/internal/v1/orders", Body(45, "standard"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var again = await Inbound(f, "/api/internal/v1/orders", Body(45, "standard"));
        var changed = await Inbound(f, "/api/internal/v1/orders", Body(99, "overnight"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); Assert.Equal(HttpStatusCode.OK, changed.StatusCode);   // the existing rule: the marketplace order id is the key, the first order stands
        var o = f.WithDb(db => db.Orders.AsNoTracking().Where(x => x.MarketplaceOrderId == mid).ToList());
        Assert.Single(o); Assert.Equal(45, o[0].ShippingTaxCents); Assert.Equal("standard", o[0].ShippingSpeed);
    }

    // ---- refunds: the worked example of docs/ORDERS.md ---------------------------------------------------------------------------

    [Fact]
    public async Task Worked_example_500_shipping_45_tax_a_40_percent_refund_is_218_200_18_18_and_the_rest_takes_the_remainder()
    {
        var s = await NewShop(f);
        var (mid, id) = await PlaceTaxed(s, shipping: 500, shipTax: 45);
        await Deliver(s, id);
        var preview = await Ok(s.Owner.Post($"/api/v1/seller/orders/{id}/refunds/preview", new { reason = "goodwill", shippingCents = 200 }));
        Assert.Equal((218L, 200L, 18L, 18L), ((long)preview["amountCents"]!, (long)preview["shippingCents"]!, (long)preview["shippingTaxCents"]!, (long)preview["taxCents"]!));
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", shippingCents = 200 }));
        var e1 = Event(f, mid, "refund.issued", 0);
        Assert.Equal(218, (long)e1["amountCents"]!); Assert.Equal(200, (long)e1["shippingCents"]!); Assert.Equal(18, (long)e1["shippingTaxCents"]!); Assert.Equal(18, (long)e1["taxCents"]!);
        Assert.Empty(e1["lines"]!.AsArray());

        var p2 = await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", shippingCents = 300 }));
        var e2 = Event(f, mid, "refund.issued", 1);
        Assert.Equal(327, (long)e2["amountCents"]!); Assert.Equal(300, (long)e2["shippingCents"]!); Assert.Equal(27, (long)e2["shippingTaxCents"]!); Assert.Equal(27, (long)e2["taxCents"]!);   // 45 - 18: the shares add up exactly
        Assert.Equal(545, (long)p2["money"]!["refundedCents"]!);
        Assert.Equal(45, f.WithDb(db => db.Refunds.Where(r => r.OrderId == id).Sum(r => r.ShippingTaxCents)));
    }

    [Fact]
    public async Task A_full_refund_returns_the_items_and_all_of_the_shipping_tax_and_nothing_is_left_refundable()
    {
        var s = await NewShop(f);
        var (mid, id) = await PlaceTaxed(s, qty: 1, price: 1000, lineTax: 80, shipping: 500, shipTax: 45);   // total 1000 + 80 + 500 + 45 = 1625
        await Deliver(s, id);
        var d = await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", full = true }));
        Assert.Equal(1625, (long)d["money"]!["refundedCents"]!); Assert.Equal(0, (long)d["money"]!["refundableCents"]!); Assert.Equal("Refunded", St(d));
        var e = Event(f, mid, "refund.issued");
        Assert.Equal(1625, (long)e["amountCents"]!); Assert.Equal(500, (long)e["shippingCents"]!); Assert.Equal(45, (long)e["shippingTaxCents"]!); Assert.Equal(125, (long)e["taxCents"]!);
        Assert.Equal(80, (long)e["lines"]![0]!["taxCents"]!);
    }

    // ---- C9: tax split ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_multi_unit_line_refunded_in_three_parts_has_tax_shares_that_add_up_and_amount_minus_tax_is_the_items_without_tax()
    {
        var s = await NewShop(f);
        var (mid, id) = await PlaceTaxed(s, qty: 3, price: 1000, lineTax: 100, shipping: 0, shipTax: 0);   // 3000 + 100 tax; 100 / 3 does not divide
        await Deliver(s, id);
        var line = await LineId(s.Owner, id);
        for (var i = 0; i < 3; i++) await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        var events = Enumerable.Range(0, 3).Select(i => Event(f, mid, "refund.issued", i)).ToList();
        Assert.Equal([33L, 33L, 34L], events.Select(e => (long)e["taxCents"]!));                                  // floor, floor, remainder
        Assert.Equal(100, events.Sum(e => (long)e["taxCents"]!));
        Assert.Equal(100, events.Sum(e => (long)e["lines"]![0]!["taxCents"]!));
        // what the marketplace sends back as refund.processed (items excluding tax) always adds up to the items
        Assert.Equal([1000L, 1000L, 1000L], events.Select(e => (long)e["amountCents"]! - (long)e["taxCents"]! - (long)e["shippingCents"]! - (long)e["shippingTaxCents"]!));
        Assert.Equal(3100, events.Sum(e => (long)e["amountCents"]!));
    }

    [Fact]
    public async Task A_full_cancel_carries_the_tax_inside_refund_due_and_a_remainder_cancel_carries_its_lines_tax_and_no_shipping_tax()
    {
        var s = await NewShop(f);
        var (midFull, full) = await PlaceTaxed(s, qty: 2, price: 1000, lineTax: 90, shipping: 500, shipTax: 45);   // 2000 + 90 + 500 + 45 = 2635
        await Ok(Act(s.Owner, full, "cancel", new { reason = "other" }));
        var c = Event(f, midFull, "order.cancelled");
        Assert.Equal(2635, (long)c["refundDueCents"]!); Assert.Equal(135, (long)c["taxCents"]!); Assert.Equal(45, (long)c["shippingTaxCents"]!);

        var (midPart, part) = await PlaceTaxed(s, qty: 3, price: 1000, lineTax: 100, shipping: 500, shipTax: 45);
        await Ok(Act(s.Owner, part, "accept"));
        var line = await LineId(s.Owner, part);
        await Ok(Act(s.Owner, part, "ship", Ship(line, 1)));
        await Ok(Act(s.Owner, part, "cancel", new { reason = "other" }));                                   // the two unshipped units
        var p = Event(f, midPart, "order.cancelled");
        Assert.True((bool)p["partial"]!);
        Assert.Equal(2066, (long)p["refundDueCents"]!);                                                      // floor(3100 x 2 / 3); no shipping, no shipping tax
        Assert.Equal(66, (long)p["taxCents"]!); Assert.Equal(0, (long)p["shippingTaxCents"]!);              // floor(100 x 2 / 3)
        Assert.Equal(66, (long)p["lines"]![0]!["taxCents"]!);
    }
}
