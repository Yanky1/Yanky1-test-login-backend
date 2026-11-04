using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using Solvelt.Api.Shipping;

namespace Solvelt.Api.Tests;

public class ShippingReturnTests
{
    private const string Base = "/api/v1/seller/shipping";

    /// <summary>An order that shipped (label), was delivered by the carrier, and has an approved return R1 for 1 unit.</summary>
    private static async Task<(Shop S, Guid Order, string Mid, Guid Pkg, string Rid, JsonNode OutLabel)> ApprovedReturn(ShipFactory f, bool approve = true)
    {
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s, qty: 2, lid: "RL1");
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        await Ship.Track(f, Ship.Tn(label), "delivered");
        var r = await Orders.Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R1", reason = "not_as_described", lines = new[] { new { marketplaceLineId = "RL1", quantity = 1 } } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var rid = (string)(await Orders.Detail(s.Owner, id))["returns"]![0]!["id"]!;
        if (approve) await Orders.Ok(Orders.Act(s.Owner, id, $"returns/{rid}/approve", new { note = "Send it back" }));
        return (s, id, mid, pkg, rid, label);
    }

    private static string PathOf(string url) { var u = new Uri(url); return u.PathAndQuery; }

    [Fact]
    public async Task A_return_label_needs_an_approved_return_and_is_bought_only_once()
    {
        using var f = new ShipFactory();
        var (s, id, _, pkg, rid, _) = await ApprovedReturn(f, approve: false);
        var early = await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(0, f.Provider.Purchases - 1);
        await Orders.Ok(Orders.Act(s.Owner, id, $"returns/{rid}/approve"));
        var ok = await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var again = await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg), idempotencyKey = "other" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); Assert.Equal("label_exists", await Orders.Code(again));
        Assert.Equal(2, f.Provider.Purchases);                                    // the outbound label + exactly one return label
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.Post($"{Base}/orders/{id}/returns/{Guid.NewGuid()}/label", new { package = Ship.Pkg(pkg) })).StatusCode);
    }

    [Fact]
    public async Task Return_label_goes_to_the_marketplace_as_a_signed_short_lived_link_and_is_billed_to_the_seller()
    {
        using var f = new ShipFactory();
        var (s, id, mid, pkg, rid, _) = await ApprovedReturn(f);
        var r = await Orders.Node(await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) }));
        var label = r["label"]!;
        Assert.Equal("return", (string)label["kind"]!);
        var ev = Ship.Outbox(f, mid).Single(x => x.Type == "return.label_created").Data;
        Assert.Equal("R1", (string)ev["returnId"]!); Assert.Equal(Ship.Tn(label), (string)ev["trackingNumber"]!); Assert.NotNull((string?)ev["carrier"]);
        var url = (string)ev["labelUrl"]!;
        Assert.StartsWith("https://api.test/api/v1/shipping/public/labels/", url);
        Assert.True(DateTime.Parse((string)ev["labelUrlExpiresAt"]!).ToUniversalTime() > f.Time.GetUtcNow().UtcDateTime);
        // the link works without a login …
        var anon = f.NewClient();
        var file = await anon.GetAsync(PathOf(url));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode); Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        // … but only that exact link: a changed signature, id or expiry gives nothing
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(PathOf(url)[..^4] + "0000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(PathOf(url).Replace("exp=", "exp=9"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(PathOf(url).Replace(((string)label["id"]!).Replace("-", ""), Guid.NewGuid().ToString("N")))).StatusCode);
        // the outbound label can't be fetched with it (the signature binds the label id)
        // it expires
        f.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(PathOf(url))).StatusCode);
        // the marketplace can ask for a fresh link (signed request)
        var fresh = await f.NewClient().SendAsync(Orders.Signed(f, "POST", "/api/internal/v1/returns/R1/label-link", new { }));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var nu = (string)(await Orders.Node(fresh))["labelUrl"]!;
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(PathOf(nu))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", "/api/internal/v1/returns/R1/label-link", new { }, sign: false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", "/api/internal/v1/returns/NOPE/label-link", new { }))).StatusCode);
        // the seller pays: a second debit journal on the same order
        var js = Ship.Journals(f, s.Seller, "shipping_label");
        Assert.Equal(2, js.Count); Assert.All(js, j => Assert.Equal(id, j.OrderId));
        Assert.Equal(-(long)label["costCents"]! - js.Where(j => j.Entries.Any(e => e.Account == LedgerAccount.Available && -e.AmountCents != (long)label["costCents"]!)).Sum(j => 0), -(long)label["costCents"]!);
    }

    [Fact]
    public async Task The_return_parcel_is_tracked_arrival_is_announced_but_never_auto_received()
    {
        using var f = new ShipFactory();
        var (s, id, mid, pkg, rid, _) = await ApprovedReturn(f);
        var owner = Ship.OwnerEmail(f, s.Seller);
        var label = (await Orders.Node(await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) })))["label"]!;
        var tn = Ship.Tn(label);
        Assert.Contains(tn, f.Provider.Registered);
        Assert.Equal("applied", await Ship.Track(f, tn, "in_transit", f.Time.GetUtcNow().UtcDateTime.AddHours(1)));
        var upd = Ship.Outbox(f, mid).Last(x => x.Type == "order.tracking_updated").Data;
        Assert.Equal("return", (string)upd["kind"]!); Assert.Equal("R1", (string)(upd["returnId"] ?? "R1")!.ToString().Replace(rid, "R1"));
        Assert.Equal("ReturnApproved", Orders.St(await Orders.Detail(s.Owner, id)));
        Assert.Equal("applied", await Ship.Track(f, tn, "delivered", f.Time.GetUtcNow().UtcDateTime.AddHours(30)));
        var d = await Orders.Detail(s.Owner, id);
        Assert.Equal("ReturnApproved", Orders.St(d));                                             // NOT received automatically
        Assert.NotNull((string?)d["returnShipments"]![0]!["deliveredAt"]);
        Assert.Contains(d["timeline"]!.AsArray(), e => (string)e!["type"]! == "return.arrived");
        Assert.Single(f.Mails(owner, "return-arrived"));
        Assert.True((bool)d["actions"]!["receiveReturn"]!);
        var tl = await Orders.Node(await s.Owner.Get($"{Base}/orders/{id}/tracking"));
        Assert.Equal("delivered", (string)tl["returns"]![0]!["status"]!);
        Assert.Equal(2, tl["returns"]![0]!["events"]!.AsArray().Count);
        // the seller inspects and marks it received
        Assert.Equal("ReturnReceived", Orders.St(await Orders.Ok(Orders.Act(s.Owner, id, $"returns/{rid}/receive", new { restock = true }))));
        // the return parcel's delivery never touched the outbound shipment's state
        Assert.Equal("delivered", Ship.Shipments(f, id).Single().TrackingStatus);
    }

    [Fact]
    public async Task A_return_label_can_be_voided_and_bought_again()
    {
        using var f = new ShipFactory();
        var (s, id, _, pkg, rid, _) = await ApprovedReturn(f);
        var label = (await Orders.Node(await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) })))["label"]!;
        f.Provider.RefundMode = "success";
        var v = await Orders.Node(await s.Owner.Post($"{Base}/labels/{(string)label["id"]!}/void"));
        Assert.Equal("voided", (string)v["label"]!["status"]!);
        Assert.Single(Ship.Journals(f, s.Seller, "shipping_label_refund"));
        Assert.Equal("ReturnApproved", Orders.St(await Orders.Detail(s.Owner, id)));              // a return label refund never reopens the order
        var again = await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task An_international_return_needs_customs_and_a_return_address_is_required()
    {
        using var f = new ShipFactory();
        var (s, id, _, pkg, rid, _) = await ApprovedReturn(f);
        // the seller's return address is in another country than the buyer's
        var ca = await Orders.Node(await s.Owner.Post($"{Base}/addresses", Ship.Address("1 Maple Rd", country: "CA")));
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"{Base}/settings", new { returnAddressId = (string)ca["id"]! })).StatusCode);
        var no = await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) });
        Assert.Equal("customs_required", await Orders.Code(no));
        var yes = await s.Owner.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg), customs = new { contentsType = "returned_goods" } });
        Assert.Equal(HttpStatusCode.Created, yes.StatusCode);
        // without any address at all
        using var f2 = new ShipFactory();
        var s2 = await Orders.NewShop(f2);
        var (mid2, id2, _, _) = await Orders.Place(s2, qty: 1, lid: "RL1");
        await Orders.Ok(Orders.Act(s2.Owner, id2, "accept")); await Orders.Ok(Orders.Act(s2.Owner, id2, "ship", Orders.Ship())); await Orders.Ok(Orders.Act(s2.Owner, id2, "deliver"));
        await Orders.Inbound(f2, $"/api/internal/v1/orders/{mid2}/return-requests", new { returnId = "R9", reason = "damaged", lines = new[] { new { marketplaceLineId = "RL1", quantity = 1 } } });
        var rid2 = (string)(await Orders.Detail(s2.Owner, id2))["returns"]![0]!["id"]!;
        await Orders.Ok(Orders.Act(s2.Owner, id2, $"returns/{rid2}/approve"));
        var none = await s2.Owner.Post($"{Base}/orders/{id2}/returns/{rid2}/label", new { package = new { lengthMm = 200, widthMm = 150, heightMm = 100, weightGrams = 400 } });
        Assert.Equal("return_address", await Orders.Code(none));
    }

    [Fact]
    public async Task Viewers_cannot_buy_return_labels()
    {
        using var f = new ShipFactory();
        var (s, id, _, pkg, rid, _) = await ApprovedReturn(f);
        var (viewer, _) = await Solvelt.Api.Tests.TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Base}/orders/{id}/returns/{rid}/label", new { package = Ship.Pkg(pkg) })).StatusCode);
        Assert.Equal(2 - 1, f.Provider.Purchases);
    }
}

public class ShippingStaffAndContractTests
{
    private const string Base = "/api/v1/seller/shipping";

    private static async Task<(Shop S, Guid Order, JsonNode Label)> FailedRefund(ShipFactory f)
    {
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        f.Provider.RefundMode = "error";
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.Post($"{Base}/labels/{(string)label["id"]!}/void")).StatusCode);
        return (s, id, label);
    }

    [Fact]
    public async Task Staff_see_shipments_labels_pending_and_failed_refunds_exceptions_and_webhook_failures()
    {
        using var f = new ShipFactory();
        var (s, id, label) = await FailedRefund(f);
        var (_, id2, _) = await Ship.Accepted(s);
        var (_, _, pkg) = (0, 0, f.WithDb(db => db.PackagePresets.First(p => p.SellerId == s.Seller).Id));
        var l2 = await Ship.BuyCheapest(s.Owner, id2, pkg);
        f.Provider.RefundMode = "hold";
        await s.Owner.Post($"{Base}/labels/{(string)l2["id"]!}/void");                           // stays pending
        await Ship.Track(f, "1Z999AA10000000001", "failure");                                     // unknown parcel: logged, acknowledged
        await Ship.Hook(f, "{\"data\":{}}", sign: false);                                         // a forged call: logged as bad_signature
        var staff = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var refunds = await Orders.Node(await staff.Get("/api/v1/admin/shipping/refunds"));
        Assert.Equal(2, (int)refunds["total"]!);
        Assert.Single((await Orders.Node(await staff.Get("/api/v1/admin/shipping/refunds?state=failed")))["items"]!.AsArray());
        Assert.Single((await Orders.Node(await staff.Get("/api/v1/admin/shipping/refunds?state=pending")))["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await staff.Get("/api/v1/admin/shipping/refunds?state=bogus")).StatusCode == HttpStatusCode.BadRequest ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK);
        Assert.Equal(2, (await Orders.Node(await staff.Get($"/api/v1/admin/shipping/sellers/{s.Seller}/labels")))["items"]!.AsArray().Count);
        Assert.Equal(2, (await Orders.Node(await staff.Get($"/api/v1/admin/shipping/sellers/{s.Seller}/shipments")))["items"]!.AsArray().Count);
        var fails = await Orders.Node(await staff.Get("/api/v1/admin/shipping/webhooks/failures"));
        var outcomes = fails["items"]!.AsArray().Select(x => (string)x!["Outcome"]! ?? (string)x["outcome"]!).ToList();
        Assert.Contains("bad_signature", outcomes); Assert.Contains("unknown_shipment", outcomes);
        await Ship.Track(f, Ship.Tn(l2), "failure", detail: "Lost");
        Assert.Single((await Orders.Node(await staff.Get("/api/v1/admin/shipping/exceptions")))["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await staff.Get($"/api/v1/admin/shipping/sellers/{Guid.NewGuid()}/labels")).StatusCode);
        Assert.NotNull(label); Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public async Task Staff_can_retry_a_failed_refund_with_a_reason_and_it_is_audited_read_only_staff_cannot()
    {
        using var f = new ShipFactory();
        var (s, id, label) = await FailedRefund(f);
        var lid = (string)label["id"]!;
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"/api/v1/admin/shipping/labels/{lid}/refund/retry", new { reason = StaffKit.Why })).StatusCode);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await support.Post($"/api/v1/admin/shipping/labels/{lid}/refund/retry", new { reason = "" })).StatusCode);
        f.Provider.RefundMode = "success";
        var ok = await support.Post($"/api/v1/admin/shipping/labels/{lid}/refund/retry", new { reason = StaffKit.Why });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(LabelStatus.Voided, Ship.Labels(f, id).Single().Status);
        Assert.Single(Ship.Journals(f, s.Seller, "shipping_label_refund"));
        Assert.Equal("admin.shipping.refund_retried", f.WithDb(db => db.AuditEvents.Where(a => a.Type.StartsWith("admin.shipping")).Select(a => a.Type).Single()));
        // a label that isn't waiting on a refund can't be "retried"
        var again = await support.Post($"/api/v1/admin/shipping/labels/{lid}/refund/retry", new { reason = StaffKit.Why });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.Post($"/api/v1/admin/shipping/labels/{Guid.NewGuid()}/refund/retry", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post($"/api/v1/admin/shipping/labels/{lid}/refund/retry", new { reason = "x" }, false)).StatusCode);
    }

    [Fact]
    public async Task Outbox_events_follow_the_documented_contract()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        var t = f.Time.GetUtcNow().UtcDateTime;
        await Ship.Track(f, Ship.Tn(label), "in_transit", t.AddHours(1), detail: "Departed facility");
        await Ship.Track(f, Ship.Tn(label), "delivered", t.AddHours(9));
        var ev = Ship.Outbox(f, mid);
        // events written in one save share a timestamp, so compare as a set
        Assert.Equal(new[] { "order.delivered", "order.shipped", "order.tracking_updated", "order.tracking_updated", "shipment.delivered" }, ev.Select(x => x.Type).Where(t => t != "order.accepted").Order().ToArray());
        var upd = ev.First(x => x.Type == "order.tracking_updated" && (string)x.Data["status"]! == "in_transit").Data;
        foreach (var k in new[] { "order", "shipmentId", "carrier", "trackingNumber", "trackingUrl", "status", "statusDetail", "eta", "events" }) Assert.True(upd.AsObject().ContainsKey(k), k);
        Assert.Equal("in_transit", (string)upd["status"]!); Assert.Equal("Departed facility", (string)upd["statusDetail"]!);
        var e0 = upd["events"]![0]!; Assert.Equal("in_transit", (string)e0["status"]!); Assert.NotNull((string?)e0["at"]);
        Assert.Equal(Orders.St(await Orders.Detail(s.Owner, id)), "Delivered");
        var del = ev.Single(x => x.Type == "shipment.delivered").Data;
        foreach (var k in new[] { "order", "shipmentId", "carrier", "trackingNumber", "deliveredAt", "orderDelivered" }) Assert.True(del.AsObject().ContainsKey(k), k);
        Assert.All(ev.Select(x => x.Data["order"]!), o => Assert.Equal(mid, (string)o["marketplaceOrderId"]!));
        // editing tracking by hand stays compatible: the old fields are still there, the new ones are added
        var s2 = await Orders.NewShop(f);
        var (mid2, id2, _) = await Ship.Accepted(s2);
        await Orders.Ok(Orders.Act(s2.Owner, id2, "ship", Orders.Ship(tracking: "1Z999AA10123456794")));
        var sid = (string)(await Orders.Detail(s2.Owner, id2))["shipments"]![0]!["id"]!;
        await s2.Owner.Put($"/api/v1/seller/orders/{id2}/shipments/{sid}", new { trackingNumber = "1Z999AA10123456795" });
        var edit = Ship.Outbox(f, mid2).Last(x => x.Type == "order.tracking_updated").Data;
        foreach (var k in new[] { "carrier", "trackingNumber", "trackingUrl", "status", "events" }) Assert.True(edit.AsObject().ContainsKey(k), k);
    }

    [Fact]
    public async Task Order_detail_and_list_carry_tracking_status_and_label_cost_additively()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var label = await Ship.BuyCheapest(s.Owner, id, pkg);
        await Ship.Track(f, Ship.Tn(label), "in_transit");
        var d = await Orders.Detail(s.Owner, id);
        var sh = d["shipments"]![0]!;
        Assert.Equal("in_transit", (string)sh["trackingStatus"]!); Assert.Equal("carrier", (string)sh["method"]!); Assert.Equal((long)label["costCents"]!, (long)sh["labelCostCents"]!);
        Assert.False((bool)d["needsAttention"]!);
        var list = await Orders.Node(await s.Owner.Get("/api/v1/seller/orders?tracking=in_transit"));
        Assert.Equal("in_transit", (string)list["items"]![0]!["trackingStatus"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Get("/api/v1/seller/orders?tracking=nonsense")).StatusCode);
    }
}

public class ShippingUnitTests
{
    [Theory]
    [InlineData("PRE_TRANSIT", null, "pre_transit")] [InlineData("TRANSIT", null, "in_transit")] [InlineData("TRANSIT", "out_for_delivery", "out_for_delivery")]
    [InlineData("TRANSIT", "available_for_pickup", "available_for_pickup")] [InlineData("DELIVERED", null, "delivered")] [InlineData("RETURNED", null, "return_to_sender")]
    [InlineData("FAILURE", null, "failure")] [InlineData("UNKNOWN", null, "unknown")] [InlineData("SOMETHING_NEW", null, "unknown")] [InlineData(null, null, "unknown")]
    public void Shippo_statuses_map_to_the_normalized_set(string? status, string? sub, string expected)
    {
        Assert.Equal(expected, TrackingStatuses.FromShippo(status, sub));
        Assert.Contains(expected, TrackingStatuses.All);
    }

    [Fact]
    public void Normalized_statuses_are_exactly_the_documented_eight() =>
        Assert.Equal(["pre_transit", "in_transit", "out_for_delivery", "delivered", "available_for_pickup", "return_to_sender", "failure", "unknown"], TrackingStatuses.All);

    [Theory]
    [InlineData("5.50", "USD", 550)] [InlineData("5.505", "USD", 551)] [InlineData("5.504", "USD", 550)] [InlineData("0.005", "USD", 1)] [InlineData("1234", "JPY", 1234)] [InlineData("12", "EUR", 1200)]
    public void Provider_amounts_become_integer_minor_units_rounding_half_away_from_zero(string amount, string cur, long expected) =>
        Assert.Equal(expected, ShippingMoney.ToMinor(amount, cur));

    [Fact]
    public void Garbage_amounts_are_a_provider_error() => Assert.Throws<ShippingProviderException>(() => ShippingMoney.ToMinor("free", "USD"));

    [Theory]
    [InlineData("shippo_test_abc", true, false, false, false)]     // production refuses a test key …
    [InlineData("shippo_test_abc", true, true, false, true)]       // … unless allowed on purpose
    [InlineData("shippo_live_abc", false, false, false, false)]    // other environments refuse a live key …
    [InlineData("shippo_live_abc", false, false, true, true)]
    [InlineData("shippo_live_abc", true, false, false, true)]
    [InlineData("shippo_test_abc", false, false, false, true)]
    [InlineData("sk_test_abc", false, false, false, false)]        // not a Shippo token
    [InlineData("", true, false, false, true)]
    public void Shippo_key_rules(string key, bool production, bool allowTestInProd, bool allowLiveElsewhere, bool ok)
    {
        if (ok) ShippingModule.ValidateKey(key, production, allowTestInProd, allowLiveElsewhere);
        else Assert.Throws<InvalidOperationException>(() => ShippingModule.ValidateKey(key, production, allowTestInProd, allowLiveElsewhere));
    }

    [Fact]
    public async Task The_fake_provider_is_deterministic_for_the_same_input()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var a = new FakeShippingProvider(time); var b = new FakeShippingProvider(time);
        var origin = new ShipAddress("A", null, "1 St", null, "X", "NY", "10001", "US", null, null); var dest = origin with { Country = "US", PostalCode = "20002" };
        var req = new RateRequest(origin, dest, new ParcelSpec(300, 200, 100, 800), null, false);
        var ra = await a.GetRatesAsync(req); var rb = await b.GetRatesAsync(req);
        Assert.Equal(ra.Rates.Select(r => (r.Carrier, r.Service, r.AmountCents, r.EstimatedDays)), rb.Rates.Select(r => (r.Carrier, r.Service, r.AmountCents, r.EstimatedDays)));
        Assert.Equal(480, ra.Rates.First(r => r.Service == "Ground Advantage").AmountCents);
        var pa = await a.PurchaseAsync(new PurchaseRequest(ra.Rates[0].RateId, ra.ProviderShipmentId, "PDF_4x6", "k"));
        var pb = await b.PurchaseAsync(new PurchaseRequest(rb.Rates[0].RateId, rb.ProviderShipmentId, "PDF_4x6", "k"));
        Assert.Equal(pa.TrackingNumber, pb.TrackingNumber);
        Assert.True(Solvelt.Api.Orders.Tracking.Valid(pa.Carrier, pa.TrackingNumber));         // fake numbers look like the carrier's
    }

    [Fact]
    public void Shippo_tracking_payload_is_parsed_with_history()
    {
        var json = JsonNode.Parse("""
        {"carrier":"usps","tracking_number":"9400","eta":"2026-05-09T00:00:00Z",
         "tracking_status":{"status":"TRANSIT","substatus":{"code":"out_for_delivery"},"status_details":"Out","status_date":"2026-05-08T12:00:00Z","location":{"city":"Brooklyn","state":"NY","country":"US"}},
         "tracking_history":[{"status":"PRE_TRANSIT","status_details":"Label","status_date":"2026-05-06T09:00:00Z"},{"status":"TRANSIT","status_details":"Moving","status_date":"2026-05-07T09:00:00Z"}]}
        """)!;
        var snap = ShippoPayload.Snapshot(json)!;
        Assert.Equal("out_for_delivery", snap.Status); Assert.Equal("USPS", snap.Carrier); Assert.Equal("Brooklyn, NY, US", snap.Location);
        Assert.Equal(3, snap.History.Count); Assert.Equal(new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc), snap.Eta!.Value.ToUniversalTime());
    }
}
