using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Solvelt.Api.Tests;

/// <summary>A card seller like the one in issue #101: two card designs at $1.00, a wallet and a pack as gift products.</summary>
public record CardShop(ApiFactory F, Api Owner, Guid Seller, Guid Brand, Guid CardA, Guid CardB, Guid Wallet, Guid Pack);

/// <summary>C18 quantity offers: helpers for the seller API, signed orders that carry offers, and the feed.</summary>
public static class OfferKit
{
    public const string OffersUrl = "/api/v1/seller/offers", Feed = "/api/internal/v1/catalog/offers", OrdersPath = "/api/internal/v1/orders";

    public static async Task<Guid> Product(Api api, Guid brand, string title, double price, int stock = 200, double? sale = null)
    {
        var id = await ProdApi.ReadyProduct(api, title, brand, "OF-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
        var r = await ProdApi.Put(api, id, d => { d["price"] = price; d["quantity"] = stock; if (sale is { } s) d["salePrice"] = s; });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await Orders.Activate(api, id);
    }

    public static async Task<CardShop> Shop(ApiFactory f, int giftStock = 5)
    {
        var (api, seller) = await ProdApi.Approved(f);
        var brand = await TeamHelpers.BrandId(api);
        var a = await Product(api, brand, "Plastic prayer card, blue design", 1.00);
        var b = await Product(api, brand, "Plastic prayer card, gold design", 1.00);
        var w = await Product(api, brand, "Card wallet", 4.00, giftStock);
        var p = await Product(api, brand, "Card pack", 2.00, giftStock);
        return new(f, api, seller, brand, a, b, w, p);
    }

    public static object Tier(int min, long cents) => new { minQuantity = min, unitPriceCents = cents };
    public static object Gift(int min, Guid product, int qty = 1) => new { minQuantity = min, productId = product, quantity = qty };

    /// <summary>The issue's offer: from 20 cards $0.90 each, and a wallet and a pack free from 10 cards.</summary>
    public static object Write(CardShop s, string status = "active", object[]? tiers = null, object[]? gifts = null, Guid[]? products = null, int? version = null,
        string name = "Blitz cards bundle", string scope = "products", Guid[]? categories = null) => new
    {
        name, status, scope, productIds = products ?? [s.CardA, s.CardB], categoryIds = categories,
        tiers = tiers ?? [Tier(20, 90)], gifts = gifts ?? [Gift(10, s.Wallet), Gift(10, s.Pack)], version
    };

    public static async Task<JsonNode> Create(CardShop s, object body, Api? as_ = null)
    {
        var r = await (as_ ?? s.Owner).Post(OffersUrl, body);
        Assert.True(r.StatusCode == HttpStatusCode.Created, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return await Orders.Node(r);
    }

    public static async Task<Guid> Standard(CardShop s) => Guid.Parse((string)(await Create(s, Write(s)))["id"]!);

    public static async Task<JsonNode> Update(CardShop s, Guid id, object body)
    {
        var r = await s.Owner.Put($"{OffersUrl}/{id}", body);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return await Orders.Node(r);
    }

    public static object Line(CardShop s, Guid product, int qty, long price = 100, long discount = 0, Guid? offer = null, Guid? seller = null, Guid? brand = null, string? lid = null) => new
    {
        marketplaceLineId = lid ?? "L-" + Guid.NewGuid().ToString("N")[..10], sellerId = seller ?? s.Seller, brandId = brand ?? s.Brand, productId = product, variantId = (Guid?)null,
        quantity = qty, unitPriceCents = price, discountCents = discount, taxCents = 0L, offerId = offer, isGift = false, giftOfOfferId = (Guid?)null
    };

    public static object GiftLine(CardShop s, Guid product, Guid offer, int qty = 1, long price = 0, string? lid = null) => new
    {
        marketplaceLineId = lid ?? "G-" + Guid.NewGuid().ToString("N")[..10], sellerId = s.Seller, brandId = s.Brand, productId = product, variantId = (Guid?)null,
        quantity = qty, unitPriceCents = price, discountCents = 0L, taxCents = 0L, offerId = (Guid?)null, isGift = true, giftOfOfferId = (Guid?)offer
    };

    /// <summary>Both gift lines of the issue's offer (wallet and pack).</summary>
    public static object[] Gifts(CardShop s, Guid offer) => [GiftLine(s, s.Wallet, offer), GiftLine(s, s.Pack, offer)];

    public static object Ref(Guid offer, int version, bool giftUnavailable = false) => new { offerId = offer, offerVersion = version, giftUnavailable };

    public static object Order(ApiFactory f, object[] lines, object[]? offers, string? mid = null, DateTime? placedAt = null, long shipping = 0, CardShop? s = null) => new
    {
        marketplaceOrderId = mid ?? Orders.Mid(), placedAt = placedAt ?? f.Time.GetUtcNow().UtcDateTime, currency = "USD",
        buyer = new { displayName = "Jane Buyer", relayEmail = "relay-abc@relay.solvelt.example", phone = "+1 555 000 1111" },
        shippingAddress = new { name = "Jane Buyer", line1 = "1 Main St", line2 = "Apt 2", city = "Brooklyn", region = "NY", postalCode = "11201", country = "US" },
        shippingMethod = "Standard", shippingSpeed = "standard", giftMessage = (string?)null, lines,
        shipping = s is null ? null : new[] { new { sellerId = s.Seller, brandId = s.Brand, amountCents = shipping } },
        offers
    };

    public static object[] Join(params object[][] parts) => parts.SelectMany(p => p).ToArray();

    public static Task<HttpResponseMessage> Send(ApiFactory f, object body) => Orders.Inbound(f, OrdersPath, body);

    /// <summary>Sends the order, expects 201 and returns the first seller order (id and the intake response entry).</summary>
    public static async Task<(Guid Id, JsonNode Entry)> Accepted(ApiFactory f, object body)
    {
        var r = await Send(f, body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.Created, $"{(int)r.StatusCode} {text}");
        var e = JsonNode.Parse(text)!["orders"]![0]!;
        return (Guid.Parse((string)e["sellerOrderId"]!), e);
    }

    /// <summary>Sends the order, expects 422 offer_mismatch with an error of <paramref name="kind"/>, and checks nothing was created.</summary>
    public static async Task<JsonNode> Mismatch(ApiFactory f, object body, string kind, string mid)
    {
        var r = await Send(f, body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)r.StatusCode} {text}");
        var n = JsonNode.Parse(text)!;
        Assert.Equal("offer_mismatch", (string)n["code"]!);
        Assert.Contains(n["errors"]!.AsArray(), e => (string)e!["kind"]! == kind);
        Assert.Equal(0, f.WithDb(db => db.Orders.Count(o => o.MarketplaceOrderId == mid)));
        return n;
    }

    public static (int Stock, int Reserved) Stock(ApiFactory f, Guid product) =>
        f.WithDb(db => { var p = db.Products.AsNoTracking().First(x => x.Id == product); return (p.Stock, p.Reserved); });

    public static JsonNode LineOf(JsonNode detail, Guid product, bool gift = false) =>
        detail["lines"]!.AsArray().First(l => (string)l!["productId"]! == product.ToString() && (bool)l["isGift"]! == gift)!;
}
