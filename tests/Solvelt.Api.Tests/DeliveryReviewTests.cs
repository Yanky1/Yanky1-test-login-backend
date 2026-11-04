using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Delivery;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;
using static Solvelt.Api.Tests.Dlv;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the orchestrator review of local delivery (findings 1-6). Each fails when its fix is reverted.</summary>
public class DeliveryReviewTests : IDisposable
{
    private readonly DeliveryFactory f = new();
    public void Dispose() => f.Dispose();

    private async Task<(DShop D, Driver Dr, Guid Order, string Mid, Guid Stop, Guid Route)> Started(double lat = HomeLat, double lng = HomeLng)
    {
        var d = await NewDShop(f); var dr = await AddDriver(d);
        var (mid, id, _) = await PlaceLocal(d, lat: lat, lng: lng, accept: true);
        Assert.Equal(HttpStatusCode.OK, (await Assign(d, id, dr)).StatusCode);
        var route = RouteOf(f, id);
        Assert.Equal(HttpStatusCode.OK, (await Start(dr, route)).StatusCode);
        return (d, dr, id, mid, DeliveryId(f, id), route);
    }

    private T Run<T>(Func<IServiceProvider, Task<T>> work) where T : notnull => Svc(f, work);

    // 1 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_delivered_event_dated_weeks_ago_cannot_put_DeliveredAt_before_the_shipment_left()
    {
        var p = await Started();
        var r = await Deliver(p.Dr, p.Stop, at: f.Time.GetUtcNow().UtcDateTime.AddDays(-40));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var o = f.WithDb(db => db.Orders.AsNoTracking().Include(x => x.Shipments).First(x => x.Id == p.Order));
        var shipped = o.Shipments.Single().ShippedAt;
        Assert.Equal(OrderStatus.Delivered, o.Status);
        Assert.True(o.DeliveredAt >= shipped, $"DeliveredAt {o.DeliveredAt:o} must not precede ShippedAt {shipped:o}");
        Assert.True(Del(f, p.Order).DeliveredAt >= shipped);
        Assert.True(f.WithDb(db => db.DeliveryAttempts.Single(a => a.DeliveryId == p.Stop).ClientAt) >= shipped);
        // the order did not jump to Completed (and so the payout did not become releasable) because of the forged date
        Run(async sp => await sp.GetRequiredService<OrderMaintenance>().RunAsync());
        Assert.Equal(OrderStatus.Delivered, f.WithDb(db => db.Orders.First(x => x.Id == p.Order).Status));
    }

    // 2 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Coarse_positions_are_removed_from_stored_outbox_payloads_after_the_retention_period()
    {
        var p = await Started();
        await p.Dr.Api.Post("/api/v1/driver/consent", new { consented = true });
        Assert.Equal(HttpStatusCode.OK, (await p.Dr.Api.Post("/api/v1/driver/location", new { lat = 40.6931, lng = -73.9900 })).StatusCode);
        Assert.Contains(f.WithDb(db => db.Outbox.Where(m => m.EventType == "order.tracking_updated").ToList()), m => m.PayloadJson.Contains("\"position\":{"));
        Run(async sp => await sp.GetRequiredService<DeliveryMaintenance>().RunAsync());                      // fresh: nothing to remove yet
        Assert.Contains(f.WithDb(db => db.Outbox.ToList()), m => m.PayloadJson.Contains("\"position\":{"));
        f.Time.Advance(TimeSpan.FromHours(25));
        Run(async sp => await sp.GetRequiredService<DeliveryMaintenance>().RunAsync());
        var rows = f.WithDb(db => db.Outbox.Where(m => m.EventType == "order.tracking_updated").ToList());
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, m => m.PayloadJson.Contains("\"position\":{"));
        Assert.Contains(rows, m => m.PayloadJson.Contains("\"status\":\"out_for_delivery\""));              // the rest of the event is untouched
    }

    // 3 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Redaction_removes_every_coordinate_and_free_text_the_delivery_kept()
    {
        var p = await Started();
        Assert.Equal(HttpStatusCode.OK, (await Deliver(p.Dr, p.Stop)).StatusCode);                          // recipient "Jane", delivered location, destination coordinates
        var before = f.WithDb(db => (Order: db.Orders.First(x => x.Id == p.Order), Del: db.LocalDeliveries.First(x => x.Id == p.Stop), Att: db.DeliveryAttempts.First(a => a.DeliveryId == p.Stop)));
        Assert.NotNull(before.Order.ShipLat); Assert.NotNull(before.Del.DestLat); Assert.NotNull(before.Del.DeliveredLat); Assert.Equal("Jane", before.Att.RecipientName); Assert.NotNull(before.Att.Lat);
        f.Time.Advance(TimeSpan.FromDays(31));
        Run(async sp => await sp.GetRequiredService<OrderMaintenance>().RunAsync());                        // return window over: Completed
        f.Time.Advance(TimeSpan.FromDays(91));
        Run(async sp => await sp.GetRequiredService<OrderMaintenance>().RunAsync());                        // retention over: buyer data redacted
        Run(async sp => await sp.GetRequiredService<DeliveryMaintenance>().RunAsync());
        var after = f.WithDb(db => (Order: db.Orders.First(x => x.Id == p.Order), Del: db.LocalDeliveries.First(x => x.Id == p.Stop), Att: db.DeliveryAttempts.First(a => a.DeliveryId == p.Stop)));
        Assert.NotNull(after.Order.PiiRedactedAt);
        Assert.Null(after.Order.ShipLat); Assert.Null(after.Order.ShipLng);
        Assert.Null(after.Del.DestLat); Assert.Null(after.Del.DestLng); Assert.Null(after.Del.DeliveredLat); Assert.Null(after.Del.DeliveredLng);
        Assert.Null(after.Att.RecipientName); Assert.Null(after.Att.Note); Assert.Null(after.Att.Lat); Assert.Null(after.Att.Lng);
    }

    // 4 -------------------------------------------------------------------------------------------------------------------------------
    private async Task<(DShop D, Driver Dr, Guid A, Guid B, string MidA, Guid Sa, Guid Sb)> TwoStops()
    {
        var d = await NewDShop(f, stock: 10); var dr = await AddDriver(d);
        var (midA, a, _) = await PlaceLocal(d, accept: true); var (_, b, _) = await PlaceLocal(d, accept: true);
        await Assign(d, a, dr); await Assign(d, b, dr);
        await Start(dr, RouteOf(f, a));
        return (d, dr, a, b, midA, DeliveryId(f, a), DeliveryId(f, b));
    }

    [Fact]
    public async Task The_stop_that_stops_being_next_gets_its_shipment_status_and_a_buyer_event()
    {
        var t = await TwoStops();
        Assert.Equal("out_for_delivery", f.WithDb(db => db.Shipments.First(s => s.OrderId == t.A).TrackingStatus));
        Assert.Equal(HttpStatusCode.OK, (await Event(t.Dr, t.Sb, "out_for_delivery")).StatusCode);       // the driver picks stop B: A is no longer next
        Assert.Equal(LocalStatus.InTransit, Del(f, t.A).Status);
        Assert.Equal("in_transit", f.WithDb(db => db.Shipments.First(s => s.OrderId == t.A).TrackingStatus));
        var events = Events(f, t.MidA, "order.tracking_updated");
        var told = events.Single(e => (string?)e["statusDetail"] == "Another stop is next");                // the buyer of A was told it is no longer next
        Assert.Equal("in_transit", (string)told["status"]!); Assert.Null(told["position"]);
        Assert.Contains(f.WithDb(db => db.TrackingEvents.Where(e => e.OrderId == t.A).ToList()), e => e.Status == "in_transit" && e.Detail == "Another stop is next");
    }

    [Fact]
    public async Task An_older_queued_out_for_delivery_cannot_take_the_route_back_to_another_stop()
    {
        var t = await TwoStops();
        var t0 = f.Time.GetUtcNow().UtcDateTime;
        Assert.Equal(HttpStatusCode.OK, (await Event(t.Dr, t.Sb, "out_for_delivery", at: t0.AddMinutes(4))).StatusCode);
        var stale = await Node(await Event(t.Dr, t.Sa, "out_for_delivery", at: t0.AddMinutes(1)));           // queued earlier on the phone, arrives after B's
        Assert.False((bool)stale["applied"]!); Assert.Equal("ignored_stale", (string)stale["reason"]!);
        Assert.Equal(LocalStatus.OutForDelivery, Del(f, t.B).Status); Assert.Equal(LocalStatus.InTransit, Del(f, t.A).Status);
        Assert.Equal("ignored_stale", f.WithDb(db => db.DeliveryClientEvents.Single(e => e.DeliveryId == t.Sa && e.Type == "out_for_delivery" && e.Result != "applied").Result));
    }

    // 5 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Overlapping_maintenance_runs_release_a_cancelled_orders_window_exactly_once()
    {
        var d = await NewDShop(f, stock: 10);
        var (_, keep, _) = await PlaceLocal(d); var (_, gone, _) = await PlaceLocal(d);
        Assert.Equal(2, f.WithDb(db => db.DeliveryWindowUsage.Single(u => u.WindowId == d.WinA && u.Date == Today).Booked));
        await Ok(Act(d.S.Owner, gone, "cancel", new { reason = "out_of_stock", restock = true }));
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var scope = f.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DeliveryMaintenance>().RunAsync();
        })));
        Assert.Equal(1, runs.Sum(r => r.Cancelled));                                                       // counted once across all runs
        Assert.Equal(LocalStatus.Cancelled, Del(f, gone).Status); Assert.Equal(LocalStatus.Unassigned, Del(f, keep).Status);
        Assert.Equal(1, f.WithDb(db => db.DeliveryWindowUsage.Single(u => u.WindowId == d.WinA && u.Date == Today).Booked));   // not 0
    }

    // 6 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Proof_links_are_signed_with_their_own_key_not_the_shared_marketplace_secret()
    {
        var p = await Started();
        await Deliver(p.Dr, p.Stop);
        var proof = f.WithDb(db => db.DeliveryProofs.First(x => x.DeliveryId == p.Stop).Id);
        var exp = new DateTimeOffset(f.Time.GetUtcNow().UtcDateTime.AddDays(3650)).ToUnixTimeSeconds();           // an expiry nobody issued
        string Sig(string key) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"delivery-proof|{proof:N}|{exp}"))).ToLowerInvariant();
        var forged = await f.NewClient().GetAsync($"/api/public/delivery-proof/{proof}?exp={exp}&sig={Sig(OrdersFactory.OutSecret)}");
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);                                          // whoever holds the Orders secret cannot mint links
        var ok = await f.NewClient().GetAsync($"/api/public/delivery-proof/{proof}?exp={exp}&sig={Sig("dev-only-proof-url-key-0123456789abcdef")}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);                                                    // the dedicated key still works
    }
}
