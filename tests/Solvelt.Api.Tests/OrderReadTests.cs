using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class OrderReadLimitFactory : OrdersFactory { public OrderReadLimitFactory() => Extra["RateLimit:InternalOrderReadPerMinute"] = "4"; }

/// <summary>C16: GET /api/internal/v1/orders/{marketplaceOrderId}, the signed reconciliation read (docs/ORDERS.md).</summary>
public class OrderReadTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private static string P(string mid) => $"/api/internal/v1/orders/{mid}";
    private async Task<HttpResponseMessage> Get(string mid, string? nonce = null) => await f.NewClient().SendAsync(Signed(f, "GET", P(mid), null, nonce));

    [Fact]
    public async Task The_read_is_signed_fresh_and_single_use_like_the_other_internal_calls()
    {
        var s = await NewShop(f); var (mid, _, _, _) = await Place(s);
        Assert.Equal(HttpStatusCode.OK, (await Get(mid)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Signed(f, "GET", P(mid), null, sign: false))).StatusCode);                          // unsigned
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Signed(f, "GET", P(mid), null, secret: "not-the-secret-not-the-secret-0123"))).StatusCode);   // wrong key
        var stale = await f.NewClient().SendAsync(Signed(f, "GET", P(mid), null, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 3600));
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        var nonce = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.OK, (await Get(mid, nonce)).StatusCode);
        var replay = await Get(mid, nonce);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode); Assert.Equal("replayed", await Code(replay));
    }

    [Fact]
    public async Task An_unknown_marketplace_order_is_404()
    {
        var r = await Get("MP-NOPE-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); Assert.Equal("order_not_found", await Code(r));
    }

    [Fact]
    public async Task A_multi_seller_order_reports_each_seller_order_with_the_quantities_and_money_the_events_carried()
    {
        var a = await NewShop(f); var b = await NewShop(f);
        var mid = Mid();
        var created = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, mid,
            [Line(a.Seller, a.Brand, a.Product, 3, 2000, "LA", tax: 600), Line(b.Seller, b.Brand, b.Product, 2, 1500, "LB", tax: 300)],
            [new { sellerId = a.Seller, brandId = a.Brand, amountCents = 400L, taxCents = 40L }, new { sellerId = b.Seller, brandId = b.Brand, amountCents = 250L, taxCents = 25L }]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ids = (await Node(created))["orders"]!.AsArray().ToDictionary(x => Guid.Parse((string)x!["sellerId"]!), x => Guid.Parse((string)x!["sellerOrderId"]!));
        var ia = ids[a.Seller]; var ib = ids[b.Seller];
        // A: accepted, shipped (UPS), delivered, a return of 2 units requested, approved, received, refunded with shipping; B: accepted and one of two units shipped
        await Ok(Act(a.Owner, ia, "accept")); await Ok(Act(a.Owner, ia, "ship", Ship(tracking: "1Z999AA10123456784"))); await Ok(Act(a.Owner, ia, "deliver"));
        var rq = await Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R-A", reason = "damaged", lines = new[] { new { marketplaceLineId = "LA", quantity = 2 } } });
        Assert.Equal(HttpStatusCode.OK, rq.StatusCode);
        var rid = (string)(await Detail(a.Owner, ia))["returns"]![0]!["id"]!;
        await Ok(Act(a.Owner, ia, $"returns/{rid}/approve", new { note = "send it" })); await Ok(Act(a.Owner, ia, $"returns/{rid}/receive", new { restock = true }));
        var lineA = await LineId(a.Owner, ia);
        await Ok(Act(a.Owner, ia, "refunds", new { reason = "returned_item", returnId = rid, lines = new[] { new { lineId = lineA, quantity = 1 } }, shippingCents = 100 }));
        await Ok(Act(b.Owner, ib, "accept")); var lineB = await LineId(b.Owner, ib);
        await Ok(Act(b.Owner, ib, "ship", Ship(lineB, 1, tracking: "1Z999AA10123456795")));

        var r = await Get(mid); Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = await Node(r);
        Assert.Equal(mid, (string)doc["marketplaceOrderId"]!);
        var so = doc["sellerOrders"]!.AsArray().ToDictionary(x => Guid.Parse((string)x!["sellerOrderId"]!), x => x!);
        Assert.Equal(2, so.Count);
        var ja = so[ia]; var jb = so[ib];
        // seller order A: identity, money, quantities
        Assert.Equal(a.Seller.ToString(), (string)ja["sellerId"]!); Assert.Equal(a.Brand.ToString(), (string)ja["brandId"]!);
        Assert.Equal(St(await Detail(a.Owner, ia)), (string)ja["status"]!);
        var dbA = f.WithDb(db => db.Orders.AsNoTracking().Single(o => o.Id == ia));
        Assert.Equal(dbA.Number, (string)ja["sellerOrderNumber"]!);
        Assert.Equal(2000L * 3 + 600 + 400 + 40, (long)ja["totalCents"]!);
        Assert.Equal(600L, (long)ja["taxCents"]!); Assert.Equal(400L, (long)ja["shippingCents"]!); Assert.Equal(40L, (long)ja["shippingTaxCents"]!);
        Assert.Equal(dbA.RefundedCents, (long)ja["refundedCents"]!); Assert.Equal("USD", (string)ja["currency"]!);
        var la = ja["lines"]!.AsArray().Single()!;
        Assert.Equal("LA", (string)la["marketplaceLineId"]!); Assert.Equal(3, (int)la["quantity"]!); Assert.Equal(3, (int)la["shippedQty"]!);
        Assert.Equal(0, (int)la["cancelledQty"]!); Assert.Equal(1, (int)la["refundedQty"]!); Assert.Equal(2, (int)la["returnedQty"]!);   // refunded and returned differ on purpose
        // the refund is exactly what refund.issued sent
        var ev = JsonNode.Parse(OutboxFor(f, mid).Single(m => m.EventType == "refund.issued" && (string)JsonNode.Parse(m.PayloadJson)!["data"]!["order"]!["sellerOrderId"]! == ia.ToString()).PayloadJson)!["data"]!;
        var rf = ja["refunds"]!.AsArray().Single()!;
        Assert.Equal((string)ev["refundId"]!, (string)rf["refundId"]!);
        foreach (var k in new[] { "amountCents", "taxCents", "shippingCents", "shippingTaxCents" }) Assert.Equal((long)ev[k]!, (long)rf[k]!);
        Assert.Equal((string)ev["reason"]!, (string)rf["reason"]!);
        // shipment and return
        var sh = ja["shipments"]!.AsArray().Single()!;
        Assert.Equal("1Z999AA10123456784", (string)sh["trackingNumber"]!); Assert.Equal("UPS", (string)sh["carrier"]!); Assert.Equal("carrier", (string)sh["method"]!); Assert.NotNull(sh["deliveredAt"]);
        var rt = ja["returns"]!.AsArray().Single()!;
        Assert.Equal("R-A", (string)rt["returnId"]!); Assert.Equal("Refunded", (string)rt["status"]!);
        // seller order B: partly shipped, nothing refunded
        Assert.Equal(1500L * 2 + 300 + 250 + 25, (long)jb["totalCents"]!); Assert.Equal(0L, (long)jb["refundedCents"]!);
        var lb = jb["lines"]!.AsArray().Single()!;
        Assert.Equal(2, (int)lb["quantity"]!); Assert.Equal(1, (int)lb["shippedQty"]!); Assert.Equal(0, (int)lb["refundedQty"]!);
        Assert.Empty(jb["refunds"]!.AsArray()); Assert.Empty(jb["returns"]!.AsArray()); Assert.Equal("1Z999AA10123456795", (string)jb["shipments"]![0]!["trackingNumber"]!);
    }

    [Fact]
    public async Task lastEventSequence_is_the_highest_outbox_order_key_of_that_seller_orders_scope()
    {
        var a = await NewShop(f); var b = await NewShop(f);
        var mid = Mid();
        var created = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, mid, [Line(a.Seller, a.Brand, a.Product, 1, 1000, "LA"), Line(b.Seller, b.Brand, b.Product, 1, 1000, "LB")],
            [new { sellerId = a.Seller, brandId = a.Brand, amountCents = 100L }, new { sellerId = b.Seller, brandId = b.Brand, amountCents = 100L }]));
        var ids = (await Node(created))["orders"]!.AsArray().ToDictionary(x => Guid.Parse((string)x!["sellerId"]!), x => Guid.Parse((string)x!["sellerOrderId"]!));
        await Ok(Act(a.Owner, ids[a.Seller], "accept")); await Ok(Act(a.Owner, ids[a.Seller], "ship", Ship())); await Ok(Act(b.Owner, ids[b.Seller], "accept"));
        var doc = await Node(await Get(mid));
        foreach (var (seller, id) in ids)
        {
            var scope = OutboxScopes.Order(id);
            var max = f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.SequenceScope == scope).Max(m => m.OrderKey));
            var got = doc["sellerOrders"]!.AsArray().Single(x => (string)x!["sellerOrderId"]! == id.ToString())!["lastEventSequence"];
            Assert.NotNull(got); Assert.Equal(max, (long?)got);
        }
        Assert.True((long)doc["sellerOrders"]!.AsArray().Single(x => (string)x!["sellerOrderId"]! == ids[a.Seller].ToString())!["lastEventSequence"]! > (long)doc["sellerOrders"]!.AsArray().Single(x => (string)x!["sellerOrderId"]! == ids[b.Seller].ToString())!["lastEventSequence"]!);   // A sent more events
    }

    [Fact]
    public async Task The_read_contains_no_buyer_data_even_before_redaction_and_still_works_after_it()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s, buyer: "Jane Buyer");
        await Ok(Act(s.Owner, id, "accept"));
        var allowed = new HashSet<string> { "marketplaceOrderId", "sellerOrders", "sellerOrderId", "sellerOrderNumber", "sellerId", "brandId", "status", "updatedAt", "currency", "totalCents", "taxCents", "shippingCents", "shippingTaxCents", "refundedCents",
            "lines", "marketplaceLineId", "sku", "quantity", "shippedQty", "cancelledQty", "refundedQty", "returnedQty", "refunds", "refundId", "amountCents", "reason", "createdAt", "shipments", "shipmentId", "method", "carrier", "trackingNumber", "shippedAt", "deliveredAt", "returns", "returnId", "lastEventSequence" };
        async Task Check()
        {
            var r = await Get(mid); Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            foreach (var needle in new[] { "Jane", "Buyer", "relay", "@", "Main St", "phone", "email", "address", "buyer", "ship" + "Name", "note", "message" }) Assert.DoesNotContain(needle, body.Replace("shippingCents", "").Replace("shippingTaxCents", "").Replace("shippedQty", "").Replace("shipments", "").Replace("shipmentId", "").Replace("shippedAt", ""), StringComparison.OrdinalIgnoreCase);
            void Walk(JsonNode? n) { if (n is JsonObject o) foreach (var (k, v) in o) { Assert.Contains(k, allowed); Walk(v); } else if (n is JsonArray a) foreach (var x in a) Walk(x); }
            Walk(JsonNode.Parse(body));
        }
        await Check();
        f.WithDb(db => db.Orders.Where(o => o.Id == id).ExecuteUpdate(u => u.SetProperty(o => o.PiiRedactedAt, f.Time.GetUtcNow().UtcDateTime).SetProperty(o => o.BuyerName, "").SetProperty(o => o.ShipName, "").SetProperty(o => o.ShipLine1, "").SetProperty(o => o.BuyerRelayEmail, (string?)null).SetProperty(o => o.BuyerPhone, (string?)null)));
        await Check();                                                                            // after redaction: what remains, with no PII
    }
}

/// <summary>The read has its own per-caller limit (RateLimit:InternalOrderReadPerMinute, default 600), separate from the general internal limit.</summary>
public class OrderReadRateLimitTests(OrderReadLimitFactory f) : IClassFixture<OrderReadLimitFactory>
{
    [Fact]
    public async Task Too_many_reads_get_429_while_other_internal_calls_are_unaffected()
    {
        var s = await NewShop(f); var (mid, _, _, _) = await Place(s);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) codes.Add((await f.NewClient().SendAsync(Signed(f, "GET", $"/api/internal/v1/orders/{mid}", null))).StatusCode);
        Assert.Equal(4, codes.Count(c => c == HttpStatusCode.OK)); Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.Created, (await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 1, 1000, "L9")], [new { sellerId = s.Seller, brandId = s.Brand, amountCents = 100L }]))).StatusCode);
    }
}
