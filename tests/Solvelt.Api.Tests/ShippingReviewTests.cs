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
    public async Task A_delivered_webhook_goes_to_the_label_that_owns_the_number_never_to_another_sellers_order()
    {
        using var f = new ShipFactory();
        var (_, idA, _, label, _) = await Bought(f);
        var b = await Ship.NewShop(f);
        var (_, idB, _) = await Ship.Accepted(b.Shop);
        await Orders.Ok(Orders.Act(b.Shop.Owner, idB, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000001")));
        f.WithDb(db => { db.Shipments.First(x => x.OrderId == idB).TrackingNumber = Ship.Tn(label); db.SaveChanges(); return 0; });   // a legacy duplicate that predates the guard
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal("applied", await Ship.Track(f, Ship.Tn(label), "delivered", Now(f)));
        Assert.Equal(OrderStatus.Delivered, Ship.Order(f, idA).Status);
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, idB).Status);
        Assert.Null(Ship.Shipments(f, idB).Single().DeliveredAt);
    }

    [Fact]
    public async Task When_two_unlabelled_shipments_share_a_number_the_update_is_applied_to_neither()
    {
        using var f = new ShipFactory();
        var (s, _, _) = await Ship.NewShop(f);
        var (_, a, _) = await Ship.Accepted(s); var (_, b, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, a, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000002")));
        await Orders.Ok(Orders.Act(s.Owner, b, "ship", Orders.Ship(carrier: "USPS", tracking: "9400111899223000000003")));
        f.WithDb(db => { db.Shipments.First(x => x.OrderId == b).TrackingNumber = "9400111899223000000002"; db.SaveChanges(); return 0; });
        Assert.Equal("ambiguous_tracking_number", await Ship.Track(f, "9400111899223000000002", "delivered", Now(f).AddMinutes(1)));
        Assert.Equal(OrderStatus.Shipped, Ship.Order(f, a).Status); Assert.Equal(OrderStatus.Shipped, Ship.Order(f, b).Status);
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
            db.TrackingEvents.First(e => e.OrderId == id).Location = "Springfield, IL"; db.SaveChanges(); return 0;
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
