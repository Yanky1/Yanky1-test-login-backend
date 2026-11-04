using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Delivery;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>Fixed fake clock: Wednesday 2026-11-04 09:00 UTC (set before any sign-in so token times are consistent).</summary>
public class DeliveryFactory : PayFactory
{
    protected override DateTimeOffset StartTime => new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);   // the clock STARTS here; setting it after construction broke as soon as the real time passed 09:00 UTC on that day
}

public record DShop(Shop S, Guid WinA, Guid WinB);
public record Driver(Api Api, Guid Membership, string Email);

public static class Dlv
{
    public static readonly DateOnly Today = new(2026, 11, 4), Tomorrow = new(2026, 11, 5);
    public const double HomeLat = 40.6928, HomeLng = -73.9903;     // Brooklyn

    public static object Settings(Guid? brand = null, bool enabled = true, string? cutoff = "14:00", object[]? zones = null, object[]? windows = null, int? version = null) => new
    {
        brandId = brand, enabled, timeZone = "UTC", sameDayCutoff = cutoff, minLeadMinutes = 60, maxDaysAhead = 7, version,
        zones = zones ?? [new { name = "Brooklyn", country = "US", originLat = HomeLat, originLng = HomeLng, radiusKm = 5.0, postcodes = new[] { "11201", "112*" }, feeCents = 500, freeOverCents = 10000, minOrderCents = 0 }],
        windows = windows ?? [new { start = "12:00", end = "14:00", capacity = 2, days = new[] { 0, 1, 2, 3, 4, 5, 6 } }, new { start = "16:00", end = "18:00", capacity = 1, days = new[] { 0, 1, 2, 3, 4, 5, 6 } }]
    };

    public static async Task<DShop> NewDShop(ApiFactory f, int stock = 10, object? settings = null)
    {
        var s = await NewShop(f, stock);
        var r = await s.Owner.Put("/api/v1/seller/delivery/settings", settings ?? Settings());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var n = await Node(r);
        var w = n["windows"]!.AsArray().OrderBy(x => (string)x!["start"]!).ToList();
        return new(s, Guid.Parse((string)w[0]!["id"]!), Guid.Parse((string)w[Math.Min(1, w.Count - 1)]!["id"]!));
    }

    public static async Task<(string Mid, Guid OrderId, JsonNode Body)> PlaceLocal(DShop d, Guid? window = null, DateOnly? date = null, double lat = HomeLat, double lng = HomeLng,
        string postal = "11201", long price = 1999, int qty = 1, bool accept = false, string phone = "+1 555 000 1111")
    {
        var mid = Mid();
        var body = new
        {
            marketplaceOrderId = mid, placedAt = d.S.F.Time.GetUtcNow().UtcDateTime, currency = "USD",
            buyer = new { displayName = "Jane Buyer", relayEmail = "relay-abc@relay.solvelt.example", phone },
            shippingAddress = new { name = "Jane Buyer", line1 = "1 Main St", city = "Brooklyn", region = "NY", postalCode = postal, country = "US", lat, lng },
            shippingMethod = "local", shippingSpeed = "standard",
            lines = new[] { Line(d.S.Seller, d.S.Brand, d.S.Product, qty, price, "L1") },
            shipping = new[] { new { sellerId = d.S.Seller, brandId = d.S.Brand, amountCents = 500 } },
            deliveries = new[] { new { sellerId = d.S.Seller, brandId = d.S.Brand, method = "local", windowId = window ?? d.WinA, date = (date ?? Today).ToString("yyyy-MM-dd") } }
        };
        var r = await Inbound(d.S.F, "/api/internal/v1/orders", body);
        var n = await Node(r);
        Assert.True(r.StatusCode == HttpStatusCode.Created, n.ToJsonString());
        var id = Guid.Parse((string)n["orders"]![0]!["sellerOrderId"]!);
        if (accept) await Ok(Act(d.S.Owner, id, "accept"));
        return (mid, id, n);
    }

    public static async Task<Driver> AddDriver(DShop d, string? name = null)
    {
        var (api, email) = await TeamHelpers.JoinAs(d.S.F, d.S.Owner, "Driver");
        await api.Switch(d.S.Seller);
        var m = d.S.F.WithDb(db => db.Memberships.First(x => x.SellerId == d.S.Seller && db.Users.Any(u => u.Id == x.UserId && u.Email == email)).Id);
        return new(api, m, email);
    }

    public static Guid DeliveryId(ApiFactory f, Guid orderId) => f.WithDb(db => db.LocalDeliveries.First(x => x.OrderId == orderId).Id);
    public static LocalDelivery Del(ApiFactory f, Guid orderId) => f.WithDb(db => db.LocalDeliveries.First(x => x.OrderId == orderId));

    public static async Task<HttpResponseMessage> Assign(DShop d, Guid orderId, Driver dr) =>
        await d.S.Owner.Post($"/api/v1/seller/delivery/deliveries/{DeliveryId(d.S.F, orderId)}/assign", new { driverMembershipId = dr.Membership });

    public static Guid RouteOf(ApiFactory f, Guid orderId) => Del(f, orderId).RouteId!.Value;

    public static async Task<HttpResponseMessage> Start(Driver dr, Guid route) => await dr.Api.Post($"/api/v1/driver/routes/{route}/start");

    public static byte[] Jpeg(string exifMarker = "GPSDATA") =>
        [0xFF, 0xD8, 0xFF, 0xE1, 0x00, (byte)(2 + 6 + exifMarker.Length), (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, .. System.Text.Encoding.ASCII.GetBytes(exifMarker), 0xFF, 0xDA, 0x00, 0x02, 0xAA, 0xBB, 0xFF, 0xD9];

    public static async Task<Guid> Proof(Driver dr, Guid stop, string kind = "photo", Guid? clientId = null, byte[]? bytes = null)
    {
        var r = await UploadProof(dr, stop, kind, clientId, bytes);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return Guid.Parse((string)(await Node(r))["id"]!);
    }

    public static async Task<HttpResponseMessage> UploadProof(Driver dr, Guid stop, string kind = "photo", Guid? clientId = null, byte[]? bytes = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes ?? Jpeg()); file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "proof.jpg"); form.Add(new StringContent(kind), "kind"); form.Add(new StringContent((clientId ?? Guid.NewGuid()).ToString()), "clientId");
        return await dr.Api.Send(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/driver/stops/{stop}/proofs") { Content = form });
    }

    public static async Task<HttpResponseMessage> Event(Driver dr, Guid stop, string type, Guid? eventId = null, DateTime? at = null, object? extra = null, Guid[]? proofs = null,
        string? reason = null, string? placement = null, double? lat = null, double? lng = null, string? recipient = null) =>
        await dr.Api.Post($"/api/v1/driver/stops/{stop}/events", new
        {
            clientEventId = eventId ?? Guid.NewGuid(), type, clientAt = at, reason, placement, proofIds = proofs, lat, lng, recipientName = recipient
        });

    public static async Task<HttpResponseMessage> Deliver(Driver dr, Guid stop, Guid? eventId = null, DateTime? at = null)
    {
        var p = await Proof(dr, stop); var sig = await Proof(dr, stop, "signature", bytes: Jpeg("SIG"));
        return await Event(dr, stop, "delivered", eventId, at, proofs: [p, sig], placement: "handed_to", recipient: "Jane", lat: HomeLat, lng: HomeLng);
    }

    public static List<JsonNode> Events(ApiFactory f, string mid, string type) =>
        OutboxFor(f, mid).Where(m => m.EventType == type).Select(m => JsonNode.Parse(m.PayloadJson)!["data"]!).ToList();

    public static T Svc<T>(ApiFactory f, Func<IServiceProvider, Task<T>> work) where T : notnull
    {
        using var scope = f.Services.CreateScope();
        return work(scope.ServiceProvider).GetAwaiter().GetResult();
    }
}
