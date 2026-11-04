using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;
using Solvelt.Api.Shipping;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Tests;

public class ShippingLabelTests
{
    private const string Base = "/api/v1/seller/shipping";

    // ---- rates ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Rates_are_sorted_by_price_and_only_the_cheapest_that_meets_deliver_by_is_recommended()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, id, pkg);
        var rates = q["rates"]!.AsArray();
        Assert.Equal(4, rates.Count);
        var prices = rates.Select(r => (long)r!["amountCents"]!).ToList();
        Assert.Equal(prices.OrderBy(x => x), prices);
        // deliver-by is 2 (handling) + 5 (standard) days out: every service arrives in time, the CHEAPEST is recommended
        var rec = rates.Where(r => (bool)r!["recommended"]!).ToList();
        Assert.Single(rec); Assert.Equal((string)rates[0]!["rateId"]!, (string)rec[0]!["rateId"]!);
        Assert.Equal(new[] { "USPS" }, rates.Take(1).Select(r => (string)r!["carrier"]!));
        Assert.Equal(1, f.WithDb(db => db.ShippingQuotes.Count(x => x.OrderId == id)));   // the quote is stored for the purchase
    }

    [Fact]
    public async Task Recommended_rate_respects_the_orders_deliver_by_date()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        // tight deadline: only the overnight service arrives in time
        f.WithDb(db => { db.Orders.First(o => o.Id == id).DeliverByAt = f.Time.GetUtcNow().UtcDateTime.AddDays(1); db.SaveChanges(); return 0; });
        var q = await Ship.Quote(s.Owner, id, pkg);
        var rec = q["rates"]!.AsArray().Single(r => (bool)r!["recommended"]!)!;
        Assert.Equal("FedEx", (string)rec["carrier"]!); Assert.Equal("Overnight", (string)rec["service"]!);
        Assert.Equal(1, q["rates"]!.AsArray().Count(r => (bool)r!["meetsDeliverBy"]!));
        // impossible deadline: nothing is recommended, rates are still offered
        f.WithDb(db => { db.Orders.First(o => o.Id == id).DeliverByAt = f.Time.GetUtcNow().UtcDateTime.AddDays(-3); db.SaveChanges(); return 0; });
        var q2 = await Ship.Quote(s.Owner, id, pkg);
        Assert.DoesNotContain(q2["rates"]!.AsArray(), r => (bool)r!["recommended"]!);
    }

    [Fact]
    public void Recommended_logic_is_cheapest_among_those_in_time()
    {
        var by = new DateTime(2026, 5, 10);
        var offers = new[]
        {
            new RateOffer("a", "USPS", "Slow", 300, "USD", 9, by.AddDays(2)), new RateOffer("b", "UPS", "Ground", 700, "USD", 4, by.AddDays(-1)),
            new RateOffer("c", "FedEx", "Express", 900, "USD", 2, by.AddDays(-3)), new RateOffer("d", "UPS", "Ground2", 700, "USD", 3, by)
        };
        var ranked = LabelService.Rank(offers, by);
        Assert.Equal(["a", "d", "b", "c"], ranked.Select(r => r.RateId).ToArray());   // price, then fewer days
        Assert.Equal("d", ranked.Single(r => r.Recommended).RateId);          // cheapest in time; a tie goes to the faster service
        Assert.False(ranked.Single(r => r.RateId == "a").MeetsDeliverBy);
    }

    // ---- purchase ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Buying_a_label_creates_exactly_one_shipment_with_events_stock_label_file_and_ledger_debit()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s, qty: 2);
        Assert.Equal((10L, 2L), Orders.Stock(f, s.Product));
        var q = await Ship.Quote(s.Owner, id, pkg);
        var r = await Ship.Buy(s.Owner, id, q);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var label = (await Orders.Node(r))["label"]!;
        Assert.Equal("active", (string)label["status"]!);
        var ships = Ship.Shipments(f, id);
        var sh = Assert.Single(ships);
        Assert.Equal(Ship.Tn(label), sh.TrackingNumber); Assert.Equal((string)label["carrier"]!, sh.Carrier); Assert.NotNull(sh.LabelId);
        var o = Ship.Order(f, id);
        Assert.Equal(OrderStatus.Shipped, o.Status);
        Assert.Equal((8L, 0L), Orders.Stock(f, s.Product));                     // reserved units committed
        var types = Ship.Outbox(f, mid).Select(x => x.Type).ToList();
        Assert.Single(types, "order.shipped");
        var shipped = Ship.Outbox(f, mid).Single(x => x.Type == "order.shipped").Data;
        Assert.Equal(Ship.Tn(label), (string)shipped["trackingNumber"]!);
        var detail = await Orders.Detail(s.Owner, id);
        Assert.Equal("order.shipped", (string)detail["timeline"]!.AsArray().Last()!["type"]!);
        Assert.Equal((long)label["costCents"]!, (long)detail["shipments"]![0]!["labelCostCents"]!);
        // label file is served through the authorised endpoint only
        var file = await s.Owner.Get((string)label["fileUrl"]!);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode); Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", await file.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get((string)label["fileUrl"]!, false)).StatusCode);
        // ledger: a debit of exactly the label cost against the seller, balanced by the platform
        var j = Assert.Single(Ship.Journals(f, s.Seller, "shipping_label"));
        Assert.Equal(id, j.OrderId); Assert.Equal(0, j.Entries.Sum(e => e.AmountCents));
        Assert.Equal(-(long)label["costCents"]!, j.Entries.Single(e => e.Account == LedgerAccount.Available).AmountCents);
        Assert.Equal(1, f.Provider.Purchases);
    }

    [Fact]
    public async Task Partial_shipments_buy_one_label_per_shipment_for_the_chosen_lines()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s, qty: 3);
        var line = await Orders.LineId(s.Owner, id);
        var q = await Ship.Quote(s.Owner, id, pkg, new { lines = new[] { new { lineId = line, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Created, (await Ship.Buy(s.Owner, id, q)).StatusCode);
        Assert.Equal("PartiallyShipped", Orders.St(await Orders.Detail(s.Owner, id)));
        var q2 = await Ship.Quote(s.Owner, id, pkg);                              // the remaining 2 units
        Assert.Equal(2, (int)q2["lines"]![0]!["quantity"]!);
        Assert.Equal(HttpStatusCode.Created, (await Ship.Buy(s.Owner, id, q2)).StatusCode);
        Assert.Equal("Shipped", Orders.St(await Orders.Detail(s.Owner, id)));
        Assert.Equal(2, Ship.Shipments(f, id).Count);
        // more than is left is refused before anything is bought
        var over = await s.Owner.Post($"{Base}/orders/{id}/rates", new { package = Ship.Pkg(pkg), lines = new[] { new { lineId = line, quantity = 5 } } });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
    }

    [Fact]
    public async Task A_double_submit_or_retry_with_the_same_key_buys_once()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, id, pkg);
        var first = await Ship.Buy(s.Owner, id, q, key: "click-1");
        var second = await Ship.Buy(s.Owner, id, q, key: "click-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((bool)(await Orders.Node(second))["replayed"]!);
        // a different key for the SAME quoted rate is still the same purchase
        var third = await Ship.Buy(s.Owner, id, q, key: "click-2");
        Assert.True((bool)(await Orders.Node(third))["replayed"]!);
        // no key at all: derived from order + quote + rate
        var fourth = await Ship.Buy(s.Owner, id, q);
        Assert.True((bool)(await Orders.Node(fourth))["replayed"]!);
        Assert.Equal(1, f.Provider.Purchases);
        Assert.Single(Ship.Shipments(f, id)); Assert.Single(Ship.Journals(f, s.Seller, "shipping_label"));
    }

    [Fact]
    public async Task Parallel_purchases_of_the_same_rate_buy_once()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, id, pkg);
        f.Provider.PurchaseDelayMs = 150;                                          // keeps the first purchase "in flight" while the others arrive
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Ship.Buy(s.Owner, id, q, key: "same-key")));
        Assert.Equal(1, f.Provider.Purchases);
        Assert.Single(Ship.Shipments(f, id)); Assert.Single(Ship.Journals(f, s.Seller, "shipping_label"));
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(results, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict }));
        var live = Ship.Labels(f, id).Where(l => l.Status == LabelStatus.Active).ToList();
        Assert.Single(live);
    }

    [Fact]
    public async Task The_database_allows_one_live_label_per_shipment()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        var shipment = Ship.Shipments(f, id)[0].Id;
        Assert.ThrowsAny<DbUpdateException>(() => f.WithDb(db =>
        {
            db.ShippingLabels.Add(new ShippingLabel { SellerId = s.Seller, BrandId = s.Brand, OrderId = id, ShipmentId = shipment, ActiveShipmentKey = shipment, Status = LabelStatus.Active, IdempotencyKey = "dup-" + Guid.NewGuid(), Carrier = "UPS" });
            db.SaveChanges(); return 0;
        }));
        Assert.Equal("active", (string)label["status"]!);
    }

    [Fact]
    public async Task Expired_quotes_and_provider_side_rate_expiry_ask_for_new_rates_and_cost_nothing()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, id, pkg);
        f.Time.Advance(TimeSpan.FromMinutes(16));
        await s.Owner.LoginAs(Ship.OwnerEmail(f, s.Seller), ApiFactory.Strong); await s.Owner.Switch(s.Seller);
        var late = await Ship.Buy(s.Owner, id, q);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); Assert.Equal("rate_expired", await Orders.Code(late));
        Assert.True((bool)(await Orders.Node(late))["errors"]!["requote"]!);
        Assert.Equal(0, f.Provider.Purchases);
        // the provider expires the rate itself: label row failed, nothing in the ledger, a re-quote works
        var q2 = await Ship.Quote(s.Owner, id, pkg);
        f.Provider.ExpireRates = true;
        var gone = await Ship.Buy(s.Owner, id, q2);
        Assert.Equal("rate_expired", await Orders.Code(gone));
        Assert.Empty(Ship.Journals(f, s.Seller, "shipping_label")); Assert.Empty(Ship.Shipments(f, id));
        Assert.Equal(LabelStatus.Failed, Assert.Single(Ship.Labels(f, id)).Status);
        f.Provider.ExpireRates = false;
        var q3 = await Ship.Quote(s.Owner, id, pkg);
        Assert.Equal(HttpStatusCode.Created, (await Ship.Buy(s.Owner, id, q3)).StatusCode);
    }

    [Fact]
    public async Task A_provider_failure_buys_nothing_and_a_retry_with_the_same_key_can_succeed()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, id, pkg);
        f.Provider.FailPurchaseWith = "provider_error";
        var bad = await Ship.Buy(s.Owner, id, q, key: "k1");
        Assert.Equal(HttpStatusCode.BadGateway, bad.StatusCode);
        Assert.Empty(Ship.Journals(f, s.Seller, "shipping_label")); Assert.Equal("Accepted", Orders.St(await Orders.Detail(s.Owner, id)));
        f.Provider.FailPurchaseWith = null;
        var ok = await Ship.Buy(s.Owner, id, q, key: "k1");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Single(Ship.Shipments(f, id));
    }

    // ---- address validation + customs -------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Unverified_buyer_address_needs_a_ship_anyway_reason_which_is_audited()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        f.WithDb(db => { db.Orders.First(o => o.Id == id).ShipPostalCode = "00000"; db.SaveChanges(); return 0; });
        var chk = await Orders.Node(await s.Owner.Post($"{Base}/orders/{id}/validate-address"));
        Assert.Equal("unverified", (string)chk["status"]!);
        var q = await Ship.Quote(s.Owner, id, pkg);
        Assert.Equal("unverified", (string)q["addressStatus"]!);
        var blocked = await Ship.Buy(s.Owner, id, q);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode); Assert.Equal("address_unverified", await Orders.Code(blocked));
        Assert.Equal(0, f.Provider.Purchases);
        var tooShort = await Ship.Buy(s.Owner, id, q, shipAnyway: new { reason = "ok" });
        Assert.Equal("address_unverified", await Orders.Code(tooShort));
        var ok = await Ship.Buy(s.Owner, id, q, shipAnyway: new { reason = "Buyer confirmed the address by message" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Contains(f.WithDb(db => db.AuditEvents.Where(a => a.Type == "shipping.address_override").Select(a => a.Detail).ToList()), d => d!.Contains("Buyer confirmed"));
        Assert.Equal("Buyer confirmed the address by message", Ship.Labels(f, id).Single().AddressOverrideReason);
    }

    [Fact]
    public async Task International_orders_need_customs_value_comes_from_the_order_snapshot()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id) = await Ship.AcceptedAbroad(s, "CA", price: 2500);          // 2 x 25.00
        var noCustoms = await s.Owner.Post($"{Base}/orders/{id}/rates", new { package = Ship.Pkg(pkg) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noCustoms.StatusCode); Assert.Equal("customs_required", await Orders.Code(noCustoms));
        var badHs = await s.Owner.Post($"{Base}/orders/{id}/rates", new { package = Ship.Pkg(pkg), customs = new { contentsType = "merchandise", hsCode = "ABC" } });
        Assert.Equal("customs_hs", await Orders.Code(badHs));
        var q = await Ship.Quote(s.Owner, id, pkg, new { customs = new { contentsType = "merchandise", nonDeliveryOption = "return", hsCode = "6912.00" } });
        Assert.True((bool)q["international"]!);
        Assert.All(q["rates"]!.AsArray(), r => Assert.Contains((string)r!["carrier"]!, new[] { "USPS", "DHL" }));   // the international plan
        Assert.Equal(HttpStatusCode.Created, (await Ship.Buy(s.Owner, id, q)).StatusCode);
        var customs = JsonNode.Parse(Ship.Labels(f, id).Single().CustomsJson!)!;
        Assert.Equal("merchandise", (string)customs["contentsType"]!);
        var item = customs["items"]![0]!;
        Assert.Equal(5000, (long)item["valueCents"]!); Assert.Equal(2, (int)item["quantity"]!); Assert.Equal("6912.00", (string)item["hsCode"]!); Assert.Equal("US", (string)item["originCountry"]!);
        // a domestic order never asks for customs
        var (_, dom, _) = await Ship.Accepted(s);
        Assert.False((bool)(await Ship.Quote(s.Owner, dom, pkg))["international"]!);
    }

    // ---- spend limit -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Monthly_spend_limit_stops_purchases_with_a_clear_error_before_the_provider_is_called()
    {
        using var f = new SpendLimitFactory();                                  // limit 10.00; the cheapest label for 500 g (+100 g box) costs 4.60
        var (s, _, pkg) = await Ship.NewShop(f);
        for (var i = 0; i < 2; i++) { var (_, o, _) = await Ship.Accepted(s); await Ship.BuyCheapest(s.Owner, o, pkg); }
        var (_, third, _) = await Ship.Accepted(s);
        var q = await Ship.Quote(s.Owner, third, pkg);
        var r = await Ship.Buy(s.Owner, third, q);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); Assert.Equal("spend_limit", await Orders.Code(r));
        var body = await Orders.Node(r);
        Assert.Contains("monthly label limit", (string)body["title"]! + (string)(body["message"] ?? ""), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, f.Provider.Purchases);
        Assert.Equal(920, (long)(await Orders.Node(await s.Owner.Get($"{Base}/summary")))["spend"]!["spentCents"]!);
        // the next month starts clean
        f.Time.Advance(TimeSpan.FromDays(32));
        await s.Owner.LoginAs(Ship.OwnerEmail(f, s.Seller), ApiFactory.Strong); await s.Owner.Switch(s.Seller);
        var q2 = await Ship.Quote(s.Owner, third, pkg);
        Assert.Equal(HttpStatusCode.Created, (await Ship.Buy(s.Owner, third, q2)).StatusCode);
    }

    // ---- ledger -------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Label_debits_are_idempotent_on_the_label_and_show_on_the_ledger_transactions_and_statement()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        var cost = (long)label["costCents"]!;
        // posting the same journal key again changes nothing
        await Pay.Run(f, async sp =>
        {
            var l = sp.GetRequiredService<LedgerService>();
            var again = await l.PostAsync(s.Seller, "USD", "shipping_label", $"shipping_label:{(string)label["id"]!}", "dup",
                [new(LedgerAccount.Available, -cost, "shipping_label"), new(LedgerAccount.Platform, cost, "shipping_label")], orderId: id);
            Assert.Null(again); return 0;
        });
        Assert.Equal(-cost, (await Pay.Bal(f, s.Seller)).Available);
        var rows = await Orders.Node(await s.Owner.Get($"/api/v1/seller/payments/ledger?type=shipping_label"));
        var row = Assert.Single(rows["items"]!.AsArray());
        Assert.Equal(-cost, (long)row!["amountCents"]!); Assert.Equal("shipping_label", (string)row["type"]!);
        var month = f.Time.GetUtcNow().ToString("yyyy-MM");
        var st = await Orders.Node(await s.Owner.Get($"/api/v1/seller/payments/statements/{month}"));
        Assert.Equal(-cost, (long)st["currencies"]![0]!["shippingLabelsCents"]!);
    }

    // ---- void / refund ----------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Void_credits_the_ledger_only_when_the_provider_confirms_and_reopens_the_order()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s, qty: 2);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        var cost = (long)label["costCents"]!; var lid = (string)label["id"]!;
        Assert.Equal((8L, 0L), Orders.Stock(f, s.Product));
        var v = await Orders.Node(await s.Owner.Post($"{Base}/labels/{lid}/void", new { reason = "Wrong size" }));
        Assert.True((bool)v["pending"]!); Assert.Equal("voiding", (string)v["label"]!["status"]!);
        Assert.Empty(Ship.Journals(f, s.Seller, "shipping_label_refund"));       // asked, NOT yet confirmed: no credit
        Assert.Equal("Shipped", Orders.St(await Orders.Detail(s.Owner, id)));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.Post($"{Base}/labels/{lid}/void")).StatusCode);   // already asked
        var m = await Ship.Maintenance(f);                                        // the provider confirms on the next check
        Assert.Equal(0, m.RefundsConfirmed);                                      // not due yet (30 min backoff)
        f.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, (await Ship.Maintenance(f)).RefundsConfirmed);
        var credit = Assert.Single(Ship.Journals(f, s.Seller, "shipping_label_refund"));
        Assert.Equal(cost, credit.Entries.Single(e => e.Account == LedgerAccount.Available).AmountCents);
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Available);                  // debit and credit net to zero
        await s.Owner.LoginAs(Ship.OwnerEmail(f, s.Seller), ApiFactory.Strong); await s.Owner.Switch(s.Seller);
        var d = await Orders.Detail(s.Owner, id);
        Assert.Equal("Accepted", Orders.St(d)); Assert.Empty(d["shipments"]!.AsArray());
        Assert.Equal((10L, 0L), Orders.Stock(f, s.Product));                      // units are back on hand
        Assert.Contains(Ship.Outbox(f, mid), x => x.Type == "shipment.voided");
        // running the job again never credits twice, and the order can be shipped with a new label
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, (await Ship.Maintenance(f)).RefundsConfirmed);
        Assert.Single(Ship.Journals(f, s.Seller, "shipping_label_refund"));
        await s.Owner.LoginAs(Ship.OwnerEmail(f, s.Seller), ApiFactory.Strong); await s.Owner.Switch(s.Seller);
        var again = await Ship.BuyCheapest(s.Owner, id, pkg);
        Assert.Equal("active", (string)again["status"]!);
        Assert.Equal("Shipped", Orders.St(await Orders.Detail(s.Owner, id)));
    }

    [Fact]
    public async Task An_immediately_confirmed_refund_credits_right_away_and_a_refused_one_never_does()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, a, _) = await Ship.Accepted(s);
        var la = await Ship.BuyCheapest(s.Owner, a, pkg);
        f.Provider.RefundMode = "success";
        var done = await Orders.Node(await s.Owner.Post($"{Base}/labels/{(string)la["id"]!}/void"));
        Assert.Equal("voided", (string)done["label"]!["status"]!);
        Assert.Single(Ship.Journals(f, s.Seller, "shipping_label_refund"));
        var (_, b, _) = await Ship.Accepted(s);
        var lb = await Ship.BuyCheapest(s.Owner, b, pkg);
        f.Provider.RefundMode = "error";
        var refused = await s.Owner.Post($"{Base}/labels/{(string)lb["id"]!}/void");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode); Assert.Equal("refund_refused", await Orders.Code(refused));
        Assert.Single(Ship.Journals(f, s.Seller, "shipping_label_refund"));       // still only the first
        Assert.Equal("Shipped", Orders.St(await Orders.Detail(s.Owner, b)));
        Assert.Equal(LabelStatus.RefundFailed, Ship.Labels(f, b).Single().Status);
    }

    [Fact]
    public async Task A_label_the_carrier_already_scanned_cannot_be_voided()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        await Ship.Track(f, Ship.Tn(label), "in_transit");
        var r = await s.Owner.Post($"{Base}/labels/{(string)label["id"]!}/void");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("label_in_use", await Orders.Code(r));
        Assert.Equal(0, f.Provider.RefundRequests);
    }

    [Fact]
    public async Task A_label_bought_but_never_attached_is_voided_by_the_maintenance_job()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        // simulate a crash between the purchase and the shipment: the shipment is gone, the label is still Active
        f.WithDb(db => { var l = db.ShippingLabels.First(x => x.OrderId == id); l.ShipmentId = null; l.ActiveShipmentKey = null; db.SaveChanges(); return 0; });
        Assert.Equal(0, (await Ship.Maintenance(f)).OrphansVoided);              // too fresh
        f.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, (await Ship.Maintenance(f)).OrphansVoided);
        Assert.Equal(1, f.Provider.RefundRequests);
        Assert.Equal(LabelStatus.Voiding, Ship.Labels(f, id).Single().Status);
        Assert.NotNull(label);
    }

    // ---- bulk ----------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Bulk_buy_handles_each_order_on_its_own_and_a_rerun_with_the_same_key_buys_nothing_new()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f, stock: 30);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await Ship.Accepted(s)).Id);
        var (_, fresh, _, _) = await Orders.Place(s);                             // never accepted: can't be shipped
        ids.Add(fresh);
        f.WithDb(db => { db.Products.First(p => p.Id == s.Product).WeightGrams = 400; db.SaveChanges(); return 0; });
        var body = new { orderIds = ids, packageId = pkg, strategy = "recommended", idempotencyKey = "batch-1" };
        var r = await Orders.Node(await s.Owner.Post($"{Base}/labels/bulk", body));
        Assert.Equal(3, (int)r["bought"]!); Assert.Equal(4, (int)r["total"]!);
        var res = r["results"]!.AsArray();
        Assert.False((bool)res.Single(x => (Guid)x!["orderId"]! == fresh)!["ok"]!);
        Assert.Equal(3, f.Provider.Purchases);
        var again = await Orders.Node(await s.Owner.Post($"{Base}/labels/bulk", body));
        Assert.Equal(3, f.Provider.Purchases);                                    // nothing bought twice
        Assert.Equal(3, f.WithDb(db => db.ShippingLabels.Count(l => l.SellerId == s.Seller && l.Status == LabelStatus.Active)));
        Assert.NotNull(again);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Owner.Post($"{Base}/labels/bulk", new { orderIds = new Guid[0] })).StatusCode);
    }

    // ---- permissions + IDOR -----------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Viewer_can_look_but_never_buy_manager_only_for_their_brands_and_other_accounts_get_404_on_every_endpoint()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        var lid = (string)label["id"]!;
        var (_, id2, _) = await Ship.Accepted(s);
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        var (mgr, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [s.Brand]);
        var (outsider, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [await NewBrand(s)]);   // a manager of ANOTHER brand of the same account
        var other = await Orders.NewShop(f);
        var q = await Ship.Quote(s.Owner, id2, pkg);

        // viewer: reads fine, every action is 403
        foreach (var url in new[] { $"{Base}/labels", $"{Base}/labels/{lid}", $"{Base}/orders/{id}/labels", $"{Base}/orders/{id}/tracking", $"{Base}/summary", $"{Base}/labels/{lid}/file" })
            Assert.Equal(HttpStatusCode.OK, (await viewer.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Base}/orders/{id2}/rates", new { package = Ship.Pkg(pkg) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Base}/orders/{id2}/labels", new { quoteId = (string)q["quoteId"]!, rateId = Ship.Cheapest(q) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Base}/labels/{lid}/void")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Base}/labels/{lid}/printed")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Base}/labels/bulk", new { orderIds = new[] { id2 }, packageId = pkg })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Base}/orders/{id}/validate-address")).StatusCode);
        Assert.Equal(1, f.Provider.Purchases);                                    // only the owner's first label so far

        // manager of the order's brand can buy
        Assert.Equal(HttpStatusCode.Created, (await Ship.Buy(mgr, id2, q)).StatusCode);
        // a manager of a different brand sees nothing of it: 404, not 403
        foreach (var (m, url) in new[] { ("GET", $"{Base}/labels/{lid}"), ("GET", $"{Base}/labels/{lid}/file"), ("GET", $"{Base}/orders/{id}/tracking"), ("GET", $"{Base}/orders/{id}/labels"),
            ("POST", $"{Base}/labels/{lid}/void"), ("POST", $"{Base}/labels/{lid}/printed"), ("POST", $"{Base}/orders/{id}/rates"), ("POST", $"{Base}/orders/{id}/validate-address") })
        {
            var res = m == "GET" ? await outsider.Get(url) : await outsider.Post(url, new { });
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        // and so does another account
        foreach (var (m, url) in new[] { ("GET", $"{Base}/labels/{lid}"), ("GET", $"{Base}/labels/{lid}/file"), ("GET", $"{Base}/orders/{id}/tracking"), ("GET", $"{Base}/orders/{id}/labels"),
            ("POST", $"{Base}/labels/{lid}/void"), ("POST", $"{Base}/labels/{lid}/printed"), ("POST", $"{Base}/orders/{id}/rates"), ("POST", $"{Base}/orders/{id}/labels"),
            ("POST", $"{Base}/orders/{id}/validate-address"), ("POST", $"{Base}/orders/{id}/attention/dismiss"), ("POST", $"{Base}/orders/{id}/returns/{Guid.NewGuid()}/label") })
        {
            var res = m == "GET" ? await other.Owner.Get(url) : await other.Owner.Post(url, new { });
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        var bulk = await Orders.Node(await other.Owner.Post($"{Base}/labels/bulk", new { orderIds = new[] { id }, packageId = pkg }));
        Assert.Equal(0, (int)bulk["bought"]!);
        Assert.Empty((await Orders.Node(await other.Owner.Get($"{Base}/labels")))["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get($"{Base}/labels", false)).StatusCode);
    }

    private static async Task<Guid> NewBrand(Shop s)
    {
        var r = await s.Owner.Post("/api/v1/brands", new { name = "Second brand " + Guid.NewGuid().ToString("N")[..6] });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return Guid.Parse((string)(await Orders.Node(r))["id"]!);
    }

    [Fact]
    public async Task Printed_marker_feeds_the_labels_to_print_count_on_the_dashboard()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        Assert.Equal(1, (int)(await Orders.Node(await s.Owner.Get($"{Base}/summary")))["labelsToPrint"]!);
        Assert.Equal(1, (int)(await Orders.Node(await s.Owner.Get("/api/v1/seller/orders/dashboard")))["shipping"]!["labelsToPrint"]!);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"{Base}/labels/{(string)label["id"]!}/printed")).StatusCode);
        Assert.Equal(0, (int)(await Orders.Node(await s.Owner.Get($"{Base}/summary")))["labelsToPrint"]!);
        Assert.Empty((await Orders.Node(await s.Owner.Get($"{Base}/labels?unprinted=true")))["items"]!.AsArray());
    }

    [Fact]
    public async Task Label_format_follows_the_setting_and_the_file_has_the_matching_type()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"{Base}/settings", new { labelFormat = "ZPLII" })).StatusCode);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        Assert.Equal("ZPLII", (string)label["format"]!);
        var file = await s.Owner.Get((string)label["fileUrl"]!);
        Assert.Equal("application/zpl", file.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("^XA", await file.Content.ReadAsStringAsync());
    }
}
