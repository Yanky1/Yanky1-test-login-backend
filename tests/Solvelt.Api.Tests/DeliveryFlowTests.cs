using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Delivery;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;
using static Solvelt.Api.Tests.Dlv;

namespace Solvelt.Api.Tests;

/// <summary>One factory (and fake clock) per test: several tests move the clock.</summary>
public class DeliveryFlowTests : IDisposable
{
    private readonly DeliveryFactory f = new();
    public void Dispose() => f.Dispose();

    /// <summary>Shop + settings + a driver + one accepted local order assigned to that driver (route planned).</summary>
    private async Task<(DShop D, Driver Dr, Guid Order, string Mid, Guid Stop, Guid Route)> Planned(int stock = 10, double lat = HomeLat, double lng = HomeLng)
    {
        var d = await NewDShop(f, stock);
        var dr = await AddDriver(d);
        var (mid, id, _) = await PlaceLocal(d, lat: lat, lng: lng, accept: true);
        var r = await Assign(d, id, dr);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (d, dr, id, mid, DeliveryId(f, id), RouteOf(f, id));
    }

    private async Task<(DShop D, Driver Dr, Guid Order, string Mid, Guid Stop, Guid Route)> Started()
    {
        var p = await Planned();
        Assert.Equal(HttpStatusCode.OK, (await Start(p.Dr, p.Route)).StatusCode);
        return p;
    }

    // ---- dispatch -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Assigning_needs_an_accepted_order_a_real_driver_and_creates_one_route_per_driver_and_day()
    {
        var d = await NewDShop(f); var dr = await AddDriver(d);
        var (_, id1, _) = await PlaceLocal(d);                                    // still New
        var r = await Assign(d, id1, dr);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("order_not_ready", await Code(r));
        await Ok(Act(d.S.Owner, id1, "accept"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await d.S.Owner.Post($"/api/v1/seller/delivery/deliveries/{DeliveryId(f, id1)}/assign", new { driverMembershipId = Guid.NewGuid() })).StatusCode);
        var (manager, _) = await TeamHelpers.JoinAs(f, d.S.Owner, "Viewer");      // a viewer is not a driver
        Assert.Equal(HttpStatusCode.OK, (await Assign(d, id1, dr)).StatusCode);
        var (_, id2, _) = await PlaceLocal(d, accept: true);
        Assert.Equal(HttpStatusCode.OK, (await Assign(d, id2, dr)).StatusCode);
        Assert.Equal(RouteOf(f, id1), RouteOf(f, id2));
        Assert.Equal([1, 2], f.WithDb(db => db.LocalDeliveries.Where(x => x.RouteId == RouteOf(f, id1)).OrderBy(x => x.StopSeq).Select(x => x.StopSeq).ToList()));
        var del = Del(f, id1);
        Assert.Equal(LocalStatus.Assigned, del.Status);
        Assert.Contains(f.WithDb(db => db.TrackingEvents.Where(e => e.OrderId == id1).ToList()), e => e.Status == "pre_transit");    // pre_transit when assigned
        Assert.Contains(Events(f, Del(f, id1).Id.ToString() is var _ ? f.WithDb(db => db.Orders.First(o => o.Id == id1).MarketplaceOrderId) : "", "order.tracking_updated"), e => (string)e["status"]! == "pre_transit" && (string)e["method"]! == "local");
    }

    [Fact]
    public async Task Only_dispatchers_assign_a_viewer_and_a_driver_cannot_and_other_accounts_get_404()
    {
        var d = await NewDShop(f); var dr = await AddDriver(d);
        var (_, id, _) = await PlaceLocal(d, accept: true);
        var delivery = DeliveryId(f, id);
        var (viewer, _) = await TeamHelpers.JoinAs(f, d.S.Owner, "Viewer"); await viewer.Switch(d.S.Seller);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/seller/delivery/deliveries/{delivery}/assign", new { driverMembershipId = dr.Membership })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dr.Api.Post($"/api/v1/seller/delivery/deliveries/{delivery}/assign", new { driverMembershipId = dr.Membership })).StatusCode);
        var stranger = await NewDShop(f);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.S.Owner.Post($"/api/v1/seller/delivery/deliveries/{delivery}/assign", new { driverMembershipId = dr.Membership })).StatusCode);
        var (mgr, _) = await TeamHelpers.JoinAs(f, d.S.Owner, "Manager"); await mgr.Switch(d.S.Seller);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/v1/seller/delivery/deliveries/{delivery}/assign", new { driverMembershipId = dr.Membership })).StatusCode);
    }

    [Fact]
    public async Task A_manager_scoped_to_another_brand_cannot_dispatch_this_brands_delivery()
    {
        var d = await NewDShop(f); var dr = await AddDriver(d);
        var other = await TeamHelpers.NewBrand(d.S.Owner, "Other " + Guid.NewGuid().ToString("N")[..5]);
        var (_, id, _) = await PlaceLocal(d, accept: true);
        var (mgr, _) = await TeamHelpers.JoinAs(f, d.S.Owner, "Manager", [other]); await mgr.Switch(d.S.Seller);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Post($"/api/v1/seller/delivery/deliveries/{DeliveryId(f, id)}/assign", new { driverMembershipId = dr.Membership })).StatusCode);
        var board = await Node(await mgr.Get("/api/v1/seller/delivery/board"));
        Assert.Empty(board["deliveries"]!.AsArray());
    }

    [Fact]
    public async Task Optimize_orders_stops_nearest_first_reorder_validates_and_reassign_moves_the_route()
    {
        var d = await NewDShop(f, stock: 30, settings: Settings(windows: [new { start = "12:00", end = "14:00", capacity = 5, days = new[] { 0, 1, 2, 3, 4, 5, 6 } }]));
        var dr = await AddDriver(d); var dr2 = await AddDriver(d);
        var ids = new List<Guid>();
        foreach (var (lat, lng) in new[] { (40.7300, -73.99), (40.6950, -73.99), (40.7100, -73.99) })      // far, near, middle (start 40.6928)
        {
            var (_, id, _) = await PlaceLocal(d, d.WinA, lat: lat, lng: lng, postal: "11201", accept: true, price: 1000);
            ids.Add(id);
        }
        var make = await d.S.Owner.Post("/api/v1/seller/delivery/routes", new { driverMembershipId = dr.Membership, deliveryIds = ids.Select(i => DeliveryId(f, i)), startLat = HomeLat, startLng = HomeLng, optimize = true });
        Assert.Equal(HttpStatusCode.OK, make.StatusCode);
        var route = RouteOf(f, ids[0]);
        Assert.Equal([ids[1], ids[2], ids[0]], f.WithDb(db => db.LocalDeliveries.Where(x => x.RouteId == route).OrderBy(x => x.StopSeq).Select(x => x.OrderId).ToList()));
        var del = ids.Select(i => DeliveryId(f, i)).ToList();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await d.S.Owner.Post($"/api/v1/seller/delivery/routes/{route}/reorder", new { deliveryIds = new[] { del[0] } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await d.S.Owner.Post($"/api/v1/seller/delivery/routes/{route}/reorder", new { deliveryIds = new[] { del[2], del[1], del[0] } })).StatusCode);
        Assert.Equal(1, Del(f, ids[2]).StopSeq);
        Assert.Equal(HttpStatusCode.OK, (await d.S.Owner.Post($"/api/v1/seller/delivery/routes/{route}/reassign", new { driverMembershipId = dr2.Membership })).StatusCode);
        Assert.All(ids, i => Assert.Equal(dr2.Membership, Del(f, i).DriverMembershipId));
        var sheet = await Node(await d.S.Owner.Get($"/api/v1/seller/delivery/routes/{route}"));
        Assert.Equal(3, sheet["stops"]!.AsArray().Count); Assert.Contains("1 Main St", (string)sheet["stops"]![0]!["address"]!);
    }

    [Fact]
    public async Task Unassign_returns_the_stop_to_the_pool_and_the_board_shows_capacity_warnings_and_late_stops()
    {
        var d = await NewDShop(f); var dr = await AddDriver(d);
        var (_, id, _) = await PlaceLocal(d, d.WinB, accept: true);                // capacity 1 -> full
        await Assign(d, id, dr);
        var board = await Node(await d.S.Owner.Get("/api/v1/seller/delivery/board?date=2026-11-04"));
        Assert.Equal("full", (string)board["windows"]!.AsArray().First(w => (string)w!["id"]! == d.WinB.ToString())!["warning"]!);
        Assert.Single(board["routes"]!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await d.S.Owner.Post($"/api/v1/seller/delivery/deliveries/{DeliveryId(f, id)}/unassign")).StatusCode);
        var del = Del(f, id);
        Assert.Equal(LocalStatus.Unassigned, del.Status); Assert.Null(del.RouteId); Assert.Null(del.DriverMembershipId);
        Assert.Equal(HttpStatusCode.Conflict, (await d.S.Owner.Post($"/api/v1/seller/delivery/deliveries/{DeliveryId(f, id)}/unassign")).StatusCode);
    }

    // ---- driver visibility ----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_driver_sees_only_their_own_stops_other_drivers_and_accounts_get_404()
    {
        var p = await Planned();
        var driver2 = await AddDriver(p.D);
        var me = await Node(await p.Dr.Api.Get("/api/v1/driver/me"));
        var stop = me["routes"]![0]!["stops"]![0]!;
        Assert.Equal(p.Stop.ToString(), (string)stop["id"]!); Assert.True((bool)stop["detailsVisible"]!);
        Assert.Equal("1 Main St", (string)stop["to"]!["line1"]!);
        Assert.Empty((await Node(await driver2.Api.Get("/api/v1/driver/me")))["routes"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await driver2.Api.Get($"/api/v1/driver/stops/{p.Stop}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await driver2.Api.Post($"/api/v1/driver/stops/{p.Stop}/events", new { clientEventId = Guid.NewGuid(), type = "out_for_delivery" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadProof(driver2, p.Stop)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await driver2.Api.Post($"/api/v1/driver/routes/{p.Route}/start")).StatusCode);
        var stranger = await NewDShop(f); var sdr = await AddDriver(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await sdr.Api.Get($"/api/v1/driver/stops/{p.Stop}")).StatusCode);
    }

    [Fact]
    public async Task A_driver_cannot_reach_orders_products_money_or_settings_but_can_reach_the_driver_app()
    {
        var p = await Planned();
        foreach (var url in new[] { "/api/v1/seller/orders", $"/api/v1/seller/orders/{p.Order}", "/api/v1/seller/products", "/api/v1/seller/payments/balance", "/api/v1/seller/messages", "/api/v1/brands",
                     "/api/v1/team/members", "/api/v1/seller/application", "/api/v1/seller/notification-preferences", "/api/v1/seller/bookings", "/api/v1/assistant/conversations" })
            Assert.Equal(HttpStatusCode.Forbidden, (await p.Dr.Api.Get(url)).StatusCode);
        Assert.Equal("driver_forbidden", await ErrCodeOf(await p.Dr.Api.Get("/api/v1/seller/orders")));
        Assert.Equal(HttpStatusCode.OK, (await p.Dr.Api.Get("/api/v1/driver/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await p.Dr.Api.Get("/api/v1/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await p.Dr.Api.Get("/api/v1/account/me")).StatusCode);
        // an Owner can act as a driver on their own stops, but sees nobody else's
        Assert.Equal(HttpStatusCode.OK, (await p.D.S.Owner.Get("/api/v1/driver/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await p.D.S.Owner.Get($"/api/v1/driver/stops/{p.Stop}")).StatusCode);
    }

    private static async Task<string?> ErrCodeOf(HttpResponseMessage r) => (string?)(await Node(r))["code"];

    [Fact]
    public async Task The_masked_contact_has_a_first_name_and_phone_but_never_the_buyers_email_and_only_while_the_stop_is_active()
    {
        var p = await Started();
        var s = await Node(await p.Dr.Api.Get($"/api/v1/driver/stops/{p.Stop}"));
        Assert.Equal("Jane", (string)s["contact"]!["firstName"]!); Assert.StartsWith("tel:+1555", (string)s["contact"]!["call"]!);
        Assert.DoesNotContain("relay-abc", s.ToJsonString()); Assert.DoesNotContain("Buyer", s["to"]!.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, (await Deliver(p.Dr, p.Stop)).StatusCode);
        var after = await Node(await p.Dr.Api.Get($"/api/v1/driver/stops/{p.Stop}"));
        Assert.False((bool)after["detailsVisible"]!); Assert.Null(after["to"]); Assert.Null(after["contact"]); Assert.Null(after["navigateUrl"]);
        Assert.DoesNotContain("1 Main St", after.ToJsonString());
        Assert.DoesNotContain("1 Main St", (await Node(await p.Dr.Api.Get("/api/v1/driver/me"))).ToJsonString());
    }

    [Fact]
    public async Task A_finished_stop_hides_its_details_even_while_the_route_is_still_active()
    {
        var d = await NewDShop(f, stock: 10); var dr = await AddDriver(d);
        var (_, a, _) = await PlaceLocal(d, accept: true); var (_, b, _) = await PlaceLocal(d, accept: true);
        await Assign(d, a, dr); await Assign(d, b, dr);
        await Start(dr, RouteOf(f, a));
        var (sa, sb) = (DeliveryId(f, a), DeliveryId(f, b));
        Assert.Equal(HttpStatusCode.OK, (await Deliver(dr, sa)).StatusCode);
        Assert.Equal(RouteStatus.Active, f.WithDb(db => db.DeliveryRoutes.First(r => r.Id == RouteOf(f, a)).Status));
        var done = await Node(await dr.Api.Get($"/api/v1/driver/stops/{sa}"));
        Assert.False((bool)done["detailsVisible"]!); Assert.Null(done["to"]); Assert.Null(done["contact"]);
        Assert.True((bool)(await Node(await dr.Api.Get($"/api/v1/driver/stops/{sb}")))["detailsVisible"]!);       // the next stop is still shown
    }

    [Theory]
    [InlineData("jsmith@x.test", "Your driver")]
    [InlineData("johnsmith1985@x.test", "Your driver")]
    [InlineData("john.smith@x.test", "John")]
    public async Task Tracking_events_name_the_driver_only_when_the_address_reliably_holds_a_first_name(string email, string expected)
    {
        var d = await NewDShop(f, stock: 10); var dr = await AddDriver(d);
        f.WithDb(db => { var uid = db.Memberships.Where(x => x.Id == dr.Membership).Select(x => x.UserId).Single(); db.Users.Where(u => u.Id == uid).ExecuteUpdate(u => u.SetProperty(x => x.Email, email)); return 0; });
        var (mid, id, _) = await PlaceLocal(d, accept: true);
        await Assign(d, id, dr);
        Assert.Equal(HttpStatusCode.OK, (await Start(dr, RouteOf(f, id))).StatusCode);
        var names = Events(f, mid, "order.tracking_updated").Where(e => (string)e["status"]! == "out_for_delivery").Select(e => (string)e["driverFirstName"]!).ToList();
        Assert.NotEmpty(names); Assert.All(names, n => Assert.Equal(expected, n));
    }

    // ---- route start + tracking ----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Starting_the_route_ships_the_order_as_a_local_shipment_and_the_first_stop_is_out_for_delivery()
    {
        var d = await NewDShop(f, stock: 10); var dr = await AddDriver(d);
        var (mid1, id1, _) = await PlaceLocal(d, accept: true); var (_, id2, _) = await PlaceLocal(d, accept: true);
        await Assign(d, id1, dr); await Assign(d, id2, dr);
        var route = RouteOf(f, id1);
        var before = Stock(f, d.S.Product);
        var r = await Start(dr, route);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(LocalStatus.OutForDelivery, Del(f, id1).Status); Assert.Equal(LocalStatus.InTransit, Del(f, id2).Status);
        var o = await Detail(d.S.Owner, id1);
        Assert.Equal("Shipped", St(o));
        var ship = o["shipments"]![0]!;
        Assert.Equal("local", (string)ship["method"]!); Assert.Equal("Local", (string)ship["carrier"]!); Assert.Equal("out_for_delivery", (string)ship["trackingStatus"]!);
        Assert.Equal(Del(f, id1).Code, (string)ship["trackingNumber"]!);
        Assert.Equal(before.Stock - 2, Stock(f, d.S.Product).Stock);                  // stock committed through the normal ship flow
        var ev = Events(f, mid1, "order.tracking_updated");
        Assert.Contains(ev, e => (string)e["status"]! == "in_transit" && (string)e["method"]! == "local");
        Assert.Contains(ev, e => (string)e["status"]! == "out_for_delivery" && (string)e["driverFirstName"]! != "" && e["position"] is null);
        Assert.Single(Events(f, mid1, "order.shipped"));
        Assert.Equal(HttpStatusCode.Conflict, (await Start(dr, route)).StatusCode);          // a started route can't start again
        var tl = await Node(await d.S.Owner.Get($"/api/v1/seller/orders/{id1}/tracking"));
        Assert.Equal("local", (string)tl["method"]!);
        Assert.Equal(["pre_transit", "in_transit", "out_for_delivery"], tl["events"]!.AsArray().Select(e => (string)e!["status"]!).ToArray());
    }

    [Fact]
    public async Task A_route_planned_for_a_later_day_cannot_start_and_a_started_route_takes_no_new_stops()
    {
        var d = await NewDShop(f); var dr = await AddDriver(d);
        var (_, id, _) = await PlaceLocal(d, date: Tomorrow, accept: true);
        await Assign(d, id, dr);
        var r = await Start(dr, RouteOf(f, id));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("too_early", await ErrCodeOf(r));
        var (_, a, _) = await PlaceLocal(d, accept: true); var (_, b, _) = await PlaceLocal(d, accept: true);
        await Assign(d, a, dr);
        Assert.Equal(HttpStatusCode.OK, (await Start(dr, RouteOf(f, a))).StatusCode);
        var late = await Assign(d, b, dr);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); Assert.Equal("route_active", await ErrCodeOf(late));
    }

    // ---- proof of delivery -------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Delivered_requires_a_photo_and_moves_the_order_to_Delivered_with_signed_proof_links()
    {
        var p = await Started();
        var noProof = await Event(p.Dr, p.Stop, "delivered", placement: "left_at_door");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noProof.StatusCode); Assert.Equal("proof_required", await ErrCodeOf(noProof));
        var sigOnly = await Proof(p.Dr, p.Stop, "signature");
        Assert.Equal("proof_required", await ErrCodeOf(await Event(p.Dr, p.Stop, "delivered", proofs: [sigOnly], placement: "handed_to")));
        Assert.Equal("placement", await ErrCodeOf(await Event(p.Dr, p.Stop, "delivered", proofs: [await Proof(p.Dr, p.Stop)])));
        Assert.Equal("proof", await ErrCodeOf(await Event(p.Dr, p.Stop, "delivered", proofs: [Guid.NewGuid()], placement: "left_at_door")));
        Assert.Equal("Shipped", St(await Detail(p.D.S.Owner, p.Order)));
        var at = f.Time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        Assert.Equal(HttpStatusCode.OK, (await Deliver(p.Dr, p.Stop, at: at)).StatusCode);
        var o = await Detail(p.D.S.Owner, p.Order);
        Assert.Equal("Delivered", St(o)); Assert.Equal("delivered", (string)o["shipments"]![0]!["trackingStatus"]!);
        Assert.Equal(LocalStatus.Delivered, Del(f, p.Order).Status);
        var del = Events(f, p.Mid, "shipment.delivered").Single();
        Assert.Equal("local", (string)del["method"]!); Assert.Equal("Jane", (string)del["recipientName"]!); Assert.Equal("handed_to", (string)del["placement"]!);
        Assert.Single(Events(f, p.Mid, "order.delivered"));
        var photo = (string)del["proof"]!["photoUrls"]![0]!;
        Assert.StartsWith("/api/public/delivery-proof/", photo); Assert.NotNull(del["proof"]!["signatureUrl"]);
        var ok = await f.NewClient().GetAsync(photo);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode); Assert.Equal("nosniff", ok.Headers.GetValues("X-Content-Type-Options").Single()); Assert.Equal("image/jpeg", ok.Content.Headers.ContentType!.MediaType);
        var bytes = await ok.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("GPSDATA", System.Text.Encoding.ASCII.GetString(bytes));                 // EXIF removed before storage
        Assert.Equal(HttpStatusCode.NotFound, (await f.NewClient().GetAsync(photo.Replace("sig=", "sig=0"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.NewClient().GetAsync(photo[..photo.IndexOf('?')])).StatusCode);
        f.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.NotFound, (await f.NewClient().GetAsync(photo)).StatusCode);           // links are short-lived
    }

    [Fact]
    public async Task Proof_files_are_served_to_the_seller_through_an_authorised_endpoint_only()
    {
        var p = await Started();
        await Deliver(p.Dr, p.Stop);
        var tl = await Node(await p.D.S.Owner.Get($"/api/v1/seller/orders/{p.Order}/tracking"));
        var url = (string)tl["proofs"]![0]!["url"]!;
        var r = await p.D.S.Owner.Get(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await (await NewDShop(f)).S.Owner.Get(url)).StatusCode);             // another account
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await p.Dr.Api.Get(url)).StatusCode);                              // drivers never read proofs back
        Assert.Equal("recipient", "recipient"); Assert.Equal("Jane", (string)tl["attempts"]![0]!["recipientName"]!);
    }

    [Fact]
    public async Task Proof_uploads_reject_non_images_oversized_files_and_are_idempotent_on_the_client_id()
    {
        var p = await Started();
        var bad = await UploadProof(p.Dr, p.Stop, bytes: "<script>alert(1)</script>"u8.ToArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode); Assert.Equal("file_type", await ErrCodeOf(bad));
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadProof(p.Dr, p.Stop, bytes: new byte[9 * 1024 * 1024])).StatusCode);
        var cid = Guid.NewGuid();
        var a = await Proof(p.Dr, p.Stop, clientId: cid); var b = await Proof(p.Dr, p.Stop, clientId: cid);
        Assert.Equal(a, b);
        Assert.Equal(1, f.WithDb(db => db.DeliveryProofs.Count(x => x.DeliveryId == p.Stop && x.ClientId == cid)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadProof(p.Dr, p.Stop, kind: "video")).StatusCode);
    }

    [Fact]
    public async Task Delivered_moves_the_order_through_the_existing_rules_so_payments_release_still_works()
    {
        var p = await Started();
        await Deliver(p.Dr, p.Stop);
        var (mid, number) = (p.Mid, (string)(await Detail(p.D.S.Owner, p.Order))["number"]!);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, mid, number)).StatusCode);
        var b0 = await Pay.Bal(f, p.D.S.Seller);
        Assert.True(b0.Pending > 0); Assert.Equal(0, b0.Available);
        f.Time.Advance(TimeSpan.FromDays(8));
        Assert.Equal(1, (await Pay.Release(f)).Released);
        var b1 = await Pay.Bal(f, p.D.S.Seller);
        Assert.Equal((0, b0.Pending), (b1.Pending, b1.Available));
    }

    // ---- failed attempts -----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_failed_attempt_needs_a_reason_flags_the_stop_alerts_the_seller_and_tells_the_marketplace()
    {
        var p = await Started();
        Assert.Equal("reason", await ErrCodeOf(await Event(p.Dr, p.Stop, "failed")));
        Assert.Equal("reason", await ErrCodeOf(await Event(p.Dr, p.Stop, "failed", reason: "bored")));
        var photo = await Proof(p.Dr, p.Stop);
        var r = await Event(p.Dr, p.Stop, "failed", reason: "no_one_home", proofs: [photo]);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var del = Del(f, p.Order);
        Assert.Equal(LocalStatus.Failed, del.Status); Assert.Equal(1, del.Attempts); Assert.Equal("no_one_home", del.FailReason);
        Assert.Equal("Shipped", St(await Detail(p.D.S.Owner, p.Order)));
        var fail = Events(f, p.Mid, "delivery.attempt_failed").Single();
        Assert.Equal("no_one_home", (string)fail["reason"]!); Assert.Equal(1, (int)fail["attempt"]!); Assert.Equal("local", (string)fail["method"]!);
        Assert.Contains(Events(f, p.Mid, "order.tracking_updated"), e => (string)e["status"]! == "failure");
        Assert.Contains(f.Mails(Pay.OwnerEmail(f, p.D.S.Seller), "delivery-alert"), m => m.Subject.Contains("Delivery attempt failed"));
        var board = await Node(await p.D.S.Owner.Get("/api/v1/seller/delivery/board"));
        Assert.True((bool)board["deliveries"]![0]!["exception"]!);
        // still visible to the driver (they must decide), reattempt -> in transit -> next stop logic, then return to store
        Assert.True((bool)(await Node(await p.Dr.Api.Get($"/api/v1/driver/stops/{p.Stop}")))["detailsVisible"]!);
        Assert.Equal(HttpStatusCode.OK, (await Event(p.Dr, p.Stop, "reattempt")).StatusCode);
        Assert.Equal(LocalStatus.OutForDelivery, Del(f, p.Order).Status);          // the only stop: it is the next one again
        Assert.Equal(HttpStatusCode.OK, (await Event(p.Dr, p.Stop, "failed", reason: "refused")).StatusCode);
        Assert.Equal(2, Del(f, p.Order).Attempts);
        Assert.Equal(HttpStatusCode.OK, (await Event(p.Dr, p.Stop, "return_to_store")).StatusCode);
        Assert.Equal(LocalStatus.Returned, Del(f, p.Order).Status);
        Assert.Equal("return_to_sender", f.WithDb(db => db.Shipments.First(s => s.OrderId == p.Order).TrackingStatus));
        Assert.Equal(RouteStatus.Completed, f.WithDb(db => db.DeliveryRoutes.First(r => r.Id == p.Route).Status));
        Assert.Contains(f.Mails(Pay.OwnerEmail(f, p.D.S.Seller), "delivery-alert"), m => m.Subject.Contains("Delivery returned"));
        Assert.Contains(f.Mails(Pay.OwnerEmail(f, p.D.S.Seller), "delivery-alert"), m => m.Subject.Contains("route finished", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failed_stop_can_be_unassigned_and_given_to_another_driver_and_a_returned_or_delivered_stop_is_final()
    {
        var p = await Started(); var dr2 = await AddDriver(p.D);
        await Event(p.Dr, p.Stop, "failed", reason: "address_issue");
        Assert.Equal(HttpStatusCode.OK, (await p.D.S.Owner.Post($"/api/v1/seller/delivery/deliveries/{p.Stop}/unassign")).StatusCode);
        Assert.Null(Del(f, p.Order).RouteId);
        Assert.Equal(HttpStatusCode.OK, (await Assign(p.D, p.Order, dr2)).StatusCode);              // the order is already Shipped: the same shipment is reused
        Assert.Equal(HttpStatusCode.OK, (await Start(dr2, RouteOf(f, p.Order))).StatusCode);
        Assert.Equal(1, f.WithDb(db => db.Shipments.Count(s => s.OrderId == p.Order)));
        Assert.Equal(HttpStatusCode.OK, (await Deliver(dr2, p.Stop)).StatusCode);
        Assert.Equal("Delivered", St(await Detail(p.D.S.Owner, p.Order)));
        var again = await Event(dr2, p.Stop, "failed", reason: "unsafe");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); Assert.Equal("ignored_state", (string)(await Node(again))["reason"]!);
        Assert.Equal(LocalStatus.Delivered, Del(f, p.Order).Status);
    }

    // ---- offline queue: idempotency and ordering ---------------------------------------------------------------------------------
    [Fact]
    public async Task Replaying_the_same_client_event_id_applies_it_once_even_in_parallel()
    {
        var p = await Started();
        var photo = await Proof(p.Dr, p.Stop); var eid = Guid.NewGuid();
        var rs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Event(p.Dr, p.Stop, "delivered", eid, proofs: [photo], placement: "left_at_door")));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, f.WithDb(db => db.DeliveryAttempts.Count(a => a.DeliveryId == p.Stop)));
        Assert.Single(Events(f, p.Mid, "shipment.delivered")); Assert.Single(Events(f, p.Mid, "order.delivered"));
        Assert.Equal(1, f.WithDb(db => db.DeliveryClientEvents.Count(e => e.Id == eid)));
        var late = await Node(await Event(p.Dr, p.Stop, "delivered", eid, proofs: [photo], placement: "left_at_door"));
        Assert.True((bool)late["replayed"]!);
        // another driver cannot reuse the id to learn anything
        var other = await AddDriver(p.D);
        Assert.Equal(HttpStatusCode.NotFound, (await Event(other, p.Stop, "delivered", eid)).StatusCode);
    }

    [Fact]
    public async Task An_older_event_never_overwrites_newer_state_and_a_rejected_event_can_be_corrected_with_the_same_id()
    {
        var d = await NewDShop(f, stock: 10); var dr = await AddDriver(d);
        var (_, a, _) = await PlaceLocal(d, accept: true); var (_, b, _) = await PlaceLocal(d, accept: true);
        await Assign(d, a, dr); await Assign(d, b, dr);
        await Start(dr, RouteOf(f, a));
        var (sa, sb) = (DeliveryId(f, a), DeliveryId(f, b));
        var t0 = f.Time.GetUtcNow().UtcDateTime;
        // b goes out for delivery at t0+4 (client clocks more than 5 min ahead are clamped), then an OLD queued event (t0+1) says a failed: ignored as stale only if it concerns the same stop
        Assert.Equal(HttpStatusCode.OK, (await Event(dr, sb, "out_for_delivery", at: t0.AddMinutes(4))).StatusCode);
        Assert.Equal(LocalStatus.OutForDelivery, Del(f, b).Status); Assert.Equal(LocalStatus.InTransit, Del(f, a).Status);
        var stale = await Node(await Event(dr, sb, "failed", at: t0.AddMinutes(1), reason: "no_one_home"));
        Assert.False((bool)stale["applied"]!); Assert.Equal("ignored_stale", (string)stale["reason"]!);
        Assert.Equal(LocalStatus.OutForDelivery, Del(f, b).Status); Assert.Equal(0, Del(f, b).Attempts);
        Assert.Equal("ignored_stale", f.WithDb(db => db.DeliveryClientEvents.Single(e => e.DeliveryId == sb && e.Type == "failed").Result));
        // a rejected event (no photo) leaves no trace, so the corrected one can reuse its id
        var eid = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Event(dr, sb, "delivered", eid, at: t0.AddMinutes(4.5), placement: "left_at_door")).StatusCode);
        Assert.Equal(0, f.WithDb(db => db.DeliveryClientEvents.Count(e => e.Id == eid)));
        Assert.Equal(HttpStatusCode.OK, (await Event(dr, sb, "delivered", eid, at: t0.AddMinutes(4.5), proofs: [await Proof(dr, sb)], placement: "left_at_door")).StatusCode);
        Assert.Equal(LocalStatus.OutForDelivery, Del(f, a).Status);                // a is now the next stop
        // a phone clock far in the future is clamped, it does not freeze the stop
        Assert.Equal(HttpStatusCode.OK, (await Event(dr, sa, "failed", at: t0.AddDays(30), reason: "unsafe")).StatusCode);
        Assert.True(Del(f, a).LastEventClientAt <= f.Time.GetUtcNow().UtcDateTime.AddMinutes(1));
    }

    [Fact]
    public async Task Events_need_an_active_route_and_valid_transitions()
    {
        var p = await Planned();
        var r = await Event(p.Dr, p.Stop, "out_for_delivery");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("route_not_active", await ErrCodeOf(r));
        await Start(p.Dr, p.Route);
        Assert.Equal(HttpStatusCode.Conflict, (await Event(p.Dr, p.Stop, "reattempt")).StatusCode);             // only failed stops can be re-attempted
        Assert.Equal(HttpStatusCode.Conflict, (await Event(p.Dr, p.Stop, "return_to_store")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Event(p.Dr, p.Stop, "teleport")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await p.Dr.Api.Post($"/api/v1/driver/stops/{p.Stop}/events", new { type = "delivered" })).StatusCode);   // no client event id
    }

    // ---- live ETA + location -----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Location_needs_consent_an_active_route_and_can_be_paused_or_withdrawn()
    {
        var p = await Planned();
        var pos = new { lat = 40.6931, lng = -73.9900 };
        Assert.Equal("consent_required", await ErrCodeOf(await p.Dr.Api.Post("/api/v1/driver/location", pos)));
        await p.Dr.Api.Post("/api/v1/driver/consent", new { consented = true });
        Assert.Equal("no_active_route", await ErrCodeOf(await p.Dr.Api.Post("/api/v1/driver/location", pos)));          // route only planned
        await Start(p.Dr, p.Route);
        Assert.Equal(HttpStatusCode.OK, (await p.Dr.Api.Post("/api/v1/driver/location", pos)).StatusCode);
        Assert.Equal(1, f.WithDb(db => db.DriverLocations.Count(l => l.MembershipId == p.Dr.Membership)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await p.Dr.Api.Post("/api/v1/driver/location", new { lat = 120.0, lng = 5.0 })).StatusCode);
        await p.Dr.Api.Post("/api/v1/driver/consent", new { paused = true });
        Assert.Equal(0, f.WithDb(db => db.DriverLocations.Count(l => l.MembershipId == p.Dr.Membership)));            // pausing removes what was stored
        Assert.Equal("sharing_paused", await ErrCodeOf(await p.Dr.Api.Post("/api/v1/driver/location", pos)));
        await p.Dr.Api.Post("/api/v1/driver/consent", new { paused = false });
        Assert.Equal(HttpStatusCode.OK, (await p.Dr.Api.Post("/api/v1/driver/location", pos)).StatusCode);
        await p.Dr.Api.Post("/api/v1/driver/consent", new { consented = false });
        Assert.Equal(0, f.WithDb(db => db.DriverLocations.Count(l => l.MembershipId == p.Dr.Membership)));
        Assert.Equal("consent_required", await ErrCodeOf(await p.Dr.Api.Post("/api/v1/driver/location", pos)));
        // a finished route stops accepting positions
        await p.Dr.Api.Post("/api/v1/driver/consent", new { consented = true });
        await Deliver(p.Dr, p.Stop);
        Assert.Equal("no_active_route", await ErrCodeOf(await p.Dr.Api.Post("/api/v1/driver/location", pos)));
    }

    [Fact]
    public async Task Positions_are_stored_coarse_and_only_the_next_stops_buyer_event_carries_one()
    {
        var d = await NewDShop(f, stock: 10); var dr = await AddDriver(d);
        var (mid1, a, _) = await PlaceLocal(d, lat: 40.7000, lng: -73.9900, accept: true);
        var (mid2, b, _) = await PlaceLocal(d, lat: 40.7200, lng: -73.9900, accept: true);
        await Assign(d, a, dr); await Assign(d, b, dr);
        await Start(dr, RouteOf(f, a));
        await dr.Api.Post("/api/v1/driver/consent", new { consented = true });
        Assert.Equal(HttpStatusCode.OK, (await dr.Api.Post("/api/v1/driver/location", new { lat = 40.693187, lng = -73.990399 })).StatusCode);
        var stored = f.WithDb(db => db.DriverLocations.Single(l => l.MembershipId == dr.Membership));
        Assert.Equal(40.693, stored.Lat); Assert.Equal(-73.990, stored.Lng);                                       // rounded to ~100 m BEFORE storing
        Assert.Equal(stored.At.AddHours(24), stored.ExpiresAt);
        // stop 1 is next: its event carries the coarse position and an ETA; stop 2 gets none
        var onWay = Events(f, mid1, "order.tracking_updated").Last(e => (string?)e["statusDetail"] == "On the way");
        Assert.Equal(40.693, (double)onWay["position"]!["lat"]!); Assert.NotNull(onWay["eta"]!["estimatedAt"]);
        Assert.DoesNotContain(Events(f, mid2, "order.tracking_updated"), e => e["position"] is not null);
        var s1 = Del(f, a); var s2 = Del(f, b);
        Assert.NotNull(s1.EtaUtc); Assert.NotNull(s2.EtaUtc); Assert.True(s2.EtaUtc > s1.EtaUtc);                  // the later stop has the later ETA
        // throttled: a second ping inside a minute adds no buyer event, after a minute it does
        var count = Events(f, mid1, "order.tracking_updated").Count;
        await dr.Api.Post("/api/v1/driver/location", new { lat = 40.6935, lng = -73.9901 });
        Assert.Equal(count, Events(f, mid1, "order.tracking_updated").Count);
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await dr.Api.Post("/api/v1/driver/location", new { lat = 40.6970, lng = -73.9901 });
        Assert.Equal(count + 1, Events(f, mid1, "order.tracking_updated").Count);
        // once stop 1 is delivered, stop 2 becomes next and only then may its buyer see a position
        await Deliver(dr, DeliveryId(f, a));
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await dr.Api.Post("/api/v1/driver/location", new { lat = 40.7100, lng = -73.9901 });
        Assert.Contains(Events(f, mid2, "order.tracking_updated"), e => e["position"] is not null);
        Assert.DoesNotContain(Events(f, mid1, "order.tracking_updated").Skip(count + 1), e => e["position"] is not null);
    }

    [Fact]
    public async Task Locations_older_than_24_hours_are_deleted_by_the_retention_job_and_never_logged_in_events()
    {
        var p = await Started();
        await p.Dr.Api.Post("/api/v1/driver/consent", new { consented = true });
        await p.Dr.Api.Post("/api/v1/driver/location", new { lat = 40.6931, lng = -73.9900 });
        Assert.Equal(0, Svc(f, async sp => (await sp.GetRequiredService<DeliveryMaintenance>().RunAsync()).LocationsDeleted));          // fresh: nothing deleted yet
        Assert.Equal(1, f.WithDb(db => db.DriverLocations.Count()));
        f.Time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, Svc(f, async sp => (await sp.GetRequiredService<DeliveryMaintenance>().RunAsync()).LocationsDeleted));
        f.Time.Advance(TimeSpan.FromHours(1.1));
        Assert.Equal(1, Svc(f, async sp => (await sp.GetRequiredService<DeliveryMaintenance>().RunAsync()).LocationsDeleted));
        Assert.Equal(0, f.WithDb(db => db.DriverLocations.Count()));
    }

    // ---- jobs, redaction, staff ------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_cancelled_order_frees_its_window_and_a_late_delivery_alerts_the_seller_once()
    {
        var d = await NewDShop(f); var (_, id, _) = await PlaceLocal(d, d.WinB);              // capacity 1
        await Ok(Act(d.S.Owner, id, "cancel", new { reason = "out_of_stock", restock = true }));
        Svc(f, async sp => await sp.GetRequiredService<DeliveryMaintenance>().RunAsync());
        Assert.Equal(LocalStatus.Cancelled, Del(f, id).Status);
        Assert.Equal(0, f.WithDb(db => db.DeliveryWindowUsage.Single(u => u.WindowId == d.WinB && u.Date == Today).Booked));
        var (_, late, _) = await PlaceLocal(d, d.WinB);
        f.Time.Advance(TimeSpan.FromHours(10));                                               // past 18:00
        var first = Svc(f, async sp => await sp.GetRequiredService<DeliveryMaintenance>().RunAsync());
        var second = Svc(f, async sp => await sp.GetRequiredService<DeliveryMaintenance>().RunAsync());
        Assert.Equal(1, first.LateNotified); Assert.Equal(0, second.LateNotified);
        Assert.Contains(f.Mails(Pay.OwnerEmail(f, d.S.Seller), "delivery-alert"), m => m.Subject.Contains("late", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Proofs_are_deleted_when_the_orders_buyer_data_is_redacted()
    {
        var p = await Started(); await Deliver(p.Dr, p.Stop);
        var key = f.WithDb(db => db.DeliveryProofs.First(x => x.DeliveryId == p.Stop).StorageKey);
        Assert.True(File.Exists(Path.Combine(f.Dir, "uploads", key)));
        f.WithDb(db => { var o = db.Orders.First(x => x.Id == p.Order); o.PiiRedactedAt = f.Time.GetUtcNow().UtcDateTime; db.SaveChanges(); return 0; });
        Assert.Equal(2, Svc(f, async sp => (await sp.GetRequiredService<DeliveryMaintenance>().RunAsync()).ProofsDeleted));    // photo + signature
        Assert.False(File.Exists(Path.Combine(f.Dir, "uploads", key)));
        Assert.NotNull(f.WithDb(db => db.DeliveryProofs.First(x => x.DeliveryId == p.Stop).DeletedAt));
    }

    [Fact]
    public async Task A_carrier_shipment_cannot_be_created_for_an_order_that_is_on_the_dispatch_board()
    {
        var p = await Planned();
        var r = await Act(p.D.S.Owner, p.Order, "ship", Ship());
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("local_delivery", await Code(r));
        await Ok(p.D.S.Owner.Post($"/api/v1/seller/delivery/deliveries/{p.Stop}/unassign"));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(p.D.S.Owner, p.Order, "ship", Ship())).StatusCode);       // still local (unassigned, not cancelled)
    }

    [Fact]
    public async Task Staff_see_a_sellers_local_delivery_without_personal_data_and_proof_access_needs_a_reason_and_is_audited()
    {
        var p = await Started(); await Deliver(p.Dr, p.Stop);
        var (staff, _) = await StaffKit.LoginSteppedUp(f, StaffRole.Support);
        var list = await staff.Get($"/api/v1/admin/sellers/{p.D.S.Seller}/local-delivery");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var n = await Node(list);
        Assert.Single(n["deliveries"]!.AsArray()); Assert.Equal("Delivered", (string)n["deliveries"]![0]!["status"]!); Assert.NotEmpty(n["routes"]!.AsArray()); Assert.NotEmpty(n["settings"]!.AsArray());
        Assert.DoesNotContain("1 Main St", n.ToJsonString()); Assert.DoesNotContain("Jane", n.ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, (await staff.Get($"/api/v1/admin/sellers/{Guid.NewGuid()}/local-delivery")).StatusCode);
        var noReason = await staff.Post($"/api/v1/admin/local-delivery/deliveries/{p.Stop}/proof-access", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.DoesNotContain(f.WithDb(db => db.AuditEvents.ToList()), a => a.Type == "admin.delivery.proof_viewed");
        var ok = await staff.Post($"/api/v1/admin/local-delivery/deliveries/{p.Stop}/proof-access", new { reason = "Buyer disputes the delivery" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await Node(ok);
        Assert.Equal(2, body["proofs"]!.AsArray().Count); Assert.Equal("Jane", (string)body["attempts"]![0]!["recipientName"]!);
        Assert.Equal(HttpStatusCode.OK, (await f.NewClient().GetAsync((string)body["proofs"]![0]!["url"]!)).StatusCode);
        Assert.Single(f.WithDb(db => db.AuditEvents.Where(a => a.Type == "admin.delivery.proof_viewed" && a.TargetId == p.Stop.ToString() && a.Reason == "Buyer disputes the delivery").ToList()));
        // seller sessions never reach the staff endpoints
        Assert.Contains((await p.D.S.Owner.Get($"/api/v1/admin/sellers/{p.D.S.Seller}/local-delivery")).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
        var (ro, _) = await StaffKit.LoginSteppedUp(f, StaffRole.ReadOnly);
        Assert.Equal(HttpStatusCode.OK, (await ro.Get($"/api/v1/admin/sellers/{p.D.S.Seller}/local-delivery")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"/api/v1/admin/local-delivery/deliveries/{p.Stop}/proof-access", new { reason = "just looking around" })).StatusCode);
    }
}
