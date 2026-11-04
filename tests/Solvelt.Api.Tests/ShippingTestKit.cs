using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Shipping;

namespace Solvelt.Api.Tests;

public class ShipFactory : PayFactory
{
    public const string WebhookSecret = "ship-hook-secret-0123456789abcdef";
    public ShipFactory()
    {
        Extra["Shipping:WebhookSecret"] = WebhookSecret;
        Extra["RateLimit:ShippingPerMinute"] = "100000";
        Extra["Shipping:PublicBaseUrl"] = "https://api.test";
    }
    public FakeShippingProvider Provider => Services.GetRequiredService<FakeShippingProvider>();
}

public class SpendLimitFactory : ShipFactory { public SpendLimitFactory() { Extra["Shipping:SpendLimitCentsPerMonth"] = "1000"; } }

/// <summary>Helpers for the shipping tests: a shop with a ship-from address and a package, accepted orders, quotes, purchases and signed fake webhooks.</summary>
public static class Ship
{
    public static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public static object Address(string line1 = "10 Warehouse Rd", string country = "US", Guid? brandId = null, bool? isDefault = null) => new
    {
        label = "Warehouse", name = "Acme Shop", company = "Acme", line1, city = "Brooklyn", region = "NY", postalCode = "11201", country, phone = "+1 555 000 2222", brandId, isDefault
    };

    public static object Package(string name = "Medium box") => new { name, kind = "box", lengthMm = 300, widthMm = 200, heightMm = 150, emptyWeightGrams = 100 };

    /// <summary>A shop with a default ship-from address and a default package preset.</summary>
    public static async Task<(Shop Shop, Guid AddressId, Guid PackageId)> NewShop(ShipFactory f, int stock = 10)
    {
        var s = await Orders.NewShop(f, stock);
        var a = await Orders.Node(await s.Owner.Post("/api/v1/seller/shipping/addresses", Address()));
        var p = await Orders.Node(await s.Owner.Post("/api/v1/seller/shipping/packages", Package()));
        return (s, Guid.Parse((string)a["id"]!), Guid.Parse((string)p["id"]!));
    }

    public static async Task<(string Mid, Guid Id, string Number)> Accepted(Shop s, int qty = 1, long price = 1999, string? lid = null)
    {
        var (mid, id, number, _) = await Orders.Place(s, qty, price, lid: lid ?? "L1");
        await Orders.Ok(Orders.Act(s.Owner, id, "accept"));
        return (mid, id, number);
    }

    /// <summary>Accepted order for another country (ship-to Canada).</summary>
    public static async Task<(string Mid, Guid Id)> AcceptedAbroad(Shop s, string country = "CA", long price = 2500)
    {
        var mid = Orders.Mid();
        var body = Orders.OrderBody(s.F, mid, [Orders.Line(s.Seller, s.Brand, s.Product, 2, price, "L1")], [new { sellerId = s.Seller, brandId = s.Brand, amountCents = 900 }]);
        var node = JsonNode.Parse(JsonSerializer.Serialize(body, Orders.J))!;
        node["shippingAddress"]!["country"] = country; node["shippingAddress"]!["region"] = "ON"; node["shippingAddress"]!["postalCode"] = "M5V 2T6";
        var r = await s.F.NewClient().SendAsync(Orders.Signed(s.F, "POST", "/api/internal/v1/orders", node));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = Guid.Parse((string)(await Orders.Node(r))["orders"]![0]!["sellerOrderId"]!);
        await Orders.Ok(Orders.Act(s.Owner, id, "accept"));
        return (mid, id);
    }

    public static object Pkg(Guid preset, int grams = 500) => new { presetId = preset, weightGrams = grams };

    public static async Task<JsonNode> Quote(Api api, Guid orderId, Guid pkg, object? extra = null, int grams = 500)
    {
        var body = new Dictionary<string, object?> { ["package"] = Pkg(pkg, grams) };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var r = await api.Post($"/api/v1/seller/shipping/orders/{orderId}/rates", body);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await Orders.Node(r);
    }

    public static string Cheapest(JsonNode quote) => (string)quote["rates"]![0]!["rateId"]!;
    public static string Rate(JsonNode quote, string carrier, string service) =>
        (string)quote["rates"]!.AsArray().First(r => (string)r!["carrier"]! == carrier && (string)r!["service"]! == service)!["rateId"]!;

    public static Task<HttpResponseMessage> Buy(Api api, Guid orderId, JsonNode quote, string? rate = null, string? key = null, object? shipAnyway = null) =>
        api.Post($"/api/v1/seller/shipping/orders/{orderId}/labels", new { quoteId = (string)quote["quoteId"]!, rateId = rate ?? Cheapest(quote), idempotencyKey = key, shipAnyway });

    /// <summary>Quote + buy the cheapest rate; returns the label JSON.</summary>
    public static async Task<JsonNode> BuyCheapest(Api api, Guid orderId, Guid pkg, int grams = 500)
    {
        var q = await Quote(api, orderId, pkg, null, grams);
        var r = await Buy(api, orderId, q);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await Orders.Node(r))["label"]!;
    }

    // ---- webhooks ---------------------------------------------------------------------------------------------------------------------------
    public static string TrackBody(string tracking, string status, DateTime at, string? eventId = null, string? detail = null, string carrier = "usps") => JsonSerializer.Serialize(new
    {
        @event = "track_updated", event_id = eventId ?? "ev-" + Guid.NewGuid().ToString("N"),
        data = new
        {
            carrier, tracking_number = tracking, eta = (string?)null,
            tracking_status = new { status = Shippo(status), status_details = detail ?? status, status_date = at.ToString("O"), substatus = new { code = status } },
            tracking_history = Array.Empty<object>()
        }
    });

    private static string Shippo(string s) => s switch { "pre_transit" => "PRE_TRANSIT", "delivered" => "DELIVERED", "failure" => "FAILURE", "return_to_sender" => "RETURNED", "unknown" => "UNKNOWN", _ => "TRANSIT" };

    public static Task<HttpResponseMessage> Hook(ApiFactory f, string body, string? secret = ShipFactory.WebhookSecret, long? ts = null, bool sign = true, string? token = null)
    {
        var url = "/api/v1/shipping/webhooks/shippo" + (token is null ? "" : "?token=" + Uri.EscapeDataString(token));
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (sign && secret is not null) req.Headers.Add(ShippingWebhookService.SignatureHeader, ShippingWebhookService.Sign(secret, ts ?? f.Time.GetUtcNow().ToUnixTimeSeconds(), body));
        return f.NewClient().SendAsync(req);
    }

    public static async Task<string> Track(ApiFactory f, string tracking, string status, DateTime? at = null, string? eventId = null, string? detail = null)
    {
        var r = await Hook(f, TrackBody(tracking, status, at ?? f.Time.GetUtcNow().UtcDateTime, eventId, detail));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (string)(await Orders.Node(r))["outcome"]!;
    }

    public static Task<MaintenanceSummary> Maintenance(ApiFactory f) => Pay.Run(f, sp => sp.GetRequiredService<ShippingMaintenance>().RunAsync());

    // ---- reading state ------------------------------------------------------------------------------------------------------------------
    public static List<(string Type, JsonNode Data)> Outbox(ApiFactory f, string mid) =>
        f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == mid).OrderBy(m => m.CreatedAt).ToList())
            .Select(m => (m.EventType, JsonNode.Parse(m.PayloadJson)!["data"]!)).ToList();

    public static List<ShippingLabel> Labels(ApiFactory f, Guid orderId) => f.WithDb(db => db.ShippingLabels.AsNoTracking().Where(l => l.OrderId == orderId).ToList());
    public static List<Shipment> Shipments(ApiFactory f, Guid orderId) => f.WithDb(db => db.Shipments.AsNoTracking().Where(s => s.OrderId == orderId).ToList());
    public static SellerOrder Order(ApiFactory f, Guid id) => f.WithDb(db => db.Orders.AsNoTracking().First(o => o.Id == id));
    public static List<LedgerJournal> Journals(ApiFactory f, Guid seller, string type) =>
        f.WithDb(db => db.LedgerJournals.AsNoTracking().Include(j => j.Entries).Where(j => j.SellerId == seller && j.Type == type).ToList());

    public static string OwnerEmail(ApiFactory f, Guid seller) => Pay.OwnerEmail(f, seller);

    public static string Tn(JsonNode label) => (string)label["trackingNumber"]!;
}
