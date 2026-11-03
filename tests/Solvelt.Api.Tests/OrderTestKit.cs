using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Tests;

public class OrdersFactory : ApiFactory
{
    public const string InSecret = "test-inbound-secret-0123456789abcdef", OutSecret = "test-outbound-secret-fedcba9876543210";
    public OrdersFactory()
    {
        Extra["Orders:InboundSecret"] = InSecret;
        Extra["Orders:OutboundSecret"] = OutSecret;
        Extra["Orders:StreamRecheckSeconds"] = "1";
        Extra["RateLimit:OrdersPerMinute"] = "100000";
        Extra["RateLimit:InternalPerMinute"] = "100000";
    }
}

/// <summary>Replaces the real HTTP transport of the outbox so tests can make the marketplace fail.</summary>
public class WebhookFactory : OrdersFactory
{
    public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
    public List<(HttpRequestMessage Req, string Body)> Calls { get; } = [];
    public WebhookFactory() { Extra["Orders:WebhookUrl"] = "https://marketplace.test/hooks/solvelt"; Extra["Orders:OutboxBackoffSeconds"] = "30"; }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddHttpClient("outbox").ConfigurePrimaryHttpMessageHandler(() => new Fake(this)));
    }

    private sealed class Fake(WebhookFactory f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (f.Calls) f.Calls.Add((request, body));
            return f.Handler(request);
        }
    }
}

public record Shop(ApiFactory F, Api Owner, Guid Seller, Guid Brand, Guid Product, string Sku);

public static class Orders
{
    public static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public static async Task<Guid> Activate(Api api, Guid id)
    {
        var r = await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return id;
    }

    public static async Task<Guid> ActiveProduct(Api api, string title, Guid? brand, string sku, int stock = 10)
    {
        var id = await ProdApi.ReadyProduct(api, title, brand, sku);
        if (stock != 10) Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => d["quantity"] = stock)).StatusCode);
        return await Activate(api, id);
    }

    public static async Task<Shop> NewShop(ApiFactory f, int stock = 10)
    {
        var (api, seller) = await ProdApi.Approved(f);
        var brand = await TeamHelpers.BrandId(api);
        var sku = "SKU-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var p = await ActiveProduct(api, "Speckled stoneware coffee mug", brand, sku, stock);
        return new(f, api, seller, brand, p, sku);
    }

    public static (long Stock, long Reserved) Stock(ApiFactory f, Guid product) =>
        f.WithDb(db => { var p = db.Products.AsNoTracking().First(x => x.Id == product); return ((long)p.Stock, (long)p.Reserved); });

    // ---- signing -------------------------------------------------------------------------------------------------------------
    public static HttpRequestMessage Signed(ApiFactory f, string method, string path, object? body, string? nonce = null, long? ts = null, string secret = OrdersFactory.InSecret, bool sign = true)
    {
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, J);
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) { req.Content = new ByteArrayContent(bytes); req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        if (!sign) return req;
        var t = (ts ?? f.Time.GetUtcNow().ToUnixTimeSeconds()).ToString();
        var n = nonce ?? Guid.NewGuid().ToString("N");
        req.Headers.Add(Signing.TimestampHeader, t);
        req.Headers.Add(Signing.NonceHeader, n);
        req.Headers.Add(Signing.SignatureHeader, Signing.Sign(secret, Signing.Canonical(t, n, method, path, bytes)));
        return req;
    }

    public static async Task<HttpResponseMessage> Inbound(ApiFactory f, string path, object body, string? nonce = null, long? ts = null, string secret = OrdersFactory.InSecret) =>
        await f.NewClient().SendAsync(Signed(f, "POST", path, body, nonce, ts, secret));

    public static async Task<JsonNode> Node(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    public static object Line(Guid seller, Guid brand, Guid product, int qty = 1, long price = 1999, string? lid = null, Guid? variant = null, long discount = 0, long tax = 0) =>
        new { marketplaceLineId = lid ?? "L-" + Guid.NewGuid().ToString("N")[..10], sellerId = seller, brandId = brand, productId = product, variantId = variant, quantity = qty, unitPriceCents = price, discountCents = discount, taxCents = tax };

    public static object OrderBody(ApiFactory f, string mid, object[] lines, object[]? shipping = null, string buyer = "Jane Buyer", string speed = "standard") => new
    {
        marketplaceOrderId = mid, placedAt = f.Time.GetUtcNow().UtcDateTime, currency = "USD",
        buyer = new { displayName = buyer, relayEmail = "relay-abc@relay.solvelt.example", phone = "+1 555 000 1111" },
        shippingAddress = new { name = buyer, line1 = "1 Main St", line2 = "Apt 2", city = "Brooklyn", region = "NY", postalCode = "11201", country = "US" },
        shippingMethod = "Standard", shippingSpeed = speed, giftMessage = (string?)null, lines, shipping
    };

    public static string Mid() => "MP-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    /// <summary>Ingests an order for the shop's product and returns (marketplace id, seller order id).</summary>
    public static async Task<(string Mid, Guid OrderId, string Number, JsonNode Body)> Place(Shop s, int qty = 1, long price = 1999, long shipping = 500, long tax = 0, long discount = 0, string buyer = "Jane Buyer", string? lid = null)
    {
        var mid = Mid();
        var body = OrderBody(s.F, mid, [Line(s.Seller, s.Brand, s.Product, qty, price, lid ?? "L1", tax: tax, discount: discount)], [new { sellerId = s.Seller, brandId = s.Brand, amountCents = shipping }], buyer);
        var r = await Inbound(s.F, "/api/internal/v1/orders", body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var n = await Node(r);
        var o = n["orders"]![0]!;
        return (mid, Guid.Parse((string)o["sellerOrderId"]!), (string)o["number"]!, n);
    }

    public static async Task<JsonNode> Detail(Api api, Guid id) { var r = await api.Get($"/api/v1/seller/orders/{id}"); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await Node(r); }

    public static async Task<HttpResponseMessage> Act(Api api, Guid id, string action, object? body = null) => await api.Post($"/api/v1/seller/orders/{id}/{action}", body ?? new { });

    public static string St(JsonNode n) => (string)n["status"]!;

    public static async Task<JsonNode> Ok(Task<HttpResponseMessage> t)
    {
        var r = await t;
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await Node(r);
    }

    public static async Task<Guid> LineId(Api api, Guid orderId, int i = 0) => Guid.Parse((string)(await Detail(api, orderId))["lines"]![i]!["id"]!);

    public static async Task<string?> Code(HttpResponseMessage r) => (string?)(await Node(r))["code"];

    public static object Ship(Guid? line = null, int qty = 0, string carrier = "UPS", string? tracking = null) => new
    {
        carrier, trackingNumber = tracking ?? "1Z999AA1" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999),
        lines = line is null ? null : new[] { new { lineId = line.Value, quantity = qty } }
    };

    public static List<OutboxMessage> OutboxFor(ApiFactory f, string aggregate) =>
        f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == aggregate).OrderBy(m => m.CreatedAt).ToList());
}
