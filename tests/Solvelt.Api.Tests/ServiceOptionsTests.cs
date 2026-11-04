using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>C11 (cancellation policy in booking summaries, events and the catalog service document) and C12 (the signed booking options endpoint), docs/SERVICES.md and docs/CATALOG.md.</summary>
public class ServiceOptionsTests(SvcFactory f) : IClassFixture<SvcFactory>
{
    private static string Path(Guid service) => $"/api/internal/v1/services/{service}/options";
    private async Task<HttpResponseMessage> Options(Guid service, string? ifNoneMatch = null)
    {
        var req = Orders.Signed(f, "GET", Path(service), null);
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await f.NewClient().SendAsync(req);
    }
    private async Task<JsonNode> OptionsOk(Guid service) { var r = await Options(service); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await Orders.Node(r); }
    private async Task<JsonNode> Booking(string mid) { var r = await f.NewClient().SendAsync(Orders.Signed(f, "GET", $"/api/internal/v1/bookings/{mid}", null)); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await Orders.Node(r); }
    private static JsonNode ServiceDoc(ApiFactory f, Guid id) => Listing(f, id).GetAwaiter().GetResult()!["service"]!;

    // ---- C12: the endpoint ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Options_of_a_visible_service_have_the_documented_shape_and_everything_else_is_404()
    {
        var b = await Svc.NewBiz(f, d => { d["intake"] = new JsonArray(new JsonObject { ["id"] = "pets", ["label"] = "Any pets?", ["type"] = "yesno", ["required"] = false },
            new JsonObject { ["id"] = "size", ["label"] = "Home size", ["type"] = "choice", ["options"] = new JsonArray("S", "M", "L"), ["required"] = true }); });
        var r = await Options(b.Service);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(r.Headers.ETag); Assert.True(r.Headers.CacheControl!.Private); Assert.Equal(TimeSpan.FromSeconds(300), r.Headers.CacheControl.MaxAge);
        var o = await Orders.Node(r);
        Assert.Equal(["addOns", "assignment", "intakeQuestions", "policy", "providers", "serviceId", "travel", "version"], ((JsonObject)o).Select(k => k.Key).OrderBy(k => k).ToArray());
        Assert.Equal(b.Service.ToString(), (string)o["serviceId"]!); Assert.Equal(Version(f, b.Service), (long)o["version"]!);
        var a = o["addOns"]![0]!; Assert.Equal(("deep", "Deep clean", 2500L, 30), ((string)a["id"]!, (string)a["name"]!, (long)a["priceCents"]!, (int)a["extraMinutes"]!));
        var q = o["intakeQuestions"]!.AsArray();
        Assert.Equal(("pets", "yesNo", false), ((string)q[0]!["id"]!, (string)q[0]!["kind"]!, (bool)q[0]!["required"]!)); Assert.Null(q[0]!["choices"]);
        Assert.Equal(("size", "choice", true), ((string)q[1]!["id"]!, (string)q[1]!["kind"]!, (bool)q[1]!["required"]!)); Assert.Equal(["S", "M", "L"], q[1]!["choices"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal("buyerChoice", (string)o["assignment"]!);
        Assert.Equal(1000, (long)o["travel"]!["feeCents"]!); Assert.Equal(["11201", "112*"], o["travel"]!["serviceAreaPostalCodes"]!.AsArray().Select(x => (string)x!).ToArray());
        var pol = o["policy"]!; Assert.Equal((24, 50, 100, 2), ((int)pol["cancelFreeHours"]!, (int)pol["cancelFeePercent"]!, (int)pol["noShowFeePercent"]!, (int)pol["maxReschedules"]!)); Assert.Null(pol["approvalHours"]);   // instant service
        Assert.Single(o["providers"]!.AsArray());
        // not visible: a draft service, a paused one, a product, an unknown id, and unsigned requests
        var draft = await Svc.NewBiz(f, activate: false);
        Assert.Equal(HttpStatusCode.NotFound, (await Options(draft.Service)).StatusCode);
        var paused = await Svc.NewBiz(f);
        Assert.Equal(HttpStatusCode.OK, (await paused.Owner.Post($"/api/v1/seller/products/{paused.Service}/status", new { status = "inactive" })).StatusCode);
        var gone = await Options(paused.Service); Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode); Assert.Equal("not_found", await Orders.Code(gone));
        var shop = await Orders.NewShop(f);
        Assert.Equal(HttpStatusCode.NotFound, (await Options(shop.Product)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Options(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "GET", Path(b.Service), null, sign: false))).StatusCode);
        var nonce = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.OK, (await f.NewClient().SendAsync(Orders.Signed(f, "GET", Path(b.Service), null, nonce))).StatusCode);
        var replay = await f.NewClient().SendAsync(Orders.Signed(f, "GET", Path(b.Service), null, nonce));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode); Assert.Equal("replayed", await Orders.Code(replay));
    }

    [Fact]
    public async Task Options_never_contain_provider_contact_data_addresses_meeting_links_or_calendar_data()
    {
        var b = await Svc.NewBiz(f);
        var email = Pay.OwnerEmail(f, b.Seller);
        var body = await (await Options(b.Service)).Content.ReadAsStringAsync();
        var o = JsonNode.Parse(body)!;
        var provider = (JsonObject)o["providers"]![0]!;
        Assert.Equal(["displayName", "providerId"], provider.Select(k => k.Key).OrderBy(k => k).ToArray());           // assert on the keys: nothing else is ever added to a provider
        Assert.Equal(b.OwnerMember.ToString(), (string)provider["providerId"]!);
        var name = (string)provider["displayName"]!;
        Assert.DoesNotContain("@", name); Assert.NotEmpty(name);
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("meet.example.test", body);                                                              // the online instructions
        Assert.DoesNotContain("Clear the area first", body);                                                           // requirements text
        foreach (var banned in new[] { "email", "phone", "address", "lat", "lng", "baseAddress", "timeZone", "calendar", "ics", "onlineInstructions", "sellerAddress", "photo" })
            Assert.DoesNotContain("\"" + banned, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_options_carry_an_ETag_that_answers_If_None_Match_with_304_and_changes_with_the_content()
    {
        var b = await Svc.NewBiz(f);
        var r1 = await Options(b.Service);
        var etag = r1.Headers.ETag!.Tag;
        var r2 = await Options(b.Service, etag);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
        Assert.Equal(etag, r2.Headers.ETag!.Tag); Assert.True(r2.Headers.CacheControl!.Private); Assert.Equal(TimeSpan.FromSeconds(300), r2.Headers.CacheControl.MaxAge); Assert.Empty(await r2.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotModified, (await Options(b.Service, "\"nope\", " + etag)).StatusCode);          // a list of tags
        Assert.Equal(HttpStatusCode.OK, (await Options(b.Service, "\"stale-tag\"")).StatusCode);
        // a change to the options gives a new tag: the old one no longer answers 304
        Assert.Equal(HttpStatusCode.OK, (await Svc.PutDetails(b.Owner, b.Service, Svc.BaseDetails(d => d["travelFee"] = 15))).StatusCode);
        var r3 = await Options(b.Service, etag);
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode); Assert.NotEqual(etag, r3.Headers.ETag!.Tag);
        Assert.Equal(1500, (long)(await Orders.Node(r3))["travel"]!["feeCents"]!);
    }

    [Fact]
    public async Task Changing_add_ons_assignment_policy_or_travel_bumps_the_listing_version_and_emits_listing_upserted()
    {
        var b = await Svc.NewBiz(f);
        async Task<long> Change(Action<JsonObject> tweak)
        {
            var v0 = Version(f, b.Service); var n0 = ListingEvents(f, b.Service).Count;
            Assert.Equal(HttpStatusCode.OK, (await Svc.PutDetails(b.Owner, b.Service, Svc.BaseDetails(tweak))).StatusCode);
            var v1 = Version(f, b.Service);
            Assert.True(v1 > v0, "a change to the options must bump the version");
            Assert.Equal("listing.upserted", ListingEvents(f, b.Service)[^1].Type); Assert.True(ListingEvents(f, b.Service).Count > n0);
            return v1;
        }
        await Change(d => d["addOns"] = new JsonArray(new JsonObject { ["id"] = "deep", ["name"] = "Deep clean", ["price"] = 25, ["extraMinutes"] = 30 }, new JsonObject { ["id"] = "win", ["name"] = "Windows", ["price"] = 15, ["extraMinutes"] = 20 }));
        Assert.Equal(2, (await OptionsOk(b.Service))["addOns"]!.AsArray().Count);
        await Change(d => d["assignmentRule"] = "leastBusy");
        var o = await OptionsOk(b.Service);
        Assert.Equal("leastBusy", (string)o["assignment"]!); Assert.Single(o["providers"]!.AsArray());              // still listed for display; the marketplace must not send a provider choice
        await Change(d => { d["cancelFeePercent"] = 20; d["cancelFreeHours"] = 48; });
        Assert.Equal((48, 20), ((int)(await OptionsOk(b.Service))["policy"]!["cancelFreeHours"]!, (int)(await OptionsOk(b.Service))["policy"]!["cancelFeePercent"]!));
        await Change(d => d["travelFee"] = 12);
        Assert.Equal(1200, (long)(await OptionsOk(b.Service))["travel"]!["feeCents"]!);
    }

    [Fact]
    public async Task A_hold_with_an_add_on_that_is_not_in_the_options_is_a_422_and_a_listed_one_is_accepted()
    {
        var b = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday);
        var bad = await Svc.Post(b, "/bookings/holds", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 10), "online", addOns: ["nope"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode); Assert.Equal("add_on", await bad.ErrCode());
        var ok = await Svc.Post(b, "/bookings/holds", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 10), "online", addOns: ["deep"]));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("deep", (string)(await OptionsOk(b.Service))["addOns"]![0]!["id"]!);                              // the id a hold accepts is the id the options list
    }

    // ---- C11: the policy ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_booking_summary_carries_the_policy_snapshot_and_later_service_edits_change_the_catalog_policy_but_not_the_booking()
    {
        var b = await Svc.NewBiz(f);
        var (mid, id, _) = await Svc.Book(b);
        var before = (await Booking(mid))["booking"] ?? await Booking(mid);
        var pol = before["policy"]!;
        Assert.Equal((24, 50, 100, 2), ((int)pol["cancelFreeHours"]!, (int)pol["cancelFeePercent"]!, (int)pol["noShowFeePercent"]!, (int)pol["maxReschedules"]!));
        Assert.Null(pol["approvalHours"]);                                                                              // an instant-booking service has no approval window
        Assert.Equal(["approvalHours", "cancelFeePercent", "cancelFreeHours", "maxReschedules", "noShowFeePercent"], ((JsonObject)pol).Select(k => k.Key).OrderBy(k => k).ToArray());

        var v0 = Version(f, b.Service);
        Assert.Equal(HttpStatusCode.OK, (await Svc.PutDetails(b.Owner, b.Service, Svc.BaseDetails(d => { d["cancelFreeHours"] = 72; d["cancelFeePercent"] = 10; d["maxReschedules"] = 5; d["noShowFeePercent"] = 20; }))).StatusCode);
        var after = ((await Booking(mid))["booking"] ?? await Booking(mid))["policy"]!;
        Assert.Equal((24, 50, 100, 2), ((int)after["cancelFreeHours"]!, (int)after["cancelFeePercent"]!, (int)after["noShowFeePercent"]!, (int)after["maxReschedules"]!));    // the booking keeps ITS policy
        var cat = ServiceDoc(f, b.Service)["policy"]!;
        Assert.Equal((72, 10, 20, 5), ((int)cat["cancelFreeHours"]!, (int)cat["cancelFeePercent"]!, (int)cat["noShowFeePercent"]!, (int)cat["maxReschedules"]!));                    // the catalog shows the CURRENT one
        Assert.True(Version(f, b.Service) > v0); Assert.Equal("listing.upserted", ListingEvents(f, b.Service)[^1].Type);
        var (items, _) = await ReadFeed(f);
        Assert.Equal(72, (int)items.Single(i => (string)i["listingId"]! == b.Service.ToString())["service"]!["policy"]!["cancelFreeHours"]!);   // and the feed
    }

    [Fact]
    public async Task A_service_that_needs_approval_snapshots_its_approval_window()
    {
        var b = await Svc.NewBiz(f, d => { d["bookingMode"] = "request"; d["approvalHours"] = 12; });
        Assert.Equal(12, (int)ServiceDoc(f, b.Service)["policy"]!["approvalHours"]!);
        var (mid, _, _) = await Svc.Book(b);
        var pol = ((await Booking(mid))["booking"] ?? await Booking(mid))["policy"]!;
        Assert.Equal(12, (int)pol["approvalHours"]!);
    }

    [Fact]
    public async Task Hold_and_confirm_responses_and_booking_events_carry_the_policy()
    {
        var b = await Svc.NewBiz(f);
        var wed = Svc.Next(f, DayOfWeek.Wednesday); var mid = Svc.BookingId(); var start = Svc.Local(b.Tz, wed, 10);
        var hold = await Orders.Node(await Svc.Post(b, "/bookings/holds", Svc.Body(b, mid, start, "online")));
        Assert.Equal(50, (int)hold["booking"]!["policy"]!["cancelFeePercent"]!);
        var confirm = await Orders.Node(await Svc.Post(b, $"/bookings/{mid}/confirm", Svc.Body(b, mid, start, "online")));
        Assert.Equal(50, (int)confirm["booking"]!["policy"]!["cancelFeePercent"]!);
        var ev = Svc.Events(f, b.Seller, "booking.confirmed").Single(e => (string)e.Data["booking"]!["marketplaceBookingId"]! == mid);
        Assert.Equal(24, (int)ev.Data["policy"]!["cancelFreeHours"]!);
        Assert.Null(ev.Data["policy"]!["approvalHours"]);
        // no personal data came along with it
        Assert.DoesNotContain("relay-j@", ev.Data.ToJsonString()); Assert.DoesNotContain("Jane", ev.Data.ToJsonString());
    }
}
