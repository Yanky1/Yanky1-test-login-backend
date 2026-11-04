using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Shipping;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Tests;

public class ShippingTrackingTests
{
    private const string Base = "/api/v1/seller/shipping";

    private static async Task<(Shop S, Guid Order, string Mid, JsonNode Label)> Bought(ShipFactory f, int qty = 1)
    {
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s, qty);
        return (s, id, mid, await Ship.BuyCheapest(s.Owner, id, pkg));
    }

    // ---- registration ------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Tracking_is_registered_for_label_manual_and_csv_shipments_and_skipped_for_unsupported_carriers()
    {
        using var f = new ShipFactory();
        var (s, id, _, label) = await Bought(f);
        Assert.Contains(Ship.Tn(label), f.Provider.Registered);
        Assert.NotNull(Ship.Shipments(f, id).Single().TrackingRegisteredAt);
        // manual entry
        var (_, m, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, m, "ship", Orders.Ship(tracking: "1Z999AA10123456784")));
        Assert.Contains("1Z999AA10123456784", f.Provider.Registered);
        // CSV import
        var (_, c, number) = await Ship.Accepted(s);
        var csv = $"order_number,carrier,tracking_number,ship_date,sku,quantity\r\n{number},UPS,1Z999AA10123456785,,,\r\n";
        var form = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "t.csv" } };
        var imp = await s.Owner.Send(new HttpRequestMessage(HttpMethod.Post, "/api/v1/seller/orders/import-tracking?apply=true") { Content = form });
        Assert.True(imp.IsSuccessStatusCode, await imp.Content.ReadAsStringAsync());
        Assert.Contains("1Z999AA10123456785", f.Provider.Registered);
        Assert.NotNull(Ship.Shipments(f, c).Single().TrackingRegisteredAt);
        // "Other" carriers can't be tracked: shipped fine, never registered
        var (_, o, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, o, "ship", Orders.Ship(carrier: "Other", tracking: "LOCAL-12345")));
        Assert.DoesNotContain("LOCAL-12345", f.Provider.Registered);
        Assert.Null(Ship.Shipments(f, o).Single().TrackingRegisteredAt);
    }

    [Fact]
    public async Task A_failed_registration_never_blocks_shipping_and_is_retried_by_the_maintenance_job()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        f.Provider.FailRegistration = true;
        var (_, id, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(tracking: "1Z999AA10123456790")));
        Assert.Equal("Shipped", Orders.St(await Orders.Detail(s.Owner, id)));
        Assert.Null(Ship.Shipments(f, id).Single().TrackingRegisteredAt);
        f.Provider.FailRegistration = false;
        Assert.Equal(1, (await Ship.Maintenance(f)).Registered);
        Assert.NotNull(Ship.Shipments(f, id).Single().TrackingRegisteredAt);
    }

    [Fact]
    public async Task Editing_the_tracking_number_resets_tracking_and_registers_the_new_number()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        var d = await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(tracking: "1Z999AA10123456791")));
        await Ship.Track(f, "1Z999AA10123456791", "in_transit");
        var sid = (string)(await Orders.Detail(s.Owner, id))["shipments"]![0]!["id"]!;
        Assert.NotNull(d);
        var r = await s.Owner.Put($"/api/v1/seller/orders/{id}/shipments/{sid}", new { trackingNumber = "1Z999AA10123456792", carrier = "UPS" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Contains("1Z999AA10123456792", f.Provider.Registered);
        Assert.Null(Ship.Shipments(f, id).Single().TrackingStatus);
    }

    // ---- webhook authentication ------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Webhooks_need_a_valid_fresh_signature_or_the_secret_token()
    {
        using var f = new ShipFactory();
        var (_, id, _, label) = await Bought(f);
        var tn = Ship.Tn(label);
        var body = Ship.TrackBody(tn, "in_transit", f.Time.GetUtcNow().UtcDateTime);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ship.Hook(f, body, sign: false)).StatusCode);                              // unsigned
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ship.Hook(f, body, secret: "not-the-secret-not-the-secret-123")).StatusCode);   // wrong key
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ship.Hook(f, body, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 3600)).StatusCode);   // stale (replayed capture)
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ship.Hook(f, body, sign: false, token: "wrong")).StatusCode);
        Assert.Null(Ship.Shipments(f, id).Single().TrackingStatus);                                                                   // nothing was applied
        var tampered = await Ship.Hook(f, body);                                                                                      // signature of the ORIGINAL body…
        Assert.Equal(HttpStatusCode.OK, tampered.StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/shipping/webhooks/shippo") { Content = new StringContent(body.Replace("in_transit", "delivered"), System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add(ShippingWebhookService.SignatureHeader, ShippingWebhookService.Sign(ShipFactory.WebhookSecret, f.Time.GetUtcNow().ToUnixTimeSeconds(), body));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(req)).StatusCode);                                   // …does not cover a changed body
        Assert.Equal(HttpStatusCode.OK, (await Ship.Hook(f, Ship.TrackBody(tn, "out_for_delivery", f.Time.GetUtcNow().UtcDateTime.AddMinutes(5)), sign: false, token: ShipFactory.WebhookSecret)).StatusCode);
        Assert.Equal("out_for_delivery", Ship.Shipments(f, id).Single().TrackingStatus);
        Assert.True(f.WithDb(db => db.ShippingWebhookEvents.Count(w => w.Outcome == "bad_signature")) >= 4);
    }

    [Fact]
    public async Task A_replayed_event_is_applied_once_and_leaves_one_history_entry()
    {
        using var f = new ShipFactory();
        var (_, id, mid, label) = await Bought(f);
        var at = f.Time.GetUtcNow().UtcDateTime;
        Assert.Equal("applied", await Ship.Track(f, Ship.Tn(label), "in_transit", at, eventId: "evt-1"));
        Assert.Equal("duplicate", await Ship.Track(f, Ship.Tn(label), "in_transit", at, eventId: "evt-1"));
        Assert.Equal("duplicate", await Ship.Track(f, Ship.Tn(label), "in_transit", at, eventId: "evt-2"));    // same fact under a new event id: history dedupes it
        var sid = Ship.Shipments(f, id).Single().Id;
        Assert.Equal(1, f.WithDb(db => db.TrackingEvents.Count(e => e.ShipmentId == sid && e.Status == "in_transit")));
        Assert.Equal(1, Ship.Outbox(f, mid).Count(x => x.Type == "order.tracking_updated"));
    }

    [Fact]
    public async Task Out_of_order_updates_never_let_an_older_status_overwrite_a_newer_one()
    {
        using var f = new ShipFactory();
        var (s, id, _, label) = await Bought(f);
        var t0 = f.Time.GetUtcNow().UtcDateTime;
        await Ship.Track(f, Ship.Tn(label), "out_for_delivery", t0.AddHours(5));
        Assert.Equal("stale", await Ship.Track(f, Ship.Tn(label), "in_transit", t0.AddHours(2)));      // arrives late
        Assert.Equal("stale", await Ship.Track(f, Ship.Tn(label), "pre_transit", t0));
        var sh = Ship.Shipments(f, id).Single();
        Assert.Equal("out_for_delivery", sh.TrackingStatus);
        var sid = sh.Id;
        Assert.Equal(3, f.WithDb(db => db.TrackingEvents.Count(e => e.ShipmentId == sid)));              // …but the history keeps all three
        await Ship.Track(f, Ship.Tn(label), "delivered", t0.AddHours(8));
        Assert.Equal("stale", await Ship.Track(f, Ship.Tn(label), "in_transit", t0.AddHours(9)));       // nothing follows a delivery
        Assert.Equal("delivered", Ship.Shipments(f, id).Single().TrackingStatus);
        // an equal timestamp resolves by stage: delivered beats in_transit
        var (s2, id2, _, l2) = await Bought(f);
        var same = f.Time.GetUtcNow().UtcDateTime.AddHours(1);
        await Ship.Track(f, Ship.Tn(l2), "in_transit", same); await Ship.Track(f, Ship.Tn(l2), "out_for_delivery", same);
        Assert.Equal("out_for_delivery", Ship.Shipments(f, id2).Single().TrackingStatus);
        Assert.NotNull(s); Assert.NotNull(s2);
    }

    // ---- delivered ------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Delivered_moves_the_shipment_and_order_to_delivered_and_payments_release_still_works()
    {
        using var f = new ShipFactory();
        var (s, id, mid, label) = await Bought(f);
        var cap = await Pay.Capture(f, mid, null);
        Assert.Equal(HttpStatusCode.OK, cap.StatusCode);
        Assert.Equal(0, (await Pay.Release(f)).Released);                                   // not delivered yet
        var when = f.Time.GetUtcNow().UtcDateTime.AddDays(1);                               // a carrier time in the future is clamped to now (see the review tests)
        Assert.Equal("applied", await Ship.Track(f, Ship.Tn(label), "delivered", when));
        var o = Ship.Order(f, id);
        Assert.Equal(OrderStatus.Delivered, o.Status); Assert.Equal(f.Time.GetUtcNow().UtcDateTime, o.DeliveredAt!.Value, TimeSpan.FromSeconds(1));
        var sh = Ship.Shipments(f, id).Single();
        Assert.NotNull(sh.DeliveredAt); Assert.NotNull(sh.TrackingStoppedAt);
        var events = Ship.Outbox(f, mid);
        var del = events.Single(x => x.Type == "shipment.delivered").Data;
        Assert.Equal(Ship.Tn(label), (string)del["trackingNumber"]!); Assert.True((bool)del["orderDelivered"]!);
        Assert.Contains(events, x => x.Type == "order.delivered");
        Assert.Contains((await Orders.Detail(s.Owner, id))["timeline"]!.AsArray(), e => (string)e!["type"]! == "order.delivered");
        // the return window and release days apply exactly as for a manually delivered order
        Pay.Age(f, 8);
        var rel = await Pay.Release(f);
        Assert.Equal(1, rel.Released);
        var bal = await Pay.Bal(f, s.Seller);
        Assert.True(bal.Available > 0);
    }

    [Fact]
    public async Task With_partial_shipments_the_order_is_delivered_only_when_every_shipment_arrived()
    {
        using var f = new ShipFactory();
        var (s, _, pkg) = await Ship.NewShop(f);
        var (mid, id, _) = await Ship.Accepted(s, qty: 2);
        var line = await Orders.LineId(s.Owner, id);
        var l1 = (await Orders.Node(await Ship.Buy(s.Owner, id, await Ship.Quote(s.Owner, id, pkg, new { lines = new[] { new { lineId = line, quantity = 1 } } }))))["label"]!;
        Assert.Equal("PartiallyShipped", Orders.St(await Orders.Detail(s.Owner, id)));
        await Ship.Track(f, Ship.Tn(l1), "delivered");
        Assert.Equal("PartiallyShipped", Orders.St(await Orders.Detail(s.Owner, id)));          // one shipment arrived, the other isn't even sent
        var l2 = await Ship.BuyCheapest(s.Owner, id, pkg);
        Assert.Equal("Shipped", Orders.St(await Orders.Detail(s.Owner, id)));
        await Ship.Track(f, Ship.Tn(l2), "in_transit");
        Assert.Equal("Shipped", Orders.St(await Orders.Detail(s.Owner, id)));
        await Ship.Track(f, Ship.Tn(l2), "delivered", f.Time.GetUtcNow().UtcDateTime.AddHours(2));
        Assert.Equal("Delivered", Orders.St(await Orders.Detail(s.Owner, id)));
        Assert.Equal(2, Ship.Outbox(f, mid).Count(x => x.Type == "shipment.delivered"));
        Assert.Single(Ship.Outbox(f, mid), x => x.Type == "order.delivered");
    }

    [Fact]
    public async Task Manual_shipments_are_tracked_too_and_their_delivery_moves_the_order()
    {
        using var f = new ShipFactory();
        var s = await Orders.NewShop(f);
        var (_, id, _) = await Ship.Accepted(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(carrier: "UPS", tracking: "1Z999AA10123456793")));
        Assert.Equal("applied", await Ship.Track(f, "1Z999AA10123456793", "delivered"));
        Assert.Equal("Delivered", Orders.St(await Orders.Detail(s.Owner, id)));
        Assert.Equal("unknown_shipment", await Ship.Track(f, "1Z999AA10000000000", "delivered"));         // not ours: acknowledged, ignored
    }

    // ---- exceptions ----------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_exception_flags_the_order_notifies_the_owner_and_opted_in_members_and_shows_on_the_dashboard()
    {
        using var f = new ShipFactory();
        var (s, id, mid, label) = await Bought(f);
        var owner = Ship.OwnerEmail(f, s.Seller);
        var (admin, adminEmail) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        var (mgr, mgrEmail) = await TeamHelpers.JoinAs(f, s.Owner, "Manager");
        var optIn = await mgr.Put("/api/v1/seller/notification-preferences", new { shippingEmails = true });
        Assert.True(optIn.IsSuccessStatusCode, await optIn.Content.ReadAsStringAsync());
        await Ship.Track(f, Ship.Tn(label), "in_transit", f.Time.GetUtcNow().UtcDateTime.AddHours(1));
        await Ship.Track(f, Ship.Tn(label), "failure", f.Time.GetUtcNow().UtcDateTime.AddHours(2), detail: "Address not found");
        var o = Ship.Order(f, id);
        Assert.True(o.NeedsAttention); Assert.Contains("Address not found", o.AttentionReason);
        Assert.Equal(OrderStatus.Shipped, o.Status);
        Assert.Single(f.Mails(owner, "shipping-exception"));
        Assert.Single(f.Mails(mgrEmail, "shipping-exception"));
        Assert.Empty(f.Mails(adminEmail, "shipping-exception"));                                      // not opted in
        var upd = Ship.Outbox(f, mid).Last(x => x.Type == "order.tracking_updated").Data;
        Assert.Equal("failure", (string)upd["status"]!); Assert.Equal("Address not found", (string)upd["statusDetail"]!);
        Assert.Equal(1, (int)(await Orders.Node(await s.Owner.Get("/api/v1/seller/orders/dashboard")))["shipping"]!["exceptions"]!);
        Assert.Equal(1, (int)(await Orders.Node(await s.Owner.Get("/api/v1/seller/orders/counts")))["exceptions"]!);
        var list = await Orders.Node(await s.Owner.Get("/api/v1/seller/orders?tracking=exception"));
        Assert.Single(list["items"]!.AsArray()); Assert.Equal("exception", (string)list["items"]![0]!["trackingStatus"]!); Assert.True((bool)list["items"]![0]!["needsAttention"]!);
        Assert.Empty((await Orders.Node(await s.Owner.Get("/api/v1/seller/orders?tracking=delivered")))["items"]!.AsArray());
        Assert.Empty((await Orders.Node(await s.Owner.Get("/api/v1/seller/orders?tracking=in_transit")))["items"]!.AsArray());   // the parcel is a problem now, not "in transit"
        // a second failure update does not email again
        await Ship.Track(f, Ship.Tn(label), "return_to_sender", f.Time.GetUtcNow().UtcDateTime.AddHours(3));
        Assert.Single(f.Mails(owner, "shipping-exception"));
        // the carrier recovers: the flag clears by itself
        await Ship.Track(f, Ship.Tn(label), "in_transit", f.Time.GetUtcNow().UtcDateTime.AddHours(4));
        Assert.False(Ship.Order(f, id).NeedsAttention);
        Assert.NotNull(admin);
    }

    [Fact]
    public async Task The_seller_can_dismiss_an_exception_and_a_viewer_cannot()
    {
        using var f = new ShipFactory();
        var (s, id, _, label) = await Bought(f);
        await Ship.Track(f, Ship.Tn(label), "failure", detail: "Damaged");
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Base}/orders/{id}/attention/dismiss")).StatusCode);
        Assert.True(Ship.Order(f, id).NeedsAttention);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"{Base}/orders/{id}/attention/dismiss")).StatusCode);
        Assert.False(Ship.Order(f, id).NeedsAttention);
    }

    [Fact]
    public async Task The_order_tracking_timeline_lists_every_event_in_order()
    {
        using var f = new ShipFactory();
        var (s, id, _, label) = await Bought(f);
        var t = f.Time.GetUtcNow().UtcDateTime;
        await Ship.Track(f, Ship.Tn(label), "in_transit", t.AddHours(1)); await Ship.Track(f, Ship.Tn(label), "out_for_delivery", t.AddHours(5)); await Ship.Track(f, Ship.Tn(label), "delivered", t.AddHours(7));
        var tl = await Orders.Node(await s.Owner.Get($"{Base}/orders/{id}/tracking"));
        var sh = tl["shipments"]![0]!;
        Assert.Equal("delivered", (string)sh["status"]!); Assert.Equal("Delivered", (string)sh["statusLabel"]!);
        Assert.Equal(["in_transit", "out_for_delivery", "delivered"], sh["events"]!.AsArray().Select(e => (string)e!["status"]!).ToArray());
        Assert.Single(tl["labels"]!.AsArray());
    }

    // ---- reconciler ----------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_reconciler_polls_only_shipments_silent_for_24_hours_and_applies_what_it_finds()
    {
        using var f = new ShipFactory();
        var (s, id, _, label) = await Bought(f);
        Assert.Equal(0, (await Ship.Maintenance(f)).Polled);                                          // fresh: no poll
        Assert.Equal(0, f.Provider.TrackingPolls);
        f.Time.Advance(TimeSpan.FromHours(25));
        f.Provider.Script("USPS", Ship.Tn(label), "delivered", f.Time.GetUtcNow().UtcDateTime.AddHours(-2));
        var m = await Ship.Maintenance(f);
        Assert.Equal(1, m.Polled); Assert.Equal(1, f.Provider.TrackingPolls);
        Assert.Equal(OrderStatus.Delivered, Ship.Order(f, id).Status);
        Assert.Equal("poll", f.WithDb(db => db.TrackingEvents.OrderBy(e => e.RecordedAt).Last().Source));
        Assert.Equal(0, (await Ship.Maintenance(f)).Polled);                                          // delivered: tracking stopped
        Assert.NotNull(s);
    }

    [Fact]
    public async Task A_webhook_counts_as_word_from_the_carrier_so_the_next_poll_waits_another_day()
    {
        using var f = new ShipFactory();
        var (_, _, _, label) = await Bought(f);
        f.Time.Advance(TimeSpan.FromHours(20));
        await Ship.Track(f, Ship.Tn(label), "in_transit", f.Time.GetUtcNow().UtcDateTime);
        f.Time.Advance(TimeSpan.FromHours(10));                                                       // 30 h after the label, 10 h after the webhook
        Assert.Equal(0, (await Ship.Maintenance(f)).Polled);
        f.Time.Advance(TimeSpan.FromHours(15));
        Assert.Equal(1, (await Ship.Maintenance(f)).Polled);
    }

    [Fact]
    public async Task Tracking_stops_after_sixty_days()
    {
        using var f = new ShipFactory();
        var (_, id, _, _) = await Bought(f);
        f.Time.Advance(TimeSpan.FromDays(61));
        var m = await Ship.Maintenance(f);
        Assert.Equal(1, m.Stopped); Assert.Equal(0, m.Polled); Assert.Equal(0, f.Provider.TrackingPolls);
        Assert.NotNull(Ship.Shipments(f, id).Single().TrackingStoppedAt);
    }

    [Fact]
    public async Task The_poll_lease_keeps_two_instances_from_polling_the_same_shipment()
    {
        using var f = new ShipFactory();
        var (_, id, _, _) = await Bought(f);
        f.Time.Advance(TimeSpan.FromHours(30));
        // another instance holds the lease
        f.WithDb(db => { db.Shipments.First(x => x.OrderId == id).PollLockedUntil = f.Time.GetUtcNow().UtcDateTime.AddMinutes(4); db.SaveChanges(); return 0; });
        Assert.Equal(0, (await Ship.Maintenance(f)).Polled);
        Assert.Equal(0, f.Provider.TrackingPolls);
        // the lease expires (crashed holder)
        f.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, (await Ship.Maintenance(f)).Polled);
    }

    private static async Task<MaintenanceSummary[]> TwoInstancesTogether(ShipFactory f)
    {
        // hold both instances between "found the shipment" and "claimed it", then let them claim at the same moment
        var arrived = 0; var gate = new TaskCompletionSource();
        f.Services.GetRequiredService<ShippingTestHooks>().AfterPollCandidate = async _ =>
        {
            if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
            await Task.WhenAny(gate.Task, Task.Delay(5000));
        };
        var both = await Task.WhenAll(Ship.Maintenance(f), Ship.Maintenance(f));
        Assert.Equal(2, arrived);                                                                // both really reached the claim together
        return both;
    }

    [Fact]
    public async Task Two_instances_never_poll_a_shipment_twice_even_when_the_second_claims_after_the_first_finished()
    {
        using var f = new ShipFactory();
        await Bought(f);
        f.Time.Advance(TimeSpan.FromHours(30));
        // the first instance may finish (and release its lease) before the second claims: the claim must re-check "still due"
        var gate = new TaskCompletionSource(); var seen = 0;
        f.Services.GetRequiredService<ShippingTestHooks>().AfterPollCandidate = async _ => { if (Interlocked.Increment(ref seen) == 1) await gate.Task; else gate.TrySetResult(); };
        var first = Ship.Maintenance(f);                                                         // holds at the hook until the second has also found the shipment
        var second = Ship.Maintenance(f);
        var done = await Task.WhenAll(first, second);
        Assert.Equal(1, f.Provider.TrackingPolls); Assert.Equal(1, done.Sum(x => x.Polled));
    }

    [Fact]
    public async Task Two_instances_claiming_at_the_same_moment_while_the_carrier_is_slow_poll_exactly_once()
    {
        using var f = new ShipFactory();
        await Bought(f);
        f.Time.Advance(TimeSpan.FromHours(30));
        f.Provider.PollDelayMs = 400;                                                            // the winner is still mid-poll when the loser tries to claim: only the lease stops it
        var both = await TwoInstancesTogether(f);
        Assert.Equal(1, f.Provider.TrackingPolls); Assert.Equal(1, both.Sum(x => x.Polled));
    }
}
