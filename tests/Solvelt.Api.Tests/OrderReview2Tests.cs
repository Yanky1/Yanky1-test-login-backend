using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class RaceFactory : OrdersFactory
{
    public static volatile bool Race;
    public static Guid RaceOrder;
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddScoped<BuyerEventService>(sp =>
        {
            var svc = ActivatorUtilities.CreateInstance<BuyerEventService>(sp);
            svc.BeforeSave = async o =>
            {
                if (!Race || o.Id != RaceOrder) return;
                using var other = sp.CreateScope();   // somebody else changes the order right before our save
                await other.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>().Orders.Where(x => x.Id == o.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Version, x => x.Version + 1));
            };
            return svc;
        }));
    }
}

public class OrderRaceHttpTests(RaceFactory f) : IClassFixture<RaceFactory>
{
    [Fact]
    public async Task A_racing_buyer_request_answers_503_with_retry_after_and_the_per_order_results()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s);
        var body = new { requestId = "CR-HTTP", reason = "changed_mind" };
        RaceFactory.RaceOrder = id; RaceFactory.Race = true;
        HttpResponseMessage r;
        try { r = await Inbound(f, $"/api/internal/v1/orders/{mid}/cancel-requests", body); } finally { RaceFactory.Race = false; }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("1", r.Headers.GetValues("Retry-After").Single());
        var n = await Node(r);
        Assert.Equal("version_conflict", (string)n["code"]!);
        Assert.Equal("retry", (string)n["orders"]![0]!["result"]!); Assert.Equal(number, (string)n["orders"]![0]!["number"]!);
        var again = await Inbound(f, $"/api/internal/v1/orders/{mid}/cancel-requests", body);      // the marketplace retries: stored now
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("requested", (string)(await Node(again))["orders"]![0]!["result"]!);
    }
}

public class OrderRemainderTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task The_unshipped_remainder_of_a_partly_shipped_order_can_be_cancelled_with_its_stock_and_refund()
    {
        var s = await NewShop(f, stock: 10);
        var (mid, id, _, _) = await Place(s, qty: 3, price: 1000, shipping: 400, tax: 300);   // total 3000 + 400 + 300
        await Ok(Act(s.Owner, id, "accept"));
        var line = await LineId(s.Owner, id);
        await Ok(Act(s.Owner, id, "ship", Ship(line, 1)));
        Assert.Equal((9L, 2L), Stock(f, s.Product));                                        // 1 shipped, 2 still held
        var before = await Detail(s.Owner, id);
        Assert.True((bool)before["actions"]!["cancelRemainder"]!); Assert.True((bool)before["actions"]!["cancel"]!);
        var done = await Ok(Act(s.Owner, id, "cancel", new { reason = "out_of_stock", note = "Sold out", restock = true }));
        Assert.Equal("Shipped", St(done));                                                  // nothing left to ship
        Assert.Equal((9L, 0L), Stock(f, s.Product));                                        // reservation released
        Assert.Equal(2, (int)done["lines"]![0]!["cancelledQty"]!); Assert.Equal(0, (int)done["lines"]![0]!["unshippedQty"]!);
        Assert.Equal(2200, (long)done["money"]!["refundedCents"]!);                         // 2 x (1000 + 100 tax)
        Assert.Equal("cancelled_remainder", (string)done["refunds"]![0]!["reason"]!);
        Assert.Contains(done["timeline"]!.AsArray(), e => (string)e!["type"]! == "order.remainder_cancelled");
        var ev = System.Text.Json.Nodes.JsonNode.Parse(OutboxFor(f, mid).Last(m => m.EventType == "order.cancelled").PayloadJson)!["data"]!;
        Assert.True((bool)ev["partial"]!); Assert.Equal(2200, (long)ev["refundDueCents"]!); Assert.Equal("out_of_stock", (string)ev["reason"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "cancel", new { reason = "other" })).StatusCode);   // nothing left to cancel
        Assert.False((bool)(await Detail(s.Owner, id))["actions"]!["cancelRemainder"]!);
        // the shipped unit goes through the normal flow and its refund takes the rounding remainder: lines add up to what was paid for items+tax
        await Ok(Act(s.Owner, id, "deliver"));
        var full = await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", full = true }));
        Assert.Equal(3300 + 400, (long)full["money"]!["refundedCents"]!);
        Assert.Equal("Refunded", St(full));
    }

    [Fact]
    public async Task Cancelling_the_remainder_works_from_hold_and_without_restock()
    {
        var s = await NewShop(f, stock: 10);
        var (_, id, _, _) = await Place(s, qty: 2);
        await Ok(Act(s.Owner, id, "accept"));
        await Ok(Act(s.Owner, id, "ship", Ship(await LineId(s.Owner, id), 1)));
        await Ok(Act(s.Owner, id, "hold"));
        var d = await Ok(Act(s.Owner, id, "cancel", new { reason = "other", restock = false }));
        Assert.Equal("Shipped", St(d));
        Assert.Equal((8L, 0L), Stock(f, s.Product));                                        // not restocked: the held unit leaves on-hand stock too
        Assert.Equal(1999, (long)d["money"]!["refundedCents"]!);
    }
}

public class OrderStockMismatchTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Shipping_oversold_units_that_cannot_be_deducted_is_flagged_not_silent()
    {
        var s = await NewShop(f, stock: 1);
        var (_, over, _, _) = await Place(s, qty: 2);          // oversold: reserves nothing
        var (_, holder, _, _) = await Place(s, qty: 1);        // holds the only unit
        Assert.True((bool)(await Detail(s.Owner, over))["oversold"]!);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["quantity"] = 2)).StatusCode);   // restocked to 2, 1 still reserved
        await Ok(Act(s.Owner, over, "accept"));
        var shipped = await Ok(Act(s.Owner, over, "ship", Ship()));
        Assert.Equal((1L, 1L), Stock(f, s.Product));            // only the free unit could be deducted; the reserved one is untouched
        Assert.True((bool)shipped["stockMismatch"]!);
        var ev = shipped["timeline"]!.AsArray().First(e => (string)e!["type"]! == "stock.mismatch")!;
        Assert.Contains("1 unit(s) not deducted", (string)ev["text"]!);
        Assert.Contains(f.WithDb(db => db.AuditEvents.Select(a => a.Detail).ToList()), d => d != null && d.Contains("stock_mismatch=true"));
        _ = holder;
    }
}

public class OrderReconcileTests(FlakyFactory f) : IClassFixture<FlakyFactory>
{
    private async Task<OrderMaintenance.Summary> Run() { using var sc = f.Services.CreateScope(); return await sc.ServiceProvider.GetRequiredService<OrderMaintenance>().RunAsync(); }

    [Fact]
    public async Task A_failing_release_does_not_stop_the_others_and_the_reconciler_repairs_what_leaked()
    {
        var s = await NewShop(f, stock: 10);
        var p2 = await ActiveProduct(s.Owner, "Second", s.Brand, "S2-" + Guid.NewGuid().ToString("N")[..6], 10);
        var p3 = await ActiveProduct(s.Owner, "Third", s.Brand, "S3-" + Guid.NewGuid().ToString("N")[..6], 10);
        var (_, keep, _, _) = await Place(s, qty: 1);          // a real order whose hold must never be touched
        FlakyInventory.Counter = 0; FlakyInventory.FailOnReserveNumber = 3; FlakyInventory.ReleaseCounter = 0; FlakyInventory.FailReleaseNumber = 1;
        try
        {
            var r = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 3, lid: "A"), Line(s.Seller, s.Brand, p2, 2, lid: "B"), Line(s.Seller, s.Brand, p3, 1, lid: "C")]));
            Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        }
        finally { FlakyInventory.FailOnReserveNumber = 0; FlakyInventory.FailReleaseNumber = 0; }
        Assert.Equal(0, Stock(f, p3).Reserved);                // never reserved
        Assert.Equal(0, Stock(f, p2).Reserved);                // released although the first release threw
        Assert.Equal(1 + 3, Stock(f, s.Product).Reserved);     // the leaked hold (3) + the real order's (1)

        await Run();                                           // first sighting: only remembered
        Assert.Equal(4, Stock(f, s.Product).Reserved);
        f.Time.Advance(TimeSpan.FromMinutes(3));
        var r2 = await Run();
        Assert.True(r2.ReservationsRepaired >= 1);
        Assert.Equal((10L, 1L), Stock(f, s.Product));          // leak gone, the real hold stays
        Assert.Equal(1, (await Detail(s.Owner, keep)) is { } d && (int)d["lines"]![0]!["reservedQty"]! > 0 ? 1 : 0);
        Assert.Equal(0, (await Run()).ReservationsRepaired);   // nothing more to repair
    }

    [Fact]
    public async Task The_reconciler_never_wipes_the_hold_of_an_ingest_that_is_still_running()
    {
        var s = await NewShop(f, stock: 10);
        FlakyInventory.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); FlakyInventory.Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = FlakyInventory.Gate; var mid = Mid();
        Task<HttpResponseMessage> ingest;
        try
        {
            ingest = Task.Run(() => Inbound(f, "/api/internal/v1/orders", OrderBody(f, mid, [Line(s.Seller, s.Brand, s.Product, 2, lid: "L1")])));
            await FlakyInventory.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));       // stock is reserved, the order is not saved yet
            Assert.Equal((10L, 2L), Stock(f, s.Product));
            await Run(); f.Time.Advance(TimeSpan.FromMinutes(3)); await Run(); f.Time.Advance(TimeSpan.FromMinutes(3));
            Assert.Equal(0, (await Run()).ReservationsRepaired);
            Assert.Equal((10L, 2L), Stock(f, s.Product));                                // the legitimate hold survived two stalled runs
        }
        finally { FlakyInventory.Gate = null; FlakyInventory.Reached = null; gate.TrySetResult(); }
        var r = await ingest;
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = Guid.Parse((string)(await Node(r))["orders"]![0]!["sellerOrderId"]!);
        Assert.Equal((10L, 2L), Stock(f, s.Product));
        Assert.Equal(2, (int)(await Detail(s.Owner, id))["lines"]![0]!["reservedQty"]!);
        await Ok(Act(s.Owner, id, "accept")); var shipped = await Ok(Act(s.Owner, id, "ship", Ship()));
        Assert.Equal((8L, 0L), Stock(f, s.Product)); Assert.False((bool)shipped["stockMismatch"]!);
    }

    [Fact]
    public async Task A_commit_that_fails_on_ship_is_flagged_as_a_stock_mismatch()
    {
        var s = await NewShop(f, stock: 10);
        var (_, id, _, _) = await Place(s, qty: 2);
        f.WithDb(db => { db.Products.Where(p => p.Id == s.Product).ExecuteUpdate(u => u.SetProperty(p => p.Reserved, 0)); return 0; });   // the hold vanished
        await Ok(Act(s.Owner, id, "accept"));
        var shipped = await Ok(Act(s.Owner, id, "ship", Ship()));
        Assert.True((bool)shipped["stockMismatch"]!);
        Assert.Contains("could not be consumed", (string)shipped["timeline"]!.AsArray().First(e => (string)e!["type"]! == "stock.mismatch")!["text"]!);
    }
}

public class OrderServiceListingTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task A_service_listing_cannot_be_ordered_it_is_booked()
    {
        var biz = await Svc.NewBiz(f);                           // an ACTIVE service listing
        var shop = await NewShop(f);
        var r = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(biz.Seller, biz.Brand, biz.Service, 1)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var err = (await Node(r))["errors"]![0]!;
        Assert.Equal("not_a_product", (string)err["code"]!); Assert.Equal("Services are booked, not ordered.", (string)err["message"]!);
        Assert.Equal(0, f.WithDb(db => db.Orders.Count(o => o.SellerId == biz.Seller)));   // nothing created
        // a normal product order is unaffected
        var mixed = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(shop.Seller, shop.Brand, shop.Product, 1)]));
        Assert.Equal(HttpStatusCode.Created, mixed.StatusCode);                              // real products still work
    }
}
