using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class ServiceListingTests : IClassFixture<SvcFactory>
{
    private readonly SvcFactory _f;
    public ServiceListingTests(SvcFactory f) => _f = f;

    [Fact]
    public async Task Services_share_the_catalog_with_a_type_filter_and_show_price_model_and_duration()
    {
        var b = await Svc.NewBiz(_f);
        var product = await ProdApi.ReadyProduct(b.Owner, "Speckled stoneware coffee mug", b.Brand, "MUG-" + Guid.NewGuid().ToString("N")[..6]);
        var all = await ProdApi.Node(await b.Owner.Get("/api/v1/seller/products"));
        Assert.Equal(2, (int)all["total"]!);
        var services = await ProdApi.Node(await b.Owner.Get("/api/v1/seller/products?type=service"));
        var item = services["items"]!.AsArray().Single()!;
        Assert.Equal("Service", (string)item["type"]!);
        Assert.Equal("hourly", ((string)item["priceModel"]!).ToLowerInvariant());
        Assert.Equal(120, (int)item["durationMinutes"]!);
        Assert.Equal(60m, (decimal)item["price"]!);          // the hourly rate shows in the price column
        var products = await ProdApi.Node(await b.Owner.Get("/api/v1/seller/products?type=product"));
        Assert.Equal(product.ToString(), (string)products["items"]!.AsArray().Single()!["id"]!);
        Assert.Equal("Product", (string)products["items"]![0]!["type"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.Get("/api/v1/seller/products?type=nope")).StatusCode);
        // a service never counts as out of stock
        Assert.Equal(0, (int)(await ProdApi.Node(await b.Owner.Get("/api/v1/seller/products?filter=outofstock")))["total"]!);
    }

    [Fact]
    public async Task A_new_service_needs_pricing_location_and_a_provider_before_it_can_be_published()
    {
        var (api, seller) = await ProdApi.Approved(_f);
        var id = await Svc.NewService(api);
        var d = await ProdApi.Get(api, id);
        Assert.Equal("Service", (string)d["type"]!);
        var keys = d["issues"]!.AsArray().Where(i => (string)i!["severity"]! == "required").Select(i => (string)i!["key"]!).ToList();
        Assert.Contains("price_model", keys); Assert.Contains("location", keys); Assert.Contains("providers", keys);
        Assert.DoesNotContain("stock", keys); Assert.DoesNotContain("variants", keys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" })).StatusCode);

        var member = Svc.MemberOf(_f, seller);
        await Svc.MakeProvider(api, member);
        Assert.Equal(HttpStatusCode.OK, (await Svc.PutDetails(api, id, Svc.BaseDetails())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Svc.SetProviders(api, id, member)).StatusCode);
        // provider linked but without working hours: still not publishable
        Assert.Contains("providers", (await ProdApi.Get(api, id))["issues"]!.AsArray().Select(i => (string)i!["key"]!));
        await Svc.SetHours(api, member, Svc.WeekdayHours());
        Assert.True((bool)(await ProdApi.Get(api, id))["canPublish"]!);
        Assert.Equal(HttpStatusCode.OK, (await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" })).StatusCode);
    }

    [Fact]
    public async Task Publish_needs_one_location_with_its_details()
    {
        var (api, seller) = await ProdApi.Approved(_f);
        var member = Svc.MemberOf(_f, seller);
        await Svc.MakeProvider(api, member); await Svc.SetHours(api, member, Svc.WeekdayHours());
        var id = await Svc.NewService(api);
        Assert.Equal(HttpStatusCode.OK, (await Svc.SetProviders(api, id, member)).StatusCode);
        // online without instructions, customer location without an area, seller location without address
        var r = await Svc.PutDetails(api, id, Svc.BaseDetails(d => { d["online"] = true; d["onlineInstructions"] = null; d["atCustomer"] = true; d["serviceArea"] = new JsonObject(); d["atSeller"] = true; d["sellerAddress"] = ""; }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var keys = (await ProdApi.Get(api, id))["issues"]!.AsArray().Select(i => (string)i!["key"]!).ToList();
        Assert.Contains("online_instructions", keys); Assert.Contains("service_area", keys); Assert.Contains("seller_address", keys); Assert.Contains("location", keys);
        // a radius with a base location is a valid service area
        Assert.Equal(HttpStatusCode.OK, (await Svc.PutDetails(api, id, Svc.BaseDetails(d =>
        {
            d["online"] = false; d["atSeller"] = false;
            d["serviceArea"] = new JsonObject { ["radius"] = new JsonObject { ["baseAddress"] = "1 Main St", ["lat"] = 40.69, ["lng"] = -73.99, ["distance"] = 10, ["unit"] = "km" } };
        }))).StatusCode);
        Assert.True((bool)(await ProdApi.Get(api, id))["canPublish"]!);
    }

    [Fact]
    public async Task Service_details_are_validated_on_the_server()
    {
        var (api, _) = await ProdApi.Approved(_f);
        var id = await Svc.NewService(api);
        async Task Bad(string field, Action<JsonObject> tweak)
        {
            var r = await Svc.PutDetails(api, id, Svc.BaseDetails(tweak));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains(field, await r.Content.ReadAsStringAsync());
        }
        await Bad("price", d => d["price"] = -5);
        await Bad("incrementMinutes", d => d["incrementMinutes"] = 20);
        await Bad("defaultDurationMinutes", d => d["defaultDurationMinutes"] = 45);          // not a multiple of the 30 min increment
        await Bad("maxMinutes", d => { d["minMinutes"] = 120; d["maxMinutes"] = 60; });
        await Bad("slotGranularityMinutes", d => d["slotGranularityMinutes"] = 7);
        await Bad("minNoticeHours", d => d["minNoticeHours"] = -1);
        await Bad("maxAdvanceDays", d => d["maxAdvanceDays"] = 0);
        await Bad("cancelFeePercent", d => d["cancelFeePercent"] = 101);
        await Bad("bookingMode", d => d["bookingMode"] = "maybe");
        await Bad("pricingModel", d => d["pricingModel"] = "barter");
        await Bad("serviceArea", d => d["serviceArea"] = new JsonObject { ["postalCodes"] = new JsonArray("<script>") });
        await Bad("serviceArea", d => d["serviceArea"] = new JsonObject { ["radius"] = new JsonObject { ["distance"] = 5, ["unit"] = "km" } });        // no base location
        await Bad("serviceArea", d => d["serviceArea"] = new JsonObject { ["radius"] = new JsonObject { ["distance"] = 5, ["lat"] = 95, ["lng"] = 0, ["unit"] = "km" } });
        await Bad("intake", d => d["intake"] = new JsonArray(new JsonObject { ["label"] = "Pick one", ["type"] = "choice", ["options"] = new JsonArray("only") }));
        await Bad("addOns", d => d["addOns"] = new JsonArray(new JsonObject { ["name"] = "x", ["price"] = -1 }));
        await Bad("travelFee", d => d["travelFee"] = -3);
    }

    [Fact]
    public async Task Details_use_the_listing_version_for_optimistic_concurrency()
    {
        var (api, _) = await ProdApi.Approved(_f);
        var id = await Svc.NewService(api);
        var stale = Svc.BaseDetails(); stale["version"] = 99;
        var r = await api.Put($"/api/v1/seller/services/{id}/details", System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(stale.ToJsonString()));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("version_conflict", await r.ErrCode());
    }

    [Fact]
    public async Task Details_endpoints_refuse_ordinary_products_and_other_accounts()
    {
        var (api, seller) = await ProdApi.Approved(_f);
        var product = await ProdApi.ReadyProduct(api, "Speckled stoneware coffee mug", null, null);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Put($"/api/v1/seller/services/{product}/details", new { version = 1 })).StatusCode);
        var id = await Svc.NewService(api);
        var (other, _) = await ProdApi.Approved(_f);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get($"/api/v1/seller/services/{id}/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Put($"/api/v1/seller/services/{id}/providers", new { membershipIds = new Guid[0] })).StatusCode);
        var d = Svc.BaseDetails(); d["version"] = 1;
        Assert.Equal(HttpStatusCode.NotFound, (await other.Put($"/api/v1/seller/services/{id}/details", System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(d.ToJsonString()))).StatusCode);
        // a member of another account cannot be linked as a provider
        var foreign = Svc.MemberOf(_f, other.Session!.SellerId);
        await Svc.MakeProvider(other, foreign);
        Assert.Equal(HttpStatusCode.BadRequest, (await Svc.SetProviders(api, id, foreign)).StatusCode);
        _ = seller;
    }

    [Fact]
    public async Task Viewers_cannot_edit_services_and_managers_only_their_brands()
    {
        var b = await Svc.NewBiz(_f);
        var brand2 = await TeamHelpers.NewBrand(b.Owner, "Second brand");
        var (viewer, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Viewer");
        var (mgr1, mgr1Email) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager", [b.Brand]);
        var (mgr2, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager", [brand2]);
        var d = Svc.BaseDetails(); d["version"] = (int)(await ProdApi.Get(b.Owner, b.Service))["version"]!;
        var body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(d.ToJsonString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Put($"/api/v1/seller/services/{b.Service}/details", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get($"/api/v1/seller/services/{b.Service}/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr2.Put($"/api/v1/seller/services/{b.Service}/details", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr2.Get($"/api/v1/seller/services/{b.Service}/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mgr1.Put($"/api/v1/seller/services/{b.Service}/details", body)).StatusCode);
        // managers don't manage who is a provider
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr1.Put($"/api/v1/seller/providers/{Svc.MemberOf(_f, b.Seller, mgr1Email)}", new { isProvider = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr1.Put($"/api/v1/seller/providers/{b.OwnerMember}", new { isProvider = false })).StatusCode);
    }

    [Fact]
    public async Task Duplicating_a_service_copies_its_details_but_not_its_providers_and_deleting_needs_no_bookings()
    {
        var b = await Svc.NewBiz(_f);
        var dup = await ProdApi.Node(await b.Owner.Post($"/api/v1/seller/products/{b.Service}/duplicate"));
        Assert.Equal("Service", (string)dup["type"]!);
        Assert.Equal(60, (decimal)dup["service"]!["price"]!);
        var did = Guid.Parse((string)dup["id"]!);
        Assert.Contains("providers", (await ProdApi.Get(b.Owner, did))["issues"]!.AsArray().Select(i => (string)i!["key"]!));
        var booked = await Svc.Book(b);
        await b.Owner.Post($"/api/v1/seller/products/{b.Service}/status", new { status = "inactive" });
        var del = await b.Owner.Delete($"/api/v1/seller/products/{b.Service}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Equal("has_bookings", await del.ErrCode());
        _ = booked;
    }

    [Fact]
    public async Task Providers_are_chosen_by_owners_and_admins_and_members_manage_their_own_hours()
    {
        var b = await Svc.NewBiz(_f);
        var (mgr, email) = await TeamHelpers.JoinAs(_f, b.Owner, "Manager");
        var mem = Svc.MemberOf(_f, b.Seller, email);
        // not yet a provider: cannot set hours
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.Put($"/api/v1/seller/providers/{mem}/availability", new { weekly = new object[0], overrides = new object[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Put($"/api/v1/seller/providers/{mem}", new { isProvider = true })).StatusCode);
        await Svc.MakeProvider(b.Owner, mem, "Europe/Paris");
        Assert.Equal(HttpStatusCode.OK, (await mgr.Put($"/api/v1/seller/providers/{mem}/availability", new { weekly = new[] { new { day = 2, intervals = new[] { new { start = "08:00", end = "12:00" } } } }, overrides = new object[0] })).StatusCode);
        // but not someone else's
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Put($"/api/v1/seller/providers/{b.OwnerMember}/availability", new { weekly = new object[0], overrides = new object[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Get($"/api/v1/seller/providers/{b.OwnerMember}/availability")).StatusCode);
        // the list only shows themselves to a manager, everybody to the owner
        Assert.Single((await ProdApi.Node(await mgr.Get("/api/v1/seller/providers")))["providers"]!.AsArray());
        Assert.Equal(2, (await ProdApi.Node(await b.Owner.Get("/api/v1/seller/providers")))["providers"]!.AsArray().Count);
        // invalid zone
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Put($"/api/v1/seller/providers/{mem}", new { timeZone = "Mars/Olympus" })).StatusCode);
    }

    [Fact]
    public async Task Working_hours_reject_overlaps_bad_times_and_duplicate_days()
    {
        var b = await Svc.NewBiz(_f);
        async Task<HttpStatusCode> Put(object weekly, object? overrides = null) =>
            (await b.Owner.Put($"/api/v1/seller/providers/{b.OwnerMember}/availability", new { weekly, overrides = overrides ?? new object[0] })).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new[] { new { day = 1, intervals = new[] { new { start = "09:00", end = "12:00" }, new { start = "11:00", end = "14:00" } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new[] { new { day = 1, intervals = new[] { new { start = "12:00", end = "09:00" } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new[] { new { day = 1, intervals = new[] { new { start = "9am", end = "5pm" } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new[] { new { day = 9, intervals = new[] { new { start = "09:00", end = "12:00" } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new[] { new { day = 1, intervals = new[] { new { start = "09:00", end = "12:00" } } }, new { day = 1, intervals = new[] { new { start = "13:00", end = "14:00" } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new object[0], new[] { new { date = "2026-13-40", intervals = new object[0] } }));
        // multiple intervals, touching intervals and an override are accepted
        Assert.Equal(HttpStatusCode.OK, await Put(new[] { new { day = 1, intervals = new[] { new { start = "09:00", end = "12:00" }, new { start = "12:00", end = "14:00" }, new { start = "15:00", end = "24:00" } } } },
            new[] { new { date = "2030-12-24", intervals = new[] { new { start = "10:00", end = "12:00" } } } }));
        var got = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/providers/{b.OwnerMember}/availability"));
        Assert.Equal(3, got["weekly"]![0]!["intervals"]!.AsArray().Count);
        Assert.Equal("2030-12-24", (string)got["overrides"]![0]!["date"]!);
    }

    [Fact]
    public async Task Time_off_and_busy_blocks_remove_slots_and_can_be_deleted()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var before = await Svc.Slots(b, wed, wed.AddDays(1).AddHours(12), "&location=online");
        var count = before["slots"]!.AsArray().Count;
        Assert.True(count > 0);
        var r = await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/blocks", new { kind = "busy", start = Svc.Local(b.Tz, wed, 9), end = Svc.Local(b.Tz, wed, 12), note = "Dentist" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var block = await ProdApi.Node(r);
        var during = await Svc.Slots(b, wed, wed.AddDays(1).AddHours(12), "&location=online");
        Assert.True(during["slots"]!.AsArray().Count < count);
        Assert.DoesNotContain(during["slots"]!.AsArray(), s => (DateTime)s!["startAt"]! >= Svc.Local(b.Tz, wed, 9) && (DateTime)s["startAt"]! < Svc.Local(b.Tz, wed, 12));
        // an all-day holiday in the provider's zone closes the whole local day
        var thu = wed.AddDays(1);
        Assert.Equal(HttpStatusCode.Created, (await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/blocks", new { kind = "timeoff", allDay = true, date = thu.ToString("yyyy-MM-dd"), note = "Holiday" })).StatusCode);
        var holiday = await Svc.Slots(b, Svc.Local(b.Tz, thu, 0), Svc.Local(b.Tz, thu.AddDays(1), 0), "&location=online");
        Assert.Empty(holiday["slots"]!.AsArray());
        Assert.Equal(HttpStatusCode.NoContent, (await b.Owner.Delete($"/api/v1/seller/providers/{b.OwnerMember}/blocks/{(string)block["block"]!["id"]!}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/blocks", new { kind = "busy", start = Svc.Local(b.Tz, wed, 12), end = Svc.Local(b.Tz, wed, 9) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.Post($"/api/v1/seller/providers/{b.OwnerMember}/blocks", new { kind = "party", start = Svc.Local(b.Tz, wed, 9), end = Svc.Local(b.Tz, wed, 10) })).StatusCode);
    }

    [Fact]
    public async Task Category_for_local_services_exists()
    {
        var (api, _) = await ProdApi.Approved(_f);
        Assert.True(await ProdApi.CategoryId(api, "home-services") != Guid.Empty);
        Assert.True(await ProdApi.CategoryId(api, "local-services") != Guid.Empty);
    }
}
