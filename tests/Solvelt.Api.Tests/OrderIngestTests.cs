using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class OrderIngestTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private const string Path = "/api/internal/v1/orders";

    // ---- HMAC, freshness, replay -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Unsigned_requests_are_rejected_and_create_nothing()
    {
        var s = await NewShop(f);
        var body = OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]);
        var r = await f.NewClient().SendAsync(Signed(f, "POST", Path, body, sign: false));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("unsigned", await Code(r));
    }

    [Fact]
    public async Task Wrong_secret_tampered_body_and_tampered_path_are_rejected()
    {
        var s = await NewShop(f);
        var body = OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]);
        Assert.Equal("bad_signature", await Code(await Inbound(f, Path, body, secret: "some-other-secret-value-0123456789")));
        // signed for one body, sent with another
        var req = Signed(f, "POST", Path, body);
        req.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 5)]), J));
        Assert.Equal("bad_signature", await Code(await f.NewClient().SendAsync(req)));
        // signed for a different path
        var req2 = Signed(f, "POST", "/api/internal/v1/messages", body);
        req2.RequestUri = new Uri(Path, UriKind.Relative);
        Assert.Equal("bad_signature", await Code(await f.NewClient().SendAsync(req2)));
    }

    [Fact]
    public async Task Stale_and_future_timestamps_are_rejected_but_the_window_edge_is_accepted()
    {
        var s = await NewShop(f);
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal("stale", await Code(await Inbound(f, Path, OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]), ts: now - 301)));
        Assert.Equal("stale", await Code(await Inbound(f, Path, OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]), ts: now + 301)));
        Assert.Equal(HttpStatusCode.Created, (await Inbound(f, Path, OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]), ts: now - 290)).StatusCode);
    }

    [Fact]
    public async Task A_replayed_nonce_is_rejected_even_with_a_valid_signature()
    {
        var s = await NewShop(f);
        var nonce = "n-" + Guid.NewGuid().ToString("N");
        var body = OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]);
        Assert.Equal(HttpStatusCode.Created, (await Inbound(f, Path, body, nonce)).StatusCode);
        var again = await Inbound(f, Path, body, nonce);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal("replayed", await Code(again));
    }

    [Fact]
    public async Task A_seller_token_never_opens_the_internal_api_and_a_signature_never_opens_seller_endpoints()
    {
        var s = await NewShop(f);
        var req = Signed(f, "POST", Path, OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]), sign: false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Owner.Session!.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(req)).StatusCode);
        var signedGet = Signed(f, "GET", "/api/v1/seller/orders", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(signedGet)).StatusCode);
    }

    [Fact]
    public async Task Oversized_bodies_and_malformed_json_are_rejected_after_the_signature_check()
    {
        var big = new { marketplaceOrderId = "x", junk = new string('a', 300_000) };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Inbound(f, Path, big)).StatusCode);
        var req = Signed(f, "POST", Path, new { }, sign: false);
        var raw = Encoding.UTF8.GetBytes("{not json");
        req.Content = new ByteArrayContent(raw);
        var t = f.Time.GetUtcNow().ToUnixTimeSeconds().ToString(); var n = Guid.NewGuid().ToString("N");
        req.Headers.Add(Signing.TimestampHeader, t); req.Headers.Add(Signing.NonceHeader, n);
        req.Headers.Add(Signing.SignatureHeader, Signing.Sign(OrdersFactory.InSecret, Signing.Canonical(t, n, "POST", Path, raw)));
        var r = await f.NewClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_json", await Code(r));
    }

    // ---- ingest ------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Ingest_creates_an_order_with_snapshot_numbers_money_and_sla_dates()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s, qty: 2, price: 2500, shipping: 599, tax: 400, discount: 500);
        Assert.Matches(@"^SO-\d{4}-\d{6}$", number);
        var d = await Detail(s.Owner, id);
        Assert.Equal("New", St(d));
        Assert.Equal(mid, (string)d["marketplaceOrderId"]!);
        var m = d["money"]!;
        Assert.Equal(5000, (long)m["itemsCents"]!); Assert.Equal(500, (long)m["discountCents"]!); Assert.Equal(599, (long)m["shippingCents"]!); Assert.Equal(400, (long)m["taxCents"]!);
        Assert.Equal(5000 - 500 + 599 + 400, (long)m["totalCents"]!);
        Assert.Equal(450, (long)m["feeCents"]!);                    // 10% of 4500
        Assert.Equal(5000 - 500 + 599 - 450, (long)m["netCents"]!); // tax is remitted by the marketplace
        var line = d["lines"]![0]!;
        Assert.Equal("Speckled stoneware coffee mug", (string)line["title"]!); Assert.Equal(s.Sku, (string)line["sku"]!); Assert.True((bool)line["hasImage"]!);
        Assert.True(DateTime.Parse((string)d["shipByAt"]!) > DateTime.Parse((string)d["placedAt"]!));
        Assert.True(DateTime.Parse((string)d["deliverByAt"]!) > DateTime.Parse((string)d["shipByAt"]!));
        Assert.Equal("Brooklyn", (string)d["shippingAddress"]!["city"]!);
    }

    [Fact]
    public async Task Ingest_is_idempotent_on_the_marketplace_order_id()
    {
        var s = await NewShop(f, stock: 10);
        var mid = Mid();
        var body = OrderBody(f, mid, [Line(s.Seller, s.Brand, s.Product, 2)]);
        var first = await Inbound(f, Path, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await Inbound(f, Path, body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await Node(first); var b = await Node(second);
        Assert.False((bool)b["created"]!);
        Assert.Equal((string)a["orders"]![0]!["number"]!, (string)b["orders"]![0]!["number"]!);
        Assert.Equal(1, f.WithDb(db => db.Orders.Count(o => o.MarketplaceOrderId == mid)));
        Assert.Equal(2, Stock(f, s.Product).Reserved); // reserved once, not twice
    }

    [Fact]
    public async Task Concurrent_duplicate_deliveries_create_one_order_and_reserve_once()
    {
        var s = await NewShop(f, stock: 10);
        var body = OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 3)]);
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Inbound(f, Path, body)));
        Assert.All(rs, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        Assert.Equal(3, Stock(f, s.Product).Reserved);
    }

    [Fact]
    public async Task One_marketplace_order_is_split_per_account_and_brand()
    {
        var s = await NewShop(f);
        var brand2 = await TeamHelpers.NewBrand(s.Owner, "Second Brand");
        var p2 = await ActiveProduct(s.Owner, "Linen napkin set", brand2, "NAP-" + Guid.NewGuid().ToString("N")[..6]);
        var other = await NewShop(f);
        var mid = Mid();
        var body = OrderBody(f, mid, [Line(s.Seller, s.Brand, s.Product, 1), Line(s.Seller, brand2, p2, 2), Line(other.Seller, other.Brand, other.Product, 1), Line(s.Seller, s.Brand, s.Product, 1, lid: "L-again")],
            [new { sellerId = s.Seller, brandId = s.Brand, amountCents = 400 }, new { sellerId = s.Seller, brandId = brand2, amountCents = 300 }, new { sellerId = other.Seller, brandId = other.Brand, amountCents = 200 }]);
        var r = await Inbound(f, Path, body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var orders = (await Node(r))["orders"]!.AsArray();
        Assert.Equal(3, orders.Count);
        Assert.Equal(3, orders.Select(o => (string)o!["number"]!).Distinct().Count());
        var mine = f.WithDb(db => db.Orders.Include(o => o.Lines).Where(o => o.MarketplaceOrderId == mid).ToList());
        Assert.Equal(2, mine.Single(o => o.BrandId == s.Brand).Lines.Count);
        Assert.Equal(400, mine.Single(o => o.BrandId == s.Brand).ShippingCents);
        Assert.Equal(300, mine.Single(o => o.BrandId == brand2).ShippingCents);
        Assert.Equal(other.Seller, mine.Single(o => o.SellerId == other.Seller).SellerId);
        // each seller only sees their own part
        var list = await Node(await s.Owner.Get("/api/v1/seller/orders?q=" + mid));
        Assert.Equal(2, (int)list["total"]!);
        Assert.Equal(1, (int)(await Node(await other.Owner.Get("/api/v1/seller/orders?q=" + mid)))["total"]!);
    }

    [Fact]
    public async Task Lines_from_another_account_brand_or_inactive_products_reject_the_whole_order()
    {
        var s = await NewShop(f);
        var other = await NewShop(f);
        var brand2 = await TeamHelpers.NewBrand(s.Owner, "B2");
        var draft = await ProdApi.ReadyProduct(s.Owner, "Draft thing", s.Brand, "DR-" + Guid.NewGuid().ToString("N")[..6]);
        async Task<JsonElement> Reject(object[] lines)
        {
            var r = await Inbound(f, Path, OrderBody(f, Mid(), lines));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        }
        // product belongs to another account
        Assert.Equal("product", (await Reject([Line(s.Seller, s.Brand, s.Product), Line(s.Seller, s.Brand, other.Product)]))[0].GetProperty("code").GetString());
        // right account, wrong brand
        Assert.Equal("product", (await Reject([Line(s.Seller, brand2, s.Product)]))[0].GetProperty("code").GetString());
        // brand belonging to another account
        Assert.Equal("brand", (await Reject([Line(s.Seller, other.Brand, s.Product)]))[0].GetProperty("code").GetString());
        // not active
        Assert.Equal("product_inactive", (await Reject([Line(s.Seller, s.Brand, draft)]))[0].GetProperty("code").GetString());
        // unknown sku, bad quantity/price, variant on a simple product
        var r2 = await Reject([new { sellerId = s.Seller, brandId = s.Brand, sku = "NOPE", quantity = 1, unitPriceCents = 100 }, Line(s.Seller, s.Brand, s.Product, 0), Line(s.Seller, s.Brand, s.Product, 1, -5)]);
        Assert.Equal(3, r2.GetArrayLength());
        Assert.Equal("variant", (await Reject([Line(s.Seller, s.Brand, s.Product, variant: Guid.NewGuid())]))[0].GetProperty("code").GetString());
        Assert.Equal(0, Stock(f, s.Product).Reserved); // nothing reserved by rejected orders
    }

    [Fact]
    public async Task Lines_can_be_identified_by_sku()
    {
        var s = await NewShop(f);
        var r = await Inbound(f, Path, OrderBody(f, Mid(), [new { sellerId = s.Seller, brandId = s.Brand, sku = s.Sku.ToLowerInvariant(), quantity = 1, unitPriceCents = 1500, marketplaceLineId = "L1" }]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Ingest_validates_the_payload_shape()
    {
        var s = await NewShop(f);
        async Task<string?> Bad(Func<dynamic, object> tweak)
        {
            var good = OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]);
            var r = await Inbound(f, Path, tweak(good));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            return (string?)(await Node(r))["errors"]![0]!["code"];
        }
        Assert.Equal("marketplaceOrderId", await Bad(o => new { marketplaceOrderId = "", o.buyer, o.shippingAddress, o.lines }));
        Assert.Equal("shippingAddress", await Bad(o => new { o.marketplaceOrderId, o.buyer, shippingAddress = new { name = "x" }, o.lines }));
        Assert.Equal("lines", await Bad(o => new { o.marketplaceOrderId, o.buyer, o.shippingAddress, lines = Array.Empty<object>() }));
        Assert.Equal("shippingSpeed", await Bad(o => new { o.marketplaceOrderId, o.buyer, o.shippingAddress, o.lines, shippingSpeed = "teleport" }));
    }

    [Fact]
    public void Signing_matches_the_documented_openssl_test_vector()
    {
        // generated with scripts/post-signed.sh's algorithm (openssl): see docs/ORDERS.md, "Signing test vector"
        var canonical = Signing.Canonical("1700000000", "abc123", "POST", "/api/internal/v1/orders", Encoding.UTF8.GetBytes("{\"a\":1}"));
        Assert.Equal("1700000000\nabc123\nPOST\n/api/internal/v1/orders\n015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862", canonical);
        Assert.Equal("v1=8a1a05a14201d38431d0c9816b6b127ff9193117e81e0530895a25934cbaadea", Signing.Sign("test-secret", canonical));
        Assert.True(Signing.Verify("test-secret", canonical, "V1=8A1A05A14201D38431D0C9816B6B127FF9193117E81E0530895A25934CBAADEA"));
        Assert.False(Signing.Verify("test-secret", canonical, "v1=00"));
        Assert.False(Signing.Verify("test-secret", canonical, null));
    }

    // ---- inventory -----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Ingest_reserves_stock_and_an_oversold_order_is_flagged_but_accepted()
    {
        var s = await NewShop(f, stock: 3);
        var (_, ok, _, _) = await Place(s, qty: 2);
        Assert.Equal((3L, 2L), Stock(f, s.Product));
        Assert.False((bool)(await Detail(s.Owner, ok))["oversold"]!);
        var (_, over, _, _) = await Place(s, qty: 5);
        var d = await Detail(s.Owner, over);
        Assert.True((bool)d["oversold"]!); Assert.Equal("New", St(d));
        Assert.True((bool)d["lines"]![0]!["stockShort"]!);
        Assert.Equal((3L, 2L), Stock(f, s.Product));        // the oversold order took nothing
        Assert.Contains(d["timeline"]!.AsArray(), e => (string)e!["type"]! == "order.oversold");
        Assert.Contains("\"oversold\":true", (await (await s.Owner.Get("/api/v1/seller/orders?oversold=true")).Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Concurrent_orders_never_oversell()
    {
        var s = await NewShop(f, stock: 5);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Place(s, qty: 1)));
        var db = f.WithDb(d => d.Orders.AsNoTracking().Where(o => results.Select(r => r.OrderId).Contains(o.Id)).ToList());
        Assert.Equal(12, db.Count);
        Assert.Equal(5, db.Count(o => !o.Oversold));
        Assert.Equal(7, db.Count(o => o.Oversold));
        Assert.Equal((5L, 5L), Stock(f, s.Product));
    }

    [Fact]
    public async Task Order_numbers_are_unique_under_concurrency()
    {
        var s = await NewShop(f, stock: 100);
        var res = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Place(s)));
        Assert.Equal(10, res.Select(r => r.Number).Distinct().Count());
    }

    [Fact]
    public async Task Fee_and_proration_math_is_exact_integer_arithmetic()
    {
        Assert.Equal(0, OrderSupport.Fee(0, 1000)); Assert.Equal(1, OrderSupport.Fee(5, 1000)); Assert.Equal(0, OrderSupport.Fee(4, 1000));
        Assert.Equal(1, OrderSupport.Fee(4, 1250));
        Assert.Equal(333, OrderSupport.Prorate(1000, 1, 3)); Assert.Equal(666, OrderSupport.Prorate(1000, 2, 3));
        Assert.Equal(3, OrderSupport.Fee(33, 1000));
        await Task.CompletedTask;
    }
}
