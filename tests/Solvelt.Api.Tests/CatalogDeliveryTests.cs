using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Catalog;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>C5: explicit delivery options on the listing (docs/CATALOG.md): how each value is derived, and that a settings change re-emits listing.upserted only for listings that changed.</summary>
public class CatalogDeliveryTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    private static readonly object[] Zones =
    [
        new { name = "Domestic", kind = "domestic", countries = new[] { "US" }, speeds = new object[] { new { speed = "standard", method = "flat", flatCents = 500 } } }
    ];

    private static async Task<(bool Ships, bool LocalDelivery, string[] Where)> Delivery(ApiFactory f, Guid id)
    {
        var d = (await Listing(f, id))!["delivery"]!;
        return ((bool)d["ships"]!, (bool)d["localDelivery"]!, d["serviceLocations"]!.AsArray().Select(x => (string)x!).ToArray());
    }

    private static async Task<Guid> Profile(Api owner, Guid? brand = null)
    {
        var r = await owner.Post("/api/v1/seller/shipping/profiles", new { name = "Profile " + Guid.NewGuid().ToString("N")[..5], brandId = brand, currency = "USD", zones = Zones });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return Guid.Parse((string)(await Orders.Node(r))["id"]!);
    }

    private static async Task<int> PutLocal(Api owner, Guid? brand, bool enabled, int? version = null)
    {
        var r = await owner.Put("/api/v1/seller/delivery/settings", Dlv.Settings(brand, enabled, version: version));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (int)(await Orders.Node(r))["version"]!;
    }

    private async Task<int> Refresh() => (int)(await Orders.Node(await f.NewClient().PostAsync("/api/dev/catalog/refresh", null)))["bumped"]!;

    private static int OpenRequests(ApiFactory f, Guid seller) => f.WithDb(db => db.CatalogRefreshRequests.Count(r => r.SellerId == seller && r.ProcessedAt == null));

    private async Task<(Shop Shop, Guid Other, Guid OtherBrand)> TwoBrands()
    {
        var s = await Orders.NewShop(f);
        var brand2 = await TeamHelpers.NewBrand(s.Owner, "Second brand " + Guid.NewGuid().ToString("N")[..5]);
        var other = await Orders.ActiveProduct(s.Owner, "Other brand mug", brand2, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        await Refresh();                                                    // nothing pending: the new listings were versioned with their delivery state
        return (s, other, brand2);
    }

    // ---- derivation ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ships_is_true_only_with_an_unarchived_shipping_profile_that_covers_the_listings_brand()
    {
        var (s, other, brand2) = await TwoBrands();
        Assert.Equal((false, false, Array.Empty<string>()), await Delivery(f, s.Product));            // nothing configured: explicit false / false / []
        var forBrand2 = await Profile(s.Owner, brand2);
        Assert.False((await Delivery(f, s.Product)).Ships);                                           // a profile of ANOTHER brand does not cover this brand
        Assert.True((await Delivery(f, other)).Ships);
        var account = await Profile(s.Owner);                                                         // account-wide: covers every brand
        Assert.True((await Delivery(f, s.Product)).Ships); Assert.True((await Delivery(f, other)).Ships);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/shipping/profiles/{account}")).StatusCode);   // archived: no longer covers
        Assert.False((await Delivery(f, s.Product)).Ships); Assert.True((await Delivery(f, other)).Ships);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/shipping/profiles/{forBrand2}")).StatusCode);
        Assert.False((await Delivery(f, other)).Ships);
    }

    [Fact]
    public async Task LocalDelivery_is_true_only_when_the_setting_that_applies_to_the_brand_is_enabled_and_has_an_active_zone()
    {
        var (s, other, brand2) = await TwoBrands();
        Assert.False((await Delivery(f, s.Product)).LocalDelivery);
        var acct = await PutLocal(s.Owner, null, enabled: true);                                       // account-wide, enabled, one active zone
        Assert.True((await Delivery(f, s.Product)).LocalDelivery); Assert.True((await Delivery(f, other)).LocalDelivery);
        var acct2 = await PutLocal(s.Owner, null, enabled: false, version: acct);                      // disabled: nothing is offered
        Assert.False((await Delivery(f, s.Product)).LocalDelivery); Assert.False((await Delivery(f, other)).LocalDelivery);
        var own = await PutLocal(s.Owner, brand2, enabled: true);                                      // only brand 2 has its own enabled setting
        Assert.True((await Delivery(f, other)).LocalDelivery); Assert.False((await Delivery(f, s.Product)).LocalDelivery);
        await PutLocal(s.Owner, null, enabled: true, version: acct2);                                  // account-wide back on: brand 1 follows it
        Assert.True((await Delivery(f, s.Product)).LocalDelivery);
        // a brand's own DISABLED setting does not hide the account-wide one (the rule of DeliveryService.SettingFor)
        await PutLocal(s.Owner, brand2, enabled: false, version: own);
        Assert.True((await Delivery(f, other)).LocalDelivery);
    }

    [Fact]
    public async Task ServiceLocations_are_the_enabled_location_types_of_a_service_and_empty_for_products()
    {
        var b = await Svc.NewBiz(f, d => { d["online"] = true; d["atCustomer"] = true; d["atSeller"] = false; });
        var service = await Delivery(f, b.Service);
        Assert.Equal(["online", "customer"], service.Where);                                           // fixed order: online, customer, seller
        Assert.False(service.Ships); Assert.False(service.LocalDelivery);                                       // services never ship or deliver locally
        var product = await Orders.NewShop(f);
        Assert.Empty((await Delivery(f, product.Product)).Where);
        // the seller changes the service: the value follows at once, with a new version (a service detail is part of the listing)
        var v0 = Version(f, b.Service);
        var put = await Svc.PutDetails(b.Owner, b.Service, Svc.BaseDetails(d => { d["online"] = false; d["atCustomer"] = true; d["atSeller"] = true; d["sellerAddress"] = "1 Studio Way, Brooklyn, NY 11201"; }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(["customer", "seller"], (await Delivery(f, b.Service)).Where);
        Assert.True(Version(f, b.Service) > v0);
        Assert.Equal("listing.upserted", ListingEvents(f, b.Service).Last().Type);
    }

    [Fact]
    public async Task The_feed_and_the_single_listing_carry_the_same_delivery_object_and_tombstones_carry_none()
    {
        var s = await Orders.NewShop(f);
        await Profile(s.Owner);
        var single = (await Listing(f, s.Product))!["delivery"]!.ToJsonString();
        var inFeed = (await ReadFeed(f)).Items.Last(i => (string)i["listingId"]! == s.Product.ToString());
        Assert.Equal(single, inFeed["delivery"]!.ToJsonString());
        Assert.Equal("""{"ships":true,"localDelivery":false,"serviceLocations":[]}""", single);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{s.Product}/status", new { status = "inactive" })).StatusCode);
        var tomb = (await ReadFeed(f)).Items.Last(i => (string)i["listingId"]! == s.Product.ToString());
        Assert.False((bool)tomb["listed"]!); Assert.Null(tomb["delivery"]);
    }

    // ---- re-emit on a settings change ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_settings_change_returns_at_once_and_the_refresh_re_emits_upserted_only_for_listings_whose_delivery_changed()
    {
        var (s, other, brand2) = await TwoBrands();
        long Ver(Guid id) => Version(f, id);
        int Events(Guid id) => ListingEvents(f, id).Count;
        var (v1, v2, e1, e2) = (Ver(s.Product), Ver(other), Events(s.Product), Events(other));

        await Profile(s.Owner, brand2);                                                                // only brand 2's listing is affected
        Assert.Equal((v1, v2, e1, e2), (Ver(s.Product), Ver(other), Events(s.Product), Events(other)));   // the request returned without touching any listing...
        Assert.Equal(1, OpenRequests(f, s.Seller));                                                    // ...it left one request, written in the same transaction
        Assert.Equal(1, await Refresh());                                                              // the job bumps exactly one listing
        Assert.Equal((v1, e1), (Ver(s.Product), Events(s.Product)));                                   // the other brand's listing is untouched
        Assert.True(Ver(other) > v2);
        var ev = ListingEvents(f, other).Last();
        Assert.Equal("listing.upserted", ev.Type); Assert.Equal(Ver(other), (long)ev.Data["version"]!); Assert.Equal(other.ToString(), (string)ev.Data["listingId"]!);
        Assert.Equal(0, OpenRequests(f, s.Seller));

        // an account-wide local delivery setting flips both listings
        var (w1, w2) = (Ver(s.Product), Ver(other));
        await PutLocal(s.Owner, null, enabled: true);
        Assert.Equal((w1, w2), (Ver(s.Product), Ver(other)));
        Assert.Equal(2, await Refresh());                                                              // both listings flip
        Assert.True(Ver(s.Product) > w1); Assert.True(Ver(other) > w2);
        Assert.True((await Delivery(f, s.Product)).LocalDelivery);

        // running the job again, or a change that does not flip anything, bumps nothing
        Assert.Equal(0, await Refresh());
        var (x1, x2) = (Ver(s.Product), Ver(other));
        await Profile(s.Owner, brand2);                                                                // brand 2 already ships: no listing changes
        Assert.Equal(0, await Refresh());
        Assert.Equal((x1, x2), (Ver(s.Product), Ver(other)));
        // every version is unique and in increasing order
        foreach (var id in new[] { s.Product, other })
        {
            var vs = ListingEvents(f, id).Select(e => (long)e.Data["version"]!).ToList();
            Assert.Equal(vs.OrderBy(v => v), vs); Assert.Equal(vs.Count, vs.Distinct().Count());
        }
    }

    [Fact]
    public async Task A_whole_account_check_leaves_listings_alone_whose_delivery_did_not_change()
    {
        // A delivery zone does not know its brand, so its request checks the WHOLE account. Listings that were created or edited since have their delivery state stored with their
        // version (not "unknown"), so only the listing that really flips is bumped; a listing without a stored state would be bumped by every unrelated settings change.
        var (s, other, brand2) = await TwoBrands();
        var v1 = Version(f, s.Product); var e1 = ListingEvents(f, s.Product).Count; var v2 = Version(f, other);
        var brand2Version = await PutLocal(s.Owner, brand2, enabled: true);                            // brand 2 starts offering local delivery
        Assert.Equal(1, await Refresh());                                                              // exactly one listing flips
        Assert.Equal((v1, e1), (Version(f, s.Product), ListingEvents(f, s.Product).Count));            // brand 1's listing keeps its version and gets no event
        Assert.True(Version(f, other) > v2); Assert.True((await Delivery(f, other)).LocalDelivery);
        // a listing edited after its creation keeps a stored state too
        await SetPrice(s.Owner, s.Product, 31);
        var v3 = Version(f, s.Product);
        await PutLocal(s.Owner, brand2, enabled: false, version: brand2Version);
        Assert.Equal(1, await Refresh());                                                              // brand 2's listing flips back; brand 1's is untouched again
        Assert.Equal(v3, Version(f, s.Product));
    }

    [Fact]
    public async Task Zone_and_profile_archive_changes_are_picked_up_too_and_hidden_listings_are_left_alone()
    {
        var s = await Orders.NewShop(f);
        var hidden = await Orders.ActiveProduct(s.Owner, "Paused mug", s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{hidden}/status", new { status = "inactive" })).StatusCode);
        await Refresh();
        var (v, vh, eh) = (Version(f, s.Product), Version(f, hidden), ListingEvents(f, hidden).Count);
        var profile = await Profile(s.Owner);
        Assert.Equal(1, await Refresh());                                                              // the visible listing only
        Assert.Equal((vh, eh), (Version(f, hidden), ListingEvents(f, hidden).Count));                  // an unlisted listing never gets an upserted
        var v2 = Version(f, s.Product); Assert.True(v2 > v);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/shipping/profiles/{profile}")).StatusCode);
        Assert.Equal(1, await Refresh());
        Assert.True(Version(f, s.Product) > v2); Assert.False((await Delivery(f, s.Product)).Ships);
    }

    [Fact]
    public async Task Listings_that_predate_the_field_get_one_refresh_at_start_up_and_a_crashed_run_is_safe_to_repeat()
    {
        var s = await Orders.NewShop(f);
        f.WithDb(db => db.Products.Where(p => p.Id == s.Product).ExecuteUpdate(u => u.SetProperty(p => p.CatalogDeliveryState, (string?)null)));   // as if it had been versioned before C5
        var v0 = Version(f, s.Product);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>();
            var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            Assert.True(await CatalogTracking.BackfillDeliveryAsync(db, time) >= 1);
            Assert.Equal(0, await CatalogTracking.BackfillDeliveryAsync(db, time));                    // an account with an open request is not queued twice
        }
        Assert.True(await Refresh() >= 1);
        Assert.True(Version(f, s.Product) > v0);
        Assert.Equal("listing.upserted", ListingEvents(f, s.Product).Last().Type);
        var v1 = Version(f, s.Product);
        Assert.Equal(0, await Refresh());                                                              // nothing left to do
        Assert.Equal(v1, Version(f, s.Product));
    }
}
