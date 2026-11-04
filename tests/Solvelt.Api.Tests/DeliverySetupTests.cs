using System.Net;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;
using static Solvelt.Api.Tests.Dlv;

namespace Solvelt.Api.Tests;

public class DeliverySetupTests(DeliveryFactory f) : IClassFixture<DeliveryFactory>
{
    private const string Elig = "/api/internal/v1/local-delivery/eligibility";

    [Fact]
    public async Task Owner_configures_zones_fees_windows_and_cutoff_and_reads_them_back()
    {
        var d = await NewDShop(f);
        var n = await Node(await d.S.Owner.Get("/api/v1/seller/delivery/settings"));
        Assert.True((bool)n["enabled"]!); Assert.Equal("14:00", (string)n["sameDayCutoff"]!);
        Assert.Equal(500, (long)n["zones"]![0]!["feeCents"]!); Assert.Equal(10000, (long)n["zones"]![0]!["freeOverCents"]!);
        Assert.Equal(2, n["windows"]!.AsArray().Count);
        // replace: drop the second window (no bookings -> deleted) and change a fee; the zone keeps its id
        var zone = n["zones"]![0]!; var w0 = n["windows"]![0]!;
        var r = await d.S.Owner.Put("/api/v1/seller/delivery/settings", new
        {
            enabled = true, timeZone = "America/New_York", sameDayCutoff = "10:30", version = (int)n["version"]!,
            zones = new[] { new { id = Guid.Parse((string)zone["id"]!), name = "Brooklyn", country = "US", postcodes = new[] { "11201" }, feeCents = 700 } },
            windows = new[] { new { id = Guid.Parse((string)w0["id"]!), start = "09:00", end = "11:00", capacity = 3, days = new[] { 1, 2 } } }
        });
        var m = await Node(r);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(700, (long)m["zones"]![0]!["feeCents"]!); Assert.Single(m["windows"]!.AsArray()); Assert.Equal("America/New_York", (string)m["timeZone"]!);
        Assert.Equal(Guid.Parse((string)zone["id"]!), Guid.Parse((string)m["zones"]![0]!["id"]!));
    }

    [Fact]
    public async Task Settings_validation_rejects_bad_input_and_stale_versions()
    {
        var d = await NewDShop(f);
        async Task<HttpStatusCode> Put(object body) => (await d.S.Owner.Put("/api/v1/seller/delivery/settings", body)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Settings().with2("timeZone", "Mars/Base")));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Settings(zones: [new { name = "", postcodes = new[] { "1" } }])));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Settings(zones: [new { name = "No criteria" }])));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Settings(zones: [new { name = "R", radiusKm = 5.0 }])));                      // radius without an origin
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Settings(windows: [new { start = "14:00", end = "12:00", capacity = 2 }])));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Settings(windows: [new { start = "12:00", end = "14:00", capacity = 0 }])));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(Settings(cutoff: "25:99")));
        Assert.Equal(HttpStatusCode.Conflict, await Put(Settings(version: 999)));
    }

    [Fact]
    public async Task Only_owner_and_admin_edit_settings_viewers_read_and_drivers_see_nothing()
    {
        var d = await NewDShop(f);
        var (mgr, _) = await TeamHelpers.JoinAs(f, d.S.Owner, "Manager"); await mgr.Switch(d.S.Seller);
        var (viewer, _) = await TeamHelpers.JoinAs(f, d.S.Owner, "Viewer"); await viewer.Switch(d.S.Seller);
        var (admin, _) = await TeamHelpers.JoinAs(f, d.S.Owner, "Admin"); await admin.Switch(d.S.Seller);
        var driver = await AddDriver(d);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Put("/api/v1/seller/delivery/settings", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Put("/api/v1/seller/delivery/settings", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Put("/api/v1/seller/delivery/settings", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Get("/api/v1/seller/delivery/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get("/api/v1/seller/delivery/board")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Api.Get("/api/v1/seller/delivery/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Api.Get("/api/v1/seller/delivery/board")).StatusCode);
    }

    [Fact]
    public async Task Another_accounts_brand_is_404_for_settings()
    {
        var a = await NewDShop(f); var b = await NewDShop(f);
        Assert.Equal(HttpStatusCode.NotFound, (await a.S.Owner.Get($"/api/v1/seller/delivery/settings?brandId={b.S.Brand}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.S.Owner.Put("/api/v1/seller/delivery/settings", Settings(brand: b.S.Brand))).StatusCode);
    }

    [Fact]
    public async Task Brand_settings_override_the_account_settings()
    {
        var d = await NewDShop(f);
        Assert.Equal(HttpStatusCode.OK, (await d.S.Owner.Put("/api/v1/seller/delivery/settings", Settings(brand: d.S.Brand, zones: [new { name = "Brand zone", country = "US", postcodes = new[] { "11201" }, feeCents = 123 }]))).StatusCode);
        var r = await Node(await Inbound(f, Elig, new { sellerId = d.S.Seller, brandId = d.S.Brand, subtotalCents = 2000, address = new { postalCode = "11201", country = "US" } }));
        Assert.Equal(123, (long)r["feeCents"]!);
    }

    [Fact]
    public async Task Eligibility_endpoint_is_signed_and_returns_fee_and_open_windows()
    {
        var d = await NewDShop(f);
        var body = new { sellerId = d.S.Seller, brandId = d.S.Brand, subtotalCents = 2000, address = new { postalCode = "11201", country = "US", lat = HomeLat, lng = HomeLng } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Signed(f, "POST", Elig, body, sign: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Inbound(f, Elig, body, secret: "wrong-secret-wrong-secret-wrong")).StatusCode);
        var n = await Node(await Inbound(f, Elig, body));
        Assert.True((bool)n["eligible"]!); Assert.Equal(500, (long)n["feeCents"]!); Assert.Equal("Brooklyn", (string)n["zone"]!["name"]!);
        var wins = n["windows"]!.AsArray();
        Assert.Contains(wins, w => (string)w!["windowId"]! == d.WinA.ToString() && (string)w["date"]! == "2026-11-04");      // 12:00 today, cutoff 14:00, clock 09:00
        Assert.Equal(2, (int)wins.First(w => (string)w!["windowId"]! == d.WinA.ToString())!["remaining"]!);
        // free over the threshold, outside the zone, no settings at all
        Assert.Equal(0, (long)(await Node(await Inbound(f, Elig, new { sellerId = d.S.Seller, brandId = d.S.Brand, subtotalCents = 10000, address = new { postalCode = "11201", country = "US" } })))["feeCents"]!);
        var out1 = await Node(await Inbound(f, Elig, new { sellerId = d.S.Seller, brandId = d.S.Brand, subtotalCents = 2000, address = new { postalCode = "90210", country = "US" } }));
        Assert.False((bool)out1["eligible"]!); Assert.Equal("outside_zone", (string)out1["reason"]!); Assert.Empty(out1["windows"]!.AsArray());
        var other = await NewShop(f);
        Assert.Equal("not_offered", (string)(await Node(await Inbound(f, Elig, new { sellerId = other.Seller, brandId = other.Brand, subtotalCents = 1, address = new { postalCode = "11201", country = "US" } })))["reason"]!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Inbound(f, Elig, new { sellerId = d.S.Seller })).StatusCode);
    }

    [Fact]
    public async Task A_full_window_disappears_from_the_offer_and_a_disabled_seller_offers_nothing()
    {
        var d = await NewDShop(f, stock: 20);
        await PlaceLocal(d, d.WinB); // capacity 1 for the 16:00 window
        var wins = (await Node(await Inbound(f, Elig, new { sellerId = d.S.Seller, brandId = d.S.Brand, subtotalCents = 2000, address = new { postalCode = "11201", country = "US" } })))["windows"]!.AsArray();
        Assert.DoesNotContain(wins, w => (string)w!["windowId"]! == d.WinB.ToString() && (string)w["date"]! == "2026-11-04");
        Assert.Contains(wins, w => (string)w!["windowId"]! == d.WinB.ToString() && (string)w["date"]! == "2026-11-05");
        await d.S.Owner.Put("/api/v1/seller/delivery/settings", Settings(enabled: false));
        Assert.False((bool)(await Node(await Inbound(f, Elig, new { sellerId = d.S.Seller, brandId = d.S.Brand, subtotalCents = 2000, address = new { postalCode = "11201", country = "US" } })))["eligible"]!);
    }

    // ---- ingest ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Ingest_with_a_local_window_creates_the_delivery_books_capacity_and_is_idempotent()
    {
        var d = await NewDShop(f);
        var (mid, id, n) = await PlaceLocal(d);
        var del = Del(f, id);
        Assert.Equal(LocalStatus.Unassigned, del.Status); Assert.Equal(500, del.FeeCents); Assert.Equal(d.WinA, del.WindowId); Assert.Equal(Today, del.Date);
        Assert.Equal(new DateTime(2026, 11, 4, 12, 0, 0), del.WindowStartUtc); Assert.Matches(@"^LD-[0-9A-F]{10}$", del.Code);
        Assert.Equal("local", (string)(await Detail(d.S.Owner, id))["shippingMethod"]!);
        Assert.Equal(1, f.WithDb(db => db.DeliveryWindowUsage.Single(u => u.WindowId == d.WinA && u.Date == Today).Booked));
        // replay of the same marketplace order: 200, nothing booked twice
        var again = await Inbound(f, "/api/internal/v1/orders", new
        {
            marketplaceOrderId = mid, placedAt = f.Time.GetUtcNow().UtcDateTime, currency = "USD", buyer = new { displayName = "Jane Buyer" },
            shippingAddress = new { name = "J", line1 = "1 Main", city = "Brooklyn", postalCode = "11201", country = "US" }, lines = new[] { Line(d.S.Seller, d.S.Brand, d.S.Product) },
            deliveries = new[] { new { sellerId = d.S.Seller, brandId = d.S.Brand, method = "local", windowId = d.WinA, date = "2026-11-04" } }
        });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, f.WithDb(db => db.DeliveryWindowUsage.Single(u => u.WindowId == d.WinA && u.Date == Today).Booked));
    }

    [Fact]
    public async Task A_full_window_rejects_the_order_with_nothing_created_and_stock_released()
    {
        var d = await NewDShop(f, stock: 10);
        await PlaceLocal(d, d.WinB);
        var (reservedBefore, mid) = (Stock(f, d.S.Product).Reserved, Mid());
        var r = await Inbound(f, "/api/internal/v1/orders", new
        {
            marketplaceOrderId = mid, currency = "USD", buyer = new { displayName = "B" }, shippingAddress = new { name = "B", line1 = "1", city = "Brooklyn", postalCode = "11201", country = "US", lat = HomeLat, lng = HomeLng },
            lines = new[] { Line(d.S.Seller, d.S.Brand, d.S.Product, 2) },
            deliveries = new[] { new { sellerId = d.S.Seller, brandId = d.S.Brand, method = "local", windowId = d.WinB, date = "2026-11-04" } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("delivery_window_full", (string)(await Node(r))["errors"]![0]!["code"]!);
        Assert.Empty(f.WithDb(db => db.Orders.Where(o => o.MarketplaceOrderId == mid).ToList()));
        Assert.Equal(reservedBefore, Stock(f, d.S.Product).Reserved);
        Assert.Equal(1, f.WithDb(db => db.DeliveryWindowUsage.Single(u => u.WindowId == d.WinB && u.Date == Today).Booked));
    }

    [Theory]
    [InlineData("90210", "delivery_outside_zone", false)]
    [InlineData("11201", "delivery_window_unavailable", true)]
    public async Task Ingest_rejects_outside_zone_addresses_and_unavailable_windows(string postal, string code, bool badWindow)
    {
        var d = await NewDShop(f);
        var r = await Inbound(f, "/api/internal/v1/orders", new
        {
            marketplaceOrderId = Mid(), currency = "USD", buyer = new { displayName = "B" }, shippingAddress = new { name = "B", line1 = "1", city = "X", postalCode = postal, country = "US" },
            lines = new[] { Line(d.S.Seller, d.S.Brand, d.S.Product) },
            deliveries = new[] { new { sellerId = d.S.Seller, brandId = d.S.Brand, method = "local", windowId = badWindow ? Guid.NewGuid() : d.WinA, date = "2026-11-04" } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(code, (string)(await Node(r))["errors"]![0]!["code"]!);
    }

    [Fact]
    public async Task A_window_for_a_day_that_is_not_a_delivery_day_or_after_the_cutoff_is_refused()
    {
        var d = await NewDShop(f, settings: Settings(windows: [new { start = "12:00", end = "14:00", capacity = 2, days = new[] { 1 } }]));     // Mondays only
        var win = Guid.Parse((string)(await Node(await d.S.Owner.Get("/api/v1/seller/delivery/settings")))["windows"]![0]!["id"]!);
        var r = await Inbound(f, "/api/internal/v1/orders", new
        {
            marketplaceOrderId = Mid(), currency = "USD", buyer = new { displayName = "B" }, shippingAddress = new { name = "B", line1 = "1", city = "X", postalCode = "11201", country = "US" },
            lines = new[] { Line(d.S.Seller, d.S.Brand, d.S.Product) }, deliveries = new[] { new { sellerId = d.S.Seller, brandId = d.S.Brand, method = "local", windowId = win, date = "2026-11-04" } }   // a Wednesday
        });
        Assert.Equal("delivery_window_unavailable", (string)(await Node(r))["errors"]![0]!["code"]!);
    }

    // ---- seller-side conversion -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_carrier_order_in_the_zone_can_be_switched_to_local_with_audit_and_a_buyer_event()
    {
        var d = await NewDShop(f);
        var (mid, id, _, _) = await Place(d.S);          // plain carrier order, no coordinates: the postcode zone still matches
        var r = await d.S.Owner.Post($"/api/v1/seller/orders/{id}/local-delivery", new { windowId = d.WinA, date = "2026-11-04" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var del = Del(f, id);
        Assert.True(del.Converted); Assert.Equal(d.WinA, del.WindowId);
        Assert.Equal(1, f.WithDb(db => db.DeliveryWindowUsage.Single(u => u.WindowId == d.WinA && u.Date == Today).Booked));
        Assert.Contains(OutboxFor(f, mid), m => m.EventType == "order.delivery_method_changed");
        Assert.Contains(f.WithDb(db => db.AuditEvents.ToList()), a => a.Type == "delivery.converted" && a.Detail!.Contains(id.ToString()));
        Assert.Contains((await Detail(d.S.Owner, id))["timeline"]!.AsArray(), e => (string)e!["type"]! == "order.converted_to_local");
        // second time, shipped orders, outside the zone, other accounts, viewers
        Assert.Equal(HttpStatusCode.Conflict, (await d.S.Owner.Post($"/api/v1/seller/orders/{id}/local-delivery", new { windowId = d.WinA, date = "2026-11-04" })).StatusCode);
        var (_, id2, _, _) = await Place(d.S); await Ok(Act(d.S.Owner, id2, "accept")); await Ok(Act(d.S.Owner, id2, "ship", Ship()));
        Assert.Equal(HttpStatusCode.Conflict, (await d.S.Owner.Post($"/api/v1/seller/orders/{id2}/local-delivery", new { windowId = d.WinA, date = "2026-11-04" })).StatusCode);
        var stranger = await NewShop(f);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Owner.Post($"/api/v1/seller/orders/{id2}/local-delivery", new { windowId = d.WinA, date = "2026-11-04" })).StatusCode);
        var (viewer, _) = await TeamHelpers.JoinAs(f, d.S.Owner, "Viewer"); await viewer.Switch(d.S.Seller);
        var (_, id3, _, _) = await Place(d.S);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/seller/orders/{id3}/local-delivery", new { windowId = d.WinA, date = "2026-11-04" })).StatusCode);
    }

    [Fact]
    public async Task Conversion_outside_the_zone_or_into_a_full_window_is_refused_and_books_nothing()
    {
        var d = await NewDShop(f);
        var mid = Mid();
        var body = new
        {
            marketplaceOrderId = mid, currency = "USD", buyer = new { displayName = "B" }, shippingAddress = new { name = "B", line1 = "1", city = "LA", postalCode = "90210", country = "US" },
            lines = new[] { Line(d.S.Seller, d.S.Brand, d.S.Product) }
        };
        var id = Guid.Parse((string)(await Node(await Inbound(f, "/api/internal/v1/orders", body)))["orders"]![0]!["sellerOrderId"]!);
        var r = await d.S.Owner.Post($"/api/v1/seller/orders/{id}/local-delivery", new { windowId = d.WinA, date = "2026-11-04" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); Assert.Equal("delivery_outside_zone", await Code(r));
        Assert.Empty(f.WithDb(db => db.LocalDeliveries.Where(x => x.OrderId == id).ToList()));
        Assert.Empty(f.WithDb(db => db.DeliveryWindowUsage.Where(u => u.WindowId == d.WinA).ToList()));
    }
}

static class Ext
{
    /// <summary>Settings with one top-level field replaced.</summary>
    public static object with2(this object settings, string key, object value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(settings))!.AsObject();
        node[key] = System.Text.Json.Nodes.JsonValue.Create(value);
        return node;
    }
}
