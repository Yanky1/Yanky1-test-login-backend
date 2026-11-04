using System.Net.Http.Json;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Shipping;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Tests;

public class ShippingSettingsTests
{
    private const string Base = "/api/v1/seller/shipping";

    [Fact]
    public async Task Addresses_first_becomes_default_and_exactly_one_default_per_scope()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var a1 = await Orders.Node(await s.Owner.Post(Base + "/addresses", Ship.Address()));
        Assert.True((bool)a1["isDefault"]!); Assert.Equal("valid", (string)a1["validation"]!);
        var a2 = await Orders.Node(await s.Owner.Post(Base + "/addresses", Ship.Address("22 Other St")));
        Assert.False((bool)a2["isDefault"]!);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"{Base}/addresses/{(string)a2["id"]!}/default")).StatusCode);
        var rows = f.WithDb(db => db.ShipFromAddresses.AsNoTracking().Where(x => x.SellerId == s.Seller).ToList());
        Assert.Single(rows, x => x.IsDefault);
        Assert.Equal(Guid.Parse((string)a2["id"]!), rows.Single(x => x.IsDefault).Id);
        // a brand-scoped default does not disturb the account default
        var b = await Orders.Node(await s.Owner.Post(Base + "/addresses", Ship.Address("Brand dock", brandId: s.Brand)));
        Assert.True((bool)b["isDefault"]!);
        Assert.Equal(2, f.WithDb(db => db.ShipFromAddresses.Count(x => x.SellerId == s.Seller && x.IsDefault)));
    }

    [Fact]
    public async Task The_database_itself_allows_only_one_default_per_scope()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        await s.Owner.Post(Base + "/addresses", Ship.Address());
        var ex = Assert.ThrowsAny<DbUpdateException>(() => f.WithDb(db =>
        {
            db.ShipFromAddresses.Add(new ShipFromAddress { SellerId = s.Seller, ScopeKey = Guid.Empty, IsDefault = true, DefaultKey = Guid.Empty, Name = "x", Line1 = "x", City = "x", PostalCode = "1", Country = "US" });
            db.SaveChanges(); return 0;
        }));
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Address_is_validated_through_the_provider_invalid_is_refused_unverified_is_kept_with_a_warning()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var bad = await s.Owner.Post(Base + "/addresses", Ship.Address("1 INVALID Way"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode); Assert.Equal("address_invalid", await Orders.Code(bad));
        var warn = await Orders.Node(await s.Owner.Post(Base + "/addresses", Ship.Address("2 UNVERIFIED Way")));
        Assert.Equal("unverified", (string)warn["validation"]!); Assert.NotNull((string?)warn["validationMessage"]);
        var missing = await s.Owner.Post(Base + "/addresses", new { name = "x", line1 = "y", city = "z", country = "US", postalCode = "1" });   // US needs a state
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Packages_profiles_handling_time_and_return_address_crud()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var p = await Orders.Node(await s.Owner.Post(Base + "/packages", Ship.Package("Small")));
        var p2 = await Orders.Node(await s.Owner.Post(Base + "/packages", new { name = "Mailer", kind = "envelope", lengthMm = 320, widthMm = 240, heightMm = 20, isDefault = true }));
        Assert.True((bool)p["isDefault"]!);
        var pk = f.WithDb(db => db.PackagePresets.Where(x => x.SellerId == s.Seller).ToList());
        Assert.Equal((string)p2["id"]!, pk.Single(x => x.IsDefault).Id.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Post(Base + "/packages", new { name = "Huge", lengthMm = 9000, widthMm = 1, heightMm = 1 })).StatusCode);

        var zones = new object[]
        {
            new { name = "Domestic", kind = "domestic", countries = new[] { "US" }, speeds = new object[] { new { speed = "standard", method = "flat", flatCents = 500 }, new { speed = "overnight", method = "weight", baseCents = 1500, perKgCents = 300 } } },
            new { name = "World", kind = "international", countries = new[] { "*" }, speeds = new object[] { new { speed = "standard", method = "flat", flatCents = 2500 } } }
        };
        var prof = await s.Owner.Post(Base + "/profiles", new { name = "Default", currency = "USD", freeShippingThresholdCents = 5000, zones });
        Assert.Equal(HttpStatusCode.Created, prof.StatusCode);
        var pj = await Orders.Node(prof);
        Assert.Equal(5000, (long)pj["freeShippingThresholdCents"]!); Assert.Equal(2, pj["zones"]!.AsArray().Count);
        // bad zones are refused with a clear reason
        foreach (var bad in new object[]
        {
            new object[] { new { name = "x", kind = "domestic", countries = new[] { "US" }, speeds = new object[] { new { speed = "warp", method = "flat", flatCents = 1 } } } },
            new object[] { new { name = "x", kind = "domestic", countries = new[] { "usa" }, speeds = new object[] { new { speed = "standard", method = "flat", flatCents = 1 } } } },
            new object[] { new { name = "x", kind = "domestic", countries = new[] { "US" }, speeds = new object[] { new { speed = "standard", method = "flat", flatCents = -5 } } } },
            new object[] { new { name = "x", kind = "domestic", countries = new[] { "*" }, speeds = new object[] { new { speed = "standard", method = "flat", flatCents = 5 } } } },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Post(Base + "/profiles", new { name = "Bad", zones = bad })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"{Base}/profiles/{(string)pj["id"]!}")).StatusCode);

        var addr = await Orders.Node(await s.Owner.Post(Base + "/addresses", Ship.Address()));
        var set = await s.Owner.Put(Base + "/settings", new { handlingDays = 4, labelFormat = "PNG", returnAddressId = (string)addr["id"]! });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Put(Base + "/settings", new { handlingDays = 99 })).StatusCode);
        var all = await Orders.Node(await s.Owner.Get(Base + "/settings"));
        Assert.Equal(4, (int)all["settings"]![0]!["handlingDays"]!); Assert.True((bool)all["canManage"]!);
    }

    [Fact]
    public async Task Handling_time_feeds_the_ship_by_date_of_new_orders()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, before, _, _) = await Orders.Place(s);
        var o1 = Ship.Order(f, before);
        Assert.Equal(2, (o1.ShipByAt - o1.PlacedAt).Days);              // Orders:DefaultHandlingDays
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put(Base + "/settings", new { handlingDays = 6 })).StatusCode);
        var (_, after, _, _) = await Orders.Place(s);
        var o2 = Ship.Order(f, after);
        Assert.Equal(6, (o2.ShipByAt - o2.PlacedAt).Days);
    }

    [Fact]
    public async Task Default_package_per_product_and_weight_comes_from_the_product_when_not_given()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put($"{Base}/products/{s.Product}/package", new { packageId = pkg })).StatusCode);
        Assert.Equal(pkg, f.WithDb(db => db.Products.AsNoTracking().First(p => p.Id == s.Product).DefaultPackageId));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.Put($"{Base}/products/{s.Product}/package", new { packageId = Guid.NewGuid() })).StatusCode);
        var (_, id, _) = await Ship.Accepted(s);
        // no weight on the product and none given: asks for it; with product weight it quotes
        var r = await s.Owner.Post($"{Base}/orders/{id}/rates", new { });
        Assert.Equal("weight_required", await Orders.Code(r));
        f.WithDb(db => { db.Products.First(p => p.Id == s.Product).WeightGrams = 450; db.SaveChanges(); return 0; });
        var q = await Orders.Node(await s.Owner.Post($"{Base}/orders/{id}/rates", new { }));
        Assert.Equal(550, (int)q["parcel"]!["weightGrams"]!);            // 450 g item + 100 g empty box
    }

    // ---- permissions ----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Settings_writes_are_Owner_and_Admin_only_everyone_in_the_account_can_read_anonymous_gets_nothing()
    {
        using var f = new ShipFactory();
        var (s, addr, pkg) = await Ship.NewShop(f);
        var (admin, _) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        var (mgr, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager");
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        foreach (var who in new[] { s.Owner, admin, mgr, viewer }) Assert.Equal(HttpStatusCode.OK, (await who.Get(Base + "/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get(Base + "/settings", false)).StatusCode);
        var writes = new (HttpMethod M, string Url, object? Body)[]
        {
            (HttpMethod.Post, Base + "/addresses", Ship.Address("5 Nope St")), (HttpMethod.Put, $"{Base}/addresses/{addr}", Ship.Address("6 Nope St")), (HttpMethod.Delete, $"{Base}/addresses/{addr}", null),
            (HttpMethod.Post, $"{Base}/addresses/{addr}/default", null), (HttpMethod.Post, Base + "/packages", Ship.Package("x")), (HttpMethod.Put, $"{Base}/packages/{pkg}", Ship.Package("y")),
            (HttpMethod.Delete, $"{Base}/packages/{pkg}", null), (HttpMethod.Put, Base + "/settings", new { handlingDays = 3 }),
            (HttpMethod.Post, Base + "/profiles", new { name = "p", zones = new object[0] })
        };
        foreach (var (m, url, body) in writes)
            foreach (var who in new[] { mgr, viewer })
            {
                var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
                Assert.Equal(HttpStatusCode.Forbidden, (await who.Send(req)).StatusCode);
            }
        Assert.Equal(HttpStatusCode.Created, (await admin.Post(Base + "/addresses", Ship.Address("7 Admin St"))).StatusCode);
        Assert.False((bool)(await Orders.Node(await mgr.Get(Base + "/settings")))["canManage"]!);
    }

    [Fact]
    public void Central_permission_matrix_shipping_row()
    {
        Assert.True(Permissions.Has(AccountRole.Owner, Permission.ShippingManage)); Assert.True(Permissions.Has(AccountRole.Admin, Permission.ShippingManage));
        Assert.False(Permissions.Has(AccountRole.Manager, Permission.ShippingManage)); Assert.False(Permissions.Has(AccountRole.Viewer, Permission.ShippingManage));
    }

    [Fact]
    public async Task Brand_scoped_manager_sees_only_account_wide_and_own_brand_settings_and_other_accounts_get_404()
    {
        using var f = new ShipFactory();
        var (s, addr, _) = await Ship.NewShop(f);
        var (mgr, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [s.Brand]);
        var other = await Orders.NewShop(f);
        var otherAddr = (string)(await Orders.Node(await other.Owner.Post(Base + "/addresses", Ship.Address("other st"))))["id"]!;
        var seen = (await Orders.Node(await mgr.Get(Base + "/settings")))["addresses"]!.AsArray().Select(a => (string)a!["id"]!).ToList();
        Assert.Contains(addr.ToString(), seen); Assert.DoesNotContain(otherAddr, seen);
        // another account's rows are 404, never 403
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.Put($"{Base}/addresses/{otherAddr}", Ship.Address("hijack"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.Delete($"{Base}/addresses/{otherAddr}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.Post($"{Base}/addresses/{otherAddr}/default")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.Put($"{Base}/products/{other.Product}/package", new { packageId = (Guid?)null })).StatusCode);
    }

    [Fact]
    public async Task Marketplace_reads_settings_through_a_signed_endpoint_only()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        await s.Owner.Post(Base + "/addresses", Ship.Address());
        await s.Owner.Post(Base + "/profiles", new { name = "Default", freeShippingThresholdCents = 7500, zones = new object[] { new { name = "US", kind = "domestic", countries = new[] { "US" }, speeds = new object[] { new { speed = "standard", method = "flat", flatCents = 500 } } } } });
        await s.Owner.Put(Base + "/settings", new { handlingDays = 5 });
        var path = $"/api/internal/v1/shipping/settings?sellerId={s.Seller}&brandId={s.Brand}";
        var ok = await f.NewClient().SendAsync(Orders.Signed(f, "GET", path, null));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var j = await Orders.Node(ok);
        Assert.Equal(5, (int)j["handlingDays"]!); Assert.Equal("US", (string)j["shipsFrom"]!["country"]!);
        Assert.Equal(7500, (long)j["profiles"]![0]!["freeShippingThresholdCents"]!);
        Assert.DoesNotContain("Warehouse Rd", await ok.Content.ReadAsStringAsync());   // no street address leaves the portal
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "GET", path, null, sign: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "GET", path, null, secret: "wrong-secret-wrong-secret-wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.NewClient().SendAsync(Orders.Signed(f, "GET", $"/api/internal/v1/shipping/settings?sellerId={Guid.NewGuid()}", null))).StatusCode);
    }
}
