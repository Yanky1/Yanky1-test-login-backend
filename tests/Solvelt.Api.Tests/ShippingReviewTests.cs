using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Shipping;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the orchestrator's review of the shipping module (payout timing, cross-account tracking, double purchase, PII, ordering, recovery).</summary>
public class ShippingReviewTests
{
    private static async Task<(Shop S, Guid Order, string Mid, JsonNode Label, Guid Pkg)> Bought(ShipFactory f)
    {
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s);
        return (s, id, mid, await Ship.BuyCheapest(s.Owner, id, pkg), pkg);
    }

    private static DateTime Now(ShipFactory f) => f.Time.GetUtcNow().UtcDateTime;

    // ---- 1: a carrier clock can never deliver before shipping ---------------------------------------------------------------------------------
    [Fact]
    public async Task A_delivery_time_before_the_shipment_shipped_is_clamped_so_payout_is_never_released_early()
    {
        using var f = new ShipFactory();
        var (s, id, mid, _, _) = await Bought(f);
        await Pay.Capture(f, mid, null);
        var when = await Pay.Run(f, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>(); var a = sp.GetRequiredService<OrderActions>();
            var o = await a.Full(db.Orders).FirstAsync(x => x.Id == id);
            var sh = o.Shipments.Single();
            Assert.True((await a.ShipmentDeliveredAsync(o, sh, sh.ShippedAt.AddDays(-30))).Ok);
            return sh.ShippedAt;
        });
        var o2 = Ship.Order(f, id); var sh2 = Ship.Shipments(f, id).Single();
        Assert.Equal(OrderStatus.Delivered, o2.Status);
        Assert.True(sh2.DeliveredAt >= when); Assert.True(o2.DeliveredAt >= when);
        Assert.Equal(0, (await Pay.Release(f)).Released);   // the hold runs from the (clamped) delivery time, so nothing is due today
    }

    // ---- 2: tracking numbers are not global ------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_manual_tracking_number_already_on_another_live_shipment_is_refused()
    {
        using var f = new ShipFactory();
        var (_, _, _, label, _) = await Bought(f);
        var other = await Ship.NewShop(f);
        var (_, id2, _) = await Ship.Accepted(other.Shop);
        var r = await Orders.Act(other.Shop.Owner, id2, "ship", Orders.Ship(carrier: "USPS", tracking: Ship.Tn(label)));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("tracking_in_use", await Orders.Code(r));
        Assert.Empty(Ship.Shipments(f, id2));
    }

    [Fact]
    public async Task The_database_refuses_two_live_parcels_with_one_number_and_a_stopped_one_frees_it()
    {
        using var f = new ShipFactory();
        var (_, idA, _, label, _) = await Bought(f);
        var b = await Ship.NewShop(f);
        var (_, idB, _) = await Ship.Accepted(b.Shop);
        await Orders.Ok(Orders.Act(b.Shop.Owner, idB, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000001")));
        // a legacy/parallel duplicate of a LIVE parcel's number cannot be stored at all
        Assert.ThrowsAny<Exception>(() => f.WithDb(db => { db.Shipments.First(x => x.OrderId == idB).TrackingNumber = Ship.Tn(label); db.SaveChanges(); return 0; }));
        // once B's tracking has stopped (undelivered, 60 days) the number is free again, and the label's parcel still gets the carrier's updates
        f.WithDb(db => { var s = db.Shipments.First(x => x.OrderId == idB); s.TrackingNumber = Ship.Tn(label); s.TrackingStoppedAt = f.Time.GetUtcNow().UtcDateTime; db.SaveChanges(); return 0; });
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal("applied", await Ship.Track(f, Ship.Tn(label), "delivered", Now(f)));
        Assert.Equal(OrderStatus.Delivered, Ship.Order(f, idA).Status);
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, idB).Status); Assert.Null(Ship.Shipments(f, idB).Single().DeliveredAt);
    }

    [Fact]
    public async Task A_number_reused_after_its_first_parcel_was_delivered_still_tracks_the_new_parcel()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, a, _) = await Ship.Accepted(s); var (_, b, _) = await Ship.Accepted(s);
        const string tn = "9400111899223000000002";
        await Orders.Ok(Orders.Act(s.Owner, a, "ship", Orders.Ship(carrier: "USPS", tracking: tn)));
        Assert.Equal("applied", await Ship.Track(f, tn, "delivered", Now(f).AddMinutes(1)));
        await Orders.Ok(Orders.Act(s.Owner, b, "ship", Orders.Ship(carrier: "USPS", tracking: tn)));          // allowed: the first parcel finished
        Assert.Equal("applied", await Ship.Track(f, tn, "in_transit", Now(f).AddMinutes(2)));                    // not "ambiguous"
        Assert.Equal("in_transit", Ship.Shipments(f, b).Single().TrackingStatus); Assert.Equal("delivered", Ship.Shipments(f, a).Single().TrackingStatus);
    }

    [Fact]
    public async Task A_delivery_stamped_before_the_parcel_shipped_does_not_deliver_it()
    {
        using var f = new ShipFactory();
        var (_, id, _, label, _) = await Bought(f);
        Assert.NotEqual("applied", await Ship.Track(f, Ship.Tn(label), "delivered", Now(f).AddDays(-30)));
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, id).Status);
    }

    // ---- 3: one purchase in flight per order ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Buying_two_different_rates_in_parallel_buys_one_label_and_the_other_gets_409_before_the_provider_is_called()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, id, pkg);
        var r0 = (string)q["rates"]![0]!["rateId"]!; var r1 = (string)q["rates"]![1]!["rateId"]!;
        f.Provider.PurchaseDelayMs = 150;
        var results = await Task.WhenAll(Ship.Buy(s.Owner, id, q, r0), Ship.Buy(s.Owner, id, q, r1));
        Assert.Equal(1, f.Provider.Purchases);
        Assert.Single(Ship.Journals(f, s.Seller, "shipping_label")); Assert.Single(Ship.Shipments(f, id));
        var loser = results.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("purchase_in_progress", await Orders.Code(loser));
    }

    // ---- 4: redaction covers the shipping data -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task Redaction_removes_label_files_tracking_text_customs_and_override_notes()
    {
        using var f = new ShipFactory();
        var (s, id, _, label, _) = await Bought(f);
        var lid = Guid.Parse((string)label["id"]!);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Get($"/api/v1/seller/shipping/labels/{lid}/file")).StatusCode);
        var fileKey = Ship.Labels(f, id).Single().LabelKey!;
        Assert.True(File.Exists(Path.Combine(f.Dir, "uploads", fileKey)));
        Assert.Equal("applied", await Ship.Track(f, Ship.Tn(label), "delivered", Now(f).AddMinutes(1)));
        f.WithDb(db =>
        {
            var l = db.ShippingLabels.First(x => x.Id == lid); l.CustomsJson = "{\"contents\":\"gift\"}"; l.AddressOverrideReason = "buyer lives at 12 Elm St";
            db.TrackingEvents.First(e => e.OrderId == id).Location = "Springfield, IL";
            db.ReturnShipments.Add(new ReturnShipment { SellerId = s.Seller, OrderId = id, ReturnId = Guid.NewGuid(), Carrier = "USPS", TrackingNumber = "9400111899223000000009", TrackingStatusDetail = "Left with the neighbour at 12 Elm St", CreatedAt = f.Time.GetUtcNow().UtcDateTime });
            db.SaveChanges(); return 0;
        });
        Func<Task> run = () => Pay.Run(f, sp => sp.GetRequiredService<OrderMaintenance>().RunAsync());
        f.Time.Advance(TimeSpan.FromDays(32)); await run();       // completed
        f.Time.Advance(TimeSpan.FromDays(91)); await run();       // redacted
        Assert.NotNull(Ship.Order(f, id).PiiRedactedAt);
        Assert.False(File.Exists(Path.Combine(f.Dir, "uploads", fileKey)));   // the file that printed the buyer's name and address is deleted (the endpoint answers 404 once LabelKey is null)
        var l2 = Ship.Labels(f, id).Single();
        Assert.Null(l2.LabelKey); Assert.Null(l2.CustomsJson); Assert.Null(l2.AddressOverrideReason);
        Assert.All(f.WithDb(db => db.TrackingEvents.AsNoTracking().Where(e => e.OrderId == id).ToList()), e => { Assert.Null(e.Location); Assert.Null(e.Detail); });
        Assert.Equal(1, Ship.Journals(f, s.Seller, "shipping_label").Count);   // the money record stays
        Assert.Null(f.WithDb(db => db.ReturnShipments.AsNoTracking().Single(r => r.OrderId == id).TrackingStatusDetail));   // return parcels carry carrier text too ("left with the neighbour at ...")
        Assert.Equal(0, f.WithDb(db => db.ShippingQuotes.Count(q => q.OrderId == id)));                                    // and the stored quotes hold the buyer's address check
    }

    // ---- 5: delivered is final, whatever the carrier clock says -------------------------------------------------------------------------------
    [Fact]
    public async Task Delivered_replaces_an_earlier_stage_even_when_that_stage_carries_a_later_carrier_time()
    {
        using var f = new ShipFactory();
        var (_, id, _, label, _) = await Bought(f);
        Assert.Equal("applied", await Ship.Track(f, Ship.Tn(label), "out_for_delivery", Now(f).AddHours(3)));
        Assert.Equal("applied", await Ship.Track(f, Ship.Tn(label), "delivered", Now(f).AddHours(1)));
        Assert.Equal(OrderStatus.Delivered, Ship.Order(f, id).Status);
        Assert.Equal("stale", await Ship.Track(f, Ship.Tn(label), "in_transit", Now(f).AddHours(9)));   // and nothing replaces it again
        Assert.Equal("delivered", Ship.Shipments(f, id).Single().TrackingStatus);
    }

    // ---- 6: purchases lost to a crash are recovered or released ------------------------------------------------------------------------
    [Fact]
    public async Task A_purchase_stuck_in_Purchasing_is_recovered_from_the_provider_by_key_or_released_when_the_provider_has_none()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s); var (_, id2, _) = await Ship.Accepted(s);
        async Task<Guid> Stuck(Guid order, string key, bool providerBought)
        {
            var q = await Ship.Quote(s.Owner, order, pkg); var rate = Ship.Cheapest(q);
            var qid = Guid.Parse((string)q["quoteId"]!);
            var l = new ShippingLabel
            {
                SellerId = s.Seller, BrandId = s.Brand, OrderId = order, Kind = LabelKind.Outbound, Status = LabelStatus.Purchasing, Provider = "fake", ProviderRateId = rate, QuoteId = qid,
                IdempotencyKey = key, Carrier = "USPS", Service = "Ground", CostCents = 700, Currency = "USD", CreatedAt = Now(f), PurchasedBy = Guid.NewGuid()
            };
            f.WithDb(db => { db.ShippingLabels.Add(l); db.SaveChanges(); return 0; });
            if (providerBought) await f.Provider.PurchaseAsync(new PurchaseRequest(rate, "", "PDF_4x6", key));
            return l.Id;
        }
        var bought = await Stuck(id, "k-bought", true); var lost = await Stuck(id2, "k-lost", false);
        Assert.Equal(0, (await Ship.Maintenance(f)).PurchasesRecovered);        // not stuck yet
        f.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, (await Ship.Maintenance(f)).PurchasesRecovered);
        var rows = f.WithDb(db => db.ShippingLabels.AsNoTracking().ToList());
        Assert.Equal(LabelStatus.Active, rows.Single(l => l.Id == bought).Status); Assert.NotNull(rows.Single(l => l.Id == bought).TrackingNumber);
        Assert.Equal(LabelStatus.Failed, rows.Single(l => l.Id == lost).Status);
        Assert.Single(Ship.Journals(f, s.Seller, "shipping_label"));            // the charge the provider made is on the ledger
        f.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, (await Ship.Maintenance(f)).OrphansVoided);              // never shipped: voided and credited back
    }

    // ---- 7: one bad parcel never stops the reconciler ----------------------------------------------------------------------------------------
    [Fact]
    public async Task One_parcel_that_blows_up_does_not_stop_the_poll_run()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, a, _) = await Ship.Accepted(s); var (_, b, _) = await Ship.Accepted(s);
        var la = await Ship.BuyCheapest(s.Owner, a, pkg); var lb = await Ship.BuyCheapest(s.Owner, b, pkg);
        f.Time.Advance(TimeSpan.FromHours(25));
        f.Provider.ThrowForTracking = Ship.Tn(la);
        f.Provider.Script("USPS", Ship.Tn(lb), "in_transit", Now(f));
        var summary = await Ship.Maintenance(f);
        Assert.Equal(2, f.Provider.TrackingPolls);
        Assert.Equal("in_transit", Ship.Shipments(f, b).Single().TrackingStatus);
        Assert.True(summary.Polled >= 1);
    }

    // ---- 8: no carrier time never makes a replay look new -----------------------------------------------------------------------------------
    [Fact]
    public async Task A_webhook_without_a_status_date_adds_no_history_when_replayed_under_another_event_id()
    {
        using var f = new ShipFactory();
        var (_, id, _, label, _) = await Bought(f);
        string Body(string ev) { var n = JsonNode.Parse(Ship.TrackBody(Ship.Tn(label), "in_transit", Now(f), ev))!; n["data"]!["tracking_status"]!.AsObject().Remove("status_date"); return n.ToJsonString(); }
        Assert.True((await Ship.Hook(f, Body("e1"))).IsSuccessStatusCode);
        var first = f.WithDb(db => db.TrackingEvents.Count(e => e.OrderId == id));
        Assert.True((await Ship.Hook(f, Body("e2"))).IsSuccessStatusCode);
        Assert.Equal(first, f.WithDb(db => db.TrackingEvents.Count(e => e.OrderId == id)));
    }
}

public class ShippingMoneyReviewTests
{
    [Fact]
    public async Task A_manual_or_marketplace_delivery_time_before_the_shipment_is_clamped_to_it()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(carrier: "UPS", tracking: "1Z999AA10123456811")));
        var r = await Orders.Inbound(f, $"/api/internal/v1/orders/{mid}/delivery-confirmations", new { deliveredAt = f.Time.GetUtcNow().UtcDateTime.AddDays(-90) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var o = Ship.Order(f, id);
        Assert.Equal(OrderStatus.Delivered, o.Status); Assert.True(o.DeliveredAt >= Ship.Shipments(f, id).Single().ShippedAt);   // never before it shipped: no early payout
    }

    [Fact]
    public async Task A_rate_in_another_currency_than_the_order_is_refused_before_the_provider_is_called()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        f.Provider.RateCurrency = "EUR";
        var q = await Ship.Quote(s.Owner, id, pkg);
        var r = await Ship.Buy(s.Owner, id, q);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); Assert.Equal("currency_mismatch", await Orders.Code(r));
        Assert.Equal(0, f.Provider.Purchases); Assert.Empty(Ship.Journals(f, s.Seller, "shipping_label")); Assert.Empty(Ship.Labels(f, id));
    }

    [Fact]
    public async Task A_return_rate_in_another_currency_than_the_order_is_refused()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s, qty: 2, lid: "RL1");
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        await Ship.Track(f, Ship.Tn(label), "delivered");
        Assert.Equal(HttpStatusCode.OK, (await Orders.Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R1", reason = "not_as_described", lines = new[] { new { marketplaceLineId = "RL1", quantity = 1 } } })).StatusCode);
        var rid = (string)(await Orders.Detail(s.Owner, id))["returns"]![0]!["id"]!;
        await Orders.Ok(Orders.Act(s.Owner, id, $"returns/{rid}/approve", new { note = "ok" }));
        var before = f.Provider.Purchases;
        f.Provider.RateCurrency = "EUR";
        var r = await s.Owner.Post($"/api/v1/seller/shipping/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); Assert.Equal("currency_mismatch", await Orders.Code(r));
        Assert.Equal(before, f.Provider.Purchases); Assert.Single(Ship.Journals(f, s.Seller, "shipping_label"));   // only the outbound label was ever debited
    }
}

/// <summary>Label links are signed with their own key, never with a secret the marketplace also holds.</summary>
public class ShippingLinkKeyTests
{
    private static LabelLinks Links(string linkKey, string ordersSecret) =>
        new(Microsoft.Extensions.Options.Options.Create(new ShippingOptions { LinkKey = linkKey, PublicBaseUrl = "https://api.test" }), TimeProvider.System);

    [Fact]
    public void The_signature_depends_on_the_link_key_and_not_on_the_orders_secrets()
    {
        var id = Guid.NewGuid();
        var (u1, _) = Links("key-one-0123456789abcdef", "orders-A").Create(id);
        Assert.Contains("sig=", u1);
        var sig = u1[(u1.IndexOf("sig=", StringComparison.Ordinal) + 4)..]; var exp = long.Parse(u1.Split("exp=")[1].Split('&')[0]);
        Assert.True(Links("key-one-0123456789abcdef", "orders-B").Valid(id, exp, sig));      // the Orders secret is irrelevant
        Assert.False(Links("key-two-0123456789abcdef", "orders-A").Valid(id, exp, sig));      // another link key does not verify it
    }

    [Fact]
    public void Production_refuses_to_start_without_a_link_key_or_with_an_orders_secret_as_the_key()
    {
        foreach (var (key, expected) in new[] { ("", "Shipping:LinkKey must be configured"), ("prod-outbound-secret-fedcba987654321", "must differ from the Orders secrets") })
        {
            using var f = new NoLinkKeyFactory(key);
            var ex = Record.Exception(() => f.CreateClient());
            Assert.NotNull(ex); Assert.Contains(expected, ex!.ToString());
        }
    }

    private sealed class NoLinkKeyFactory(string key) : ProductionFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder b) { base.ConfigureWebHost(b); b.UseSetting("Shipping:LinkKey", key); }
    }
}

public class ShippingRound3Tests
{
    private static DateTime Now(ShipFactory f) => f.Time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task A_manual_shipment_entered_after_drop_off_still_accepts_a_delivery_stamped_just_before_the_entry()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(carrier: "UPS", tracking: "1Z999AA10123456812")));
        Assert.Equal("applied", await Ship.Track(f, "1Z999AA10123456812", "delivered", Now(f).AddMinutes(-5)));   // the carrier scanned it 5 minutes before the seller typed the number in
        var o = Ship.Order(f, id);
        Assert.Equal(OrderStatus.Delivered, o.Status); Assert.True(o.DeliveredAt >= Ship.Shipments(f, id).Single().ShippedAt);   // and the time is clamped, never before shipping
    }

    [Fact]
    public async Task A_manual_shipment_still_ignores_a_delivery_far_older_than_the_grace_period()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(carrier: "UPS", tracking: "1Z999AA10123456813")));
        Assert.NotEqual("applied", await Ship.Track(f, "1Z999AA10123456813", "delivered", Now(f).AddDays(-30)));
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, id).Status);
    }

    [Fact]
    public async Task A_delivered_webhook_without_a_status_date_delivers_and_a_replay_adds_nothing()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(carrier: "UPS", tracking: "1Z999AA10123456814")));
        string Body(string ev) { var n = JsonNode.Parse(Ship.TrackBody("1Z999AA10123456814", "delivered", Now(f), ev, carrier: "ups"))!; n["data"]!["tracking_status"]!.AsObject().Remove("status_date"); return n.ToJsonString(); }
        Assert.True((await Ship.Hook(f, Body("d1"))).IsSuccessStatusCode);
        Assert.Equal(OrderStatus.Delivered, Ship.Order(f, id).Status);                                           // not dropped as "stale"
        var events = f.WithDb(db => db.TrackingEvents.Count(e => e.OrderId == id));
        f.Time.Advance(TimeSpan.FromMinutes(1));                                                                 // "heard" a minute later: still the same fact
        Assert.True((await Ship.Hook(f, Body("d2"))).IsSuccessStatusCode);
        Assert.Equal(events, f.WithDb(db => db.TrackingEvents.Count(e => e.OrderId == id)));
    }

    private static async Task<ReturnShipment> ReturnDelivered(bool noDate)
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s, qty: 2, lid: "RL1");
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        await Ship.Track(f, Ship.Tn(label), "delivered");
        Assert.Equal(HttpStatusCode.OK, (await Orders.Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R1", reason = "not_as_described", lines = new[] { new { marketplaceLineId = "RL1", quantity = 1 } } })).StatusCode);
        var rid = (string)(await Orders.Detail(s.Owner, id))["returns"]![0]!["id"]!;
        await Orders.Ok(Orders.Act(s.Owner, id, $"returns/{rid}/approve", new { note = "ok" }));
        var rl = (await Orders.Node(await s.Owner.Post($"/api/v1/seller/shipping/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) })))["label"]!;
        var n = JsonNode.Parse(Ship.TrackBody(Ship.Tn(rl), "delivered", Now(f).AddDays(-90)))!;
        if (noDate) n["data"]!["tracking_status"]!.AsObject().Remove("status_date");
        Assert.True((await Ship.Hook(f, n.ToJsonString())).IsSuccessStatusCode);
        return f.WithDb(db => db.ReturnShipments.AsNoTracking().Single(r => r.OrderId == id));
    }

    [Fact]
    public async Task A_return_delivered_without_a_date_is_not_stamped_1970()
    {
        var rs = await ReturnDelivered(noDate: true);
        Assert.NotNull(rs.DeliveredAt); Assert.True(rs.DeliveredAt >= rs.CreatedAt);
    }

    [Fact]
    public async Task A_return_delivery_dated_before_the_return_label_existed_is_clamped_to_it()
    {
        var rs = await ReturnDelivered(noDate: false);
        Assert.NotNull(rs.DeliveredAt); Assert.True(rs.DeliveredAt >= rs.CreatedAt);
    }

    [Fact]
    public async Task A_recovered_return_label_that_never_got_a_parcel_is_voided_by_the_sweep()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, id, pkg); var rate = Ship.Cheapest(q);
        var l = new ShippingLabel
        {
            SellerId = s.Seller, BrandId = s.Brand, OrderId = id, Kind = LabelKind.Return, ReturnId = Guid.NewGuid(), Status = LabelStatus.Purchasing, Provider = "fake", ProviderRateId = rate,
            IdempotencyKey = "k-ret", Carrier = "USPS", Service = "Ground", CostCents = 700, Currency = "USD", CreatedAt = Now(f), PurchasedBy = Guid.NewGuid()
        };
        f.WithDb(db => { db.ShippingLabels.Add(l); db.SaveChanges(); return 0; });
        await f.Provider.PurchaseAsync(new PurchaseRequest(rate, "", "PDF_4x6", "k-ret"));
        f.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, (await Ship.Maintenance(f)).PurchasesRecovered);
        f.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, (await Ship.Maintenance(f)).OrphansVoided);
        Assert.NotEqual(LabelStatus.Active, f.WithDb(db => db.ShippingLabels.AsNoTracking().Single(x => x.Id == l.Id).Status));
    }

    // ---- the duplicate-number guard -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_409_never_says_whose_number_it_is_and_covers_edit_tracking_and_the_csv_import()
    {
        using var f = new ShipFactory();
        var (_, _, _, label, _) = await Task.Run(async () => { var (s0, _, pkg0) = await Ship.NewShop(f); var (m0, i0, _) = await Ship.Accepted(s0); return (s0, i0, m0, await Ship.BuyCheapest(s0.Owner, i0, pkg0), pkg0); });
        var other = await Ship.NewShop(f);
        var (_, o1, _) = await Ship.Accepted(other.Shop); var (_, o2, _) = await Ship.Accepted(other.Shop);
        var tn = Ship.Tn(label);
        var manual = await Orders.Act(other.Shop.Owner, o1, "ship", Orders.Ship(carrier: "USPS", tracking: tn));
        Assert.Equal(HttpStatusCode.Conflict, manual.StatusCode); Assert.Equal("tracking_in_use", await Orders.Code(manual));
        Assert.DoesNotContain("another", (string)(await Orders.Node(manual))["title"]!, StringComparison.OrdinalIgnoreCase);   // the answer never says someone else holds the number
        // edit tracking
        await Orders.Ok(Orders.Act(other.Shop.Owner, o2, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000010")));
        var sid = (string)(await Orders.Detail(other.Shop.Owner, o2))["shipments"]![0]!["id"]!;
        var edit = await other.Shop.Owner.Put($"/api/v1/seller/orders/{o2}/shipments/{sid}", new { carrier = "USPS", trackingNumber = tn });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode); Assert.Equal("tracking_in_use", await Orders.Code(edit));
        // csv import (apply)
        var number = (string)(await Orders.Detail(other.Shop.Owner, o1))["number"]!;
        var form = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"order_number,carrier,tracking_number\n{number},USPS,{tn}\n")), "file", "tracking.csv" } };
        var csv = await Orders.Node(await other.Shop.Owner.Send(new HttpRequestMessage(HttpMethod.Post, "/api/v1/seller/orders/import-tracking?apply=true") { Content = form }));
        Assert.False((bool)csv["results"]![0]!["ok"]!); Assert.Equal("Accepted", Orders.St(await Orders.Detail(other.Shop.Owner, o1)));
    }

    [Fact]
    public async Task The_same_string_under_two_different_carriers_is_two_parcels_and_webhooks_route_by_carrier()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, a, _) = await Ship.Accepted(s); var (_, b, _) = await Ship.Accepted(s);
        const string tn = "94001118992230000000";                                                                 // valid as FedEx AND as USPS
        await Orders.Ok(Orders.Act(s.Owner, a, "ship", Orders.Ship(carrier: "FedEx", tracking: tn)));
        await Orders.Ok(Orders.Act(s.Owner, b, "ship", Orders.Ship(carrier: "USPS", tracking: tn)));            // numbers are unique per carrier: no 409, no voided label
        Assert.Equal("applied", await Ship.Track(f, tn, "in_transit", Now(f), carrier: "fedex"));
        Assert.Equal("in_transit", Ship.Shipments(f, a).Single().TrackingStatus); Assert.Null(Ship.Shipments(f, b).Single().TrackingStatus);
        Assert.Equal("applied", await Ship.Track(f, tn, "pre_transit", Now(f), carrier: "usps"));
        Assert.Equal("pre_transit", Ship.Shipments(f, b).Single().TrackingStatus); Assert.Equal("in_transit", Ship.Shipments(f, a).Single().TrackingStatus);
    }

    [Fact]
    public async Task Two_entries_of_one_number_that_both_pass_the_check_are_stopped_by_the_index_and_the_loser_gets_409()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var ids = new List<Guid> { (await Ship.Accepted(s)).Id, (await Ship.Accepted(s)).Id };
        var hooks = f.Services.GetRequiredService<ShippingTestHooks>();
        var arrived = 0; var both = new TaskCompletionSource();
        hooks.AfterTrackingCheck = async () => { if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult(); await Task.WhenAny(both.Task, Task.Delay(5000)); };   // each waits until BOTH have passed the check: a real race
        try
        {
            var results = await Task.WhenAll(ids.Select(i => Orders.Act(s.Owner, i, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000020"))));
            Assert.Equal(2, arrived);
            Assert.Equal(1, results.Count(r => r.IsSuccessStatusCode));
            var loser = results.Single(r => !r.IsSuccessStatusCode); Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode); Assert.Equal("tracking_in_use", await Orders.Code(loser));
            Assert.Equal(1, f.WithDb(db => db.Shipments.Count(x => x.TrackingNumber == "9400111899223000000020")));
        }
        finally { hooks.AfterTrackingCheck = null; }
    }

    [Fact]
    public async Task A_parcel_whose_tracking_stopped_undelivered_does_not_block_its_number()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, a, _) = await Ship.Accepted(s); var (_, b, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, a, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000030")));
        f.WithDb(db => { db.Shipments.First(x => x.OrderId == a).TrackingStoppedAt = f.Time.GetUtcNow().UtcDateTime; db.SaveChanges(); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await Orders.Act(s.Owner, b, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000030"))).StatusCode);
    }
}

/// <summary>The Shippo stuck-purchase lookup pages through the history and never concludes "not bought" from a partial search.</summary>
public class ShippoPurchaseLookupTests
{
    private sealed class Pages(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Seen.Add(r.RequestUri!); return Task.FromResult(answer(r.RequestUri!)); }
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    private static ShippoShippingProvider Provider(Pages h) => new(new HttpClient(h), Microsoft.Extensions.Options.Options.Create(new ShippingOptions { ShippoApiKey = "shippo_test_x" }), Microsoft.Extensions.Logging.Abstractions.NullLogger<ShippoShippingProvider>.Instance);
    private static string Row(string meta, string created) => $"{{\"object_id\":\"tx_{meta}\",\"metadata\":\"{meta}\",\"status\":\"SUCCESS\",\"object_created\":\"{created}\",\"label_url\":\"https://labels.test/{meta}.pdf\",\"tracking_number\":\"TN{meta}\",\"rate\":\"r1\"}}";

    [Fact]
    public async Task An_older_purchase_on_a_later_page_is_found()
    {
        var since = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var h = new Pages(u => u.AbsolutePath.StartsWith("/transactions") && !u.Query.Contains("page=2") ? Json($"{{\"next\":\"https://api.goshippo.com/transactions/?results=100&page=2\",\"results\":[{Row("other", "2026-11-04T13:00:00Z")}]}}")
            : u.AbsolutePath.StartsWith("/transactions") ? Json($"{{\"next\":null,\"results\":[{Row("mine", "2026-11-04T12:00:05Z")}]}}")
            : u.AbsolutePath.StartsWith("/rates") ? Json("{\"provider\":\"USPS\",\"servicelevel\":{\"name\":\"Ground\"},\"estimated_days\":3}") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var found = await Provider(h).FindPurchaseAsync("mine", "PDF_4x6", since);
        Assert.NotNull(found); Assert.Equal("tx_mine", found!.TransactionId);
        Assert.Contains(h.Seen, u => u.Query.Contains("page=2"));
    }

    [Fact]
    public async Task A_next_link_to_a_foreign_host_is_an_incomplete_search_not_a_not_found()
    {
        var since = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var h = new Pages(_ => Json($"{{\"next\":\"https://evil.example/transactions/?page=2\",\"results\":[{Row("other", "2026-11-04T13:00:00Z")}]}}"));
        await Assert.ThrowsAsync<ShippingProviderException>(() => Provider(h).FindPurchaseAsync("mine", "PDF_4x6", since));
        Assert.Single(h.Seen, u => u.AbsolutePath.StartsWith("/transactions"));                                        // it does not follow the link at all: one request, then it gives up
    }

    [Fact]
    public async Task An_endless_history_stops_at_the_page_cap_with_an_error()
    {
        var since = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var h = new Pages(_ => Json($"{{\"next\":\"https://api.goshippo.com/transactions/?results=100&page=2\",\"results\":[{Row("other", "2026-11-04T13:00:00Z")}]}}"));
        await Assert.ThrowsAsync<ShippingProviderException>(() => Provider(h).FindPurchaseAsync("mine", "PDF_4x6", since));
        Assert.Equal(50, h.Seen.Count(u => u.AbsolutePath.StartsWith("/transactions")));
    }

    [Fact]
    public async Task Not_found_after_a_complete_search_is_null_but_a_failed_page_is_an_error_never_null()
    {
        var since = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var done = new Pages(_ => Json($"{{\"next\":null,\"results\":[{Row("other", "2026-11-04T13:00:00Z")}]}}"));
        Assert.Null(await Provider(done).FindPurchaseAsync("mine", "PDF_4x6", since));
        var broken = new Pages(u => u.Query.Contains("page=2") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json($"{{\"next\":\"https://api.goshippo.com/transactions/?results=100&page=2\",\"results\":[{Row("other", "2026-11-04T13:00:00Z")}]}}"));
        await Assert.ThrowsAsync<ShippingProviderException>(() => Provider(broken).FindPurchaseAsync("mine", "PDF_4x6", since));   // the search was cut short: the caller must retry, not mark the label failed
    }
}

/// <summary>Round 4 of the review: restored routing scenarios, reused numbers, undated replays, stuck purchases, lookups, the migration's data step.</summary>
public class ShippingRound4Tests
{
    private static DateTime Now(ShipFactory f) => f.Time.GetUtcNow().UtcDateTime;
    private static void DropLiveIndex(ShipFactory f) => f.WithDb(db => { db.Database.ExecuteSqlRaw("DROP INDEX UX_order_shipments_live_tracking"); return 0; });   // data that predates the index

    // ---- routing, restored ----------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_label_that_owns_a_number_wins_over_a_live_manual_parcel_with_the_same_number()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, idA, _) = await Ship.Accepted(s); var (_, idB, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, idA, pkg);
        await Orders.Ok(Orders.Act(s.Owner, idB, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000041")));
        DropLiveIndex(f);
        f.WithDb(db => { db.Shipments.First(x => x.OrderId == idB).TrackingNumber = Ship.Tn(label); db.SaveChanges(); return 0; });   // two LIVE parcels, one number
        Assert.Equal("applied", await Ship.Track(f, Ship.Tn(label), "delivered", Now(f)));
        Assert.Equal(OrderStatus.Delivered, Ship.Order(f, idA).Status);
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, idB).Status); Assert.Null(Ship.Shipments(f, idB).Single().DeliveredAt);
    }

    [Fact]
    public async Task Two_live_unlabelled_parcels_with_one_number_mean_the_update_is_applied_to_neither()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, a, _) = await Ship.Accepted(s); var (_, b, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, a, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000042")));
        await Orders.Ok(Orders.Act(s.Owner, b, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000043")));
        DropLiveIndex(f);
        f.WithDb(db => { db.Shipments.First(x => x.OrderId == b).TrackingNumber = "9400111899223000000042"; db.SaveChanges(); return 0; });
        Assert.Equal("ambiguous_tracking_number", await Ship.Track(f, "9400111899223000000042", "delivered", Now(f).AddMinutes(1)));
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, a).Status); Assert.Equal(OrderStatus.Shipped, Ship.Order(f, b).Status);
    }

    [Fact]
    public async Task A_return_number_reused_after_the_first_return_parcel_arrived_goes_to_the_live_return_parcel()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        await Ship.BuyCheapest(s.Owner, id, pkg);
        var now = Now(f); const string tn = "9400111899223000000044";
        f.WithDb(db =>
        {
            db.ReturnShipments.Add(new ReturnShipment { SellerId = s.Seller, OrderId = id, ReturnId = Guid.NewGuid(), Carrier = "USPS", TrackingNumber = tn, CreatedAt = now.AddDays(-5), DeliveredAt = now.AddDays(-3) });
            db.ReturnShipments.Add(new ReturnShipment { SellerId = s.Seller, OrderId = id, ReturnId = Guid.NewGuid(), Carrier = "USPS", TrackingNumber = tn, CreatedAt = now.AddMinutes(-1) });
            db.SaveChanges(); return 0;
        });
        Assert.Equal("applied", await Ship.Track(f, tn, "delivered", now));
        var rows = f.WithDb(db => db.ReturnShipments.AsNoTracking().Where(r => r.TrackingNumber == tn).OrderBy(r => r.CreatedAt).ToList());
        Assert.Equal(now.AddDays(-3), rows[0].DeliveredAt); Assert.NotNull(rows[1].DeliveredAt);                    // the live one arrived, the old one is untouched
    }

    // ---- reused numbers never deliver the NEW parcel on the OLD parcel's delivery ------------------------------------------------------------
    [Fact]
    public async Task A_resent_delivery_of_the_previous_parcel_does_not_deliver_a_new_parcel_that_reused_the_number()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, a, _) = await Ship.Accepted(s); var (_, b, _) = await Ship.Accepted(s);
        const string tn = "1Z999AA10123456820";
        await Orders.Ok(Orders.Act(s.Owner, a, "ship", Orders.Ship(carrier: "UPS", tracking: tn)));
        var stamped = Now(f).AddMinutes(-5);
        Assert.Equal("applied", await Ship.Track(f, tn, "delivered", stamped, "first"));
        f.Time.Advance(TimeSpan.FromMinutes(2));
        await Orders.Ok(Orders.Act(s.Owner, b, "ship", Orders.Ship(carrier: "UPS", tracking: tn)));                   // allowed: the first parcel finished
        Assert.NotEqual("applied", await Ship.Track(f, tn, "delivered", stamped, "resent"));                           // the same old delivery arrives again
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, b).Status); Assert.Null(Ship.Shipments(f, b).Single().DeliveredAt);
        Assert.Equal("applied", await Ship.Track(f, tn, "delivered", Now(f).AddMinutes(1), "real"));                   // a genuinely later delivery still delivers it
        Assert.Equal(OrderStatus.Delivered, Ship.Order(f, b).Status);
    }

    [Fact]
    public async Task A_backdated_ship_date_does_not_widen_the_window_for_a_manual_parcel()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(carrier: "UPS", tracking: "1Z999AA10123456821")));
        f.WithDb(db => { var sh = db.Shipments.First(x => x.OrderId == id); sh.ShippedAt = sh.CreatedAt.AddDays(-10); db.SaveChanges(); return 0; });   // the seller typed an old ship date
        Assert.NotEqual("applied", await Ship.Track(f, "1Z999AA10123456821", "delivered", Now(f).AddDays(-5)));        // before the entry time minus the grace: not this parcel
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, id).Status);
    }

    [Fact]
    public async Task A_label_parcel_has_no_grace_a_delivery_before_the_label_was_bought_is_ignored()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        var bought = Ship.Labels(f, id).Single().PurchasedAt!.Value;
        Assert.NotEqual("applied", await Ship.Track(f, Ship.Tn(label), "delivered", bought.AddHours(-1)));             // a manual parcel would accept this (48 h grace); a label cannot
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, id).Status);
    }

    // ---- undated replays ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_older_undated_replay_does_not_roll_the_current_status_back_or_re_send_an_event()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s);
        const string tn = "1Z999AA10123456822";
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(carrier: "UPS", tracking: tn)));
        string Undated(string ev, string status) { var n = JsonNode.Parse(Ship.TrackBody(tn, status, Now(f), ev, carrier: "ups"))!; n["data"]!["tracking_status"]!.AsObject().Remove("status_date"); return n.ToJsonString(); }
        Assert.True((await Ship.Hook(f, Undated("u1", "in_transit"))).IsSuccessStatusCode);
        f.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("applied", await Ship.Track(f, tn, "out_for_delivery", Now(f), "dated"));
        f.Time.Advance(TimeSpan.FromMinutes(1));
        var events = Ship.Outbox(f, mid).Count(x => x.Type == "order.tracking_updated");
        Assert.True((await Ship.Hook(f, Undated("u2", "in_transit"))).IsSuccessStatusCode);                           // the same old undated fact again, stamped later
        Assert.Equal("out_for_delivery", Ship.Shipments(f, id).Single().TrackingStatus);
        Assert.Equal(events, Ship.Outbox(f, mid).Count(x => x.Type == "order.tracking_updated"));
    }

    // ---- migration data step ---------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_migration_stops_all_but_the_newest_live_duplicate_so_the_unique_index_can_be_created()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var ids = new List<Guid>(); for (var i = 0; i < 4; i++) { var o = await Ship.Accepted(s); ids.Add(o.Id); await Orders.Ok(Orders.Act(s.Owner, o.Id, "ship", Orders.Ship(carrier: "USPS", tracking: "94001118992230000001" + i))); }
        DropLiveIndex(f);
        f.WithDb(db =>
        {
            var rows = db.Shipments.Where(x => ids.Contains(x.OrderId)).OrderBy(x => x.CreatedAt).ToList();
            rows[1].TrackingNumber = rows[0].TrackingNumber; rows[2].TrackingNumber = rows[0].TrackingNumber;   // three live duplicates; the fourth is unrelated
            rows[1].CreatedAt = rows[0].CreatedAt.AddMinutes(1); rows[2].CreatedAt = rows[0].CreatedAt.AddMinutes(2);
            db.SaveChanges(); return 0;
        });
        f.WithDb(db => db.Database.ExecuteSqlRaw(ShippingMigrationSql.StopOlderLiveDuplicates(postgres: false)));
        var after = f.WithDb(db => db.Shipments.AsNoTracking().Where(x => ids.Contains(x.OrderId)).ToList());
        var dupNumber = after.GroupBy(x => x.TrackingNumber).Single(g => g.Count() == 3).Key;
        var dups = after.Where(x => x.TrackingNumber == dupNumber).OrderBy(x => x.CreatedAt).ToList(); var other = after.Single(x => x.TrackingNumber != dupNumber);
        Assert.NotNull(dups[0].TrackingStoppedAt); Assert.NotNull(dups[1].TrackingStoppedAt);                           // the older two stop receiving updates
        Assert.Null(dups[2].TrackingStoppedAt); Assert.Null(other.TrackingStoppedAt);                                     // the newest of the three and the unrelated one stay live
        f.WithDb(db => db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX UX_order_shipments_live_tracking ON order_shipments (Carrier, TrackingNumber) WHERE \"Method\" = 'carrier' AND \"DeliveredAt\" IS NULL AND \"TrackingStoppedAt\" IS NULL"));   // and now it can be created
    }

    // ---- stuck purchases that never resolve ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_purchase_the_provider_search_cannot_resolve_is_retried_then_flagged_for_staff_after_the_abandon_period()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, id, pkg); var rate = Ship.Cheapest(q);
        var l = new ShippingLabel
        {
            SellerId = s.Seller, BrandId = s.Brand, OrderId = id, Kind = LabelKind.Outbound, Status = LabelStatus.Purchasing, Provider = "fake", ProviderRateId = rate, IdempotencyKey = "k-never",
            Carrier = "USPS", Service = "Ground", CostCents = 700, Currency = "USD", CreatedAt = Now(f), PurchasedBy = Guid.NewGuid()
        };
        f.WithDb(db => { db.ShippingLabels.Add(l); db.SaveChanges(); return 0; });
        f.Provider.FailLookup = true;
        f.Time.Advance(TimeSpan.FromMinutes(11)); await Ship.Maintenance(f);
        Assert.Equal(LabelStatus.Purchasing, f.WithDb(db => db.ShippingLabels.AsNoTracking().Single(x => x.Id == l.Id).Status));        // still retried: the search is incomplete
        f.Time.Advance(TimeSpan.FromDays(4)); await Ship.Maintenance(f);
        var after = f.WithDb(db => db.ShippingLabels.AsNoTracking().Single(x => x.Id == l.Id));
        Assert.Equal(LabelStatus.Failed, after.Status); Assert.Equal("review", after.RefundStatus); Assert.Equal("purchase_unresolved", after.Error);   // no more calls; staff see it under refunds?state=review
        f.Provider.FailLookup = false;
    }
}
