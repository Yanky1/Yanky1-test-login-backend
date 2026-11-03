using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;

namespace Solvelt.Api.Tests;

public class ProductApiTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static Guid Id(JsonNode n) => Guid.Parse((string)n["id"]!);

    [Fact]
    public async Task Categories_are_seeded_with_data_driven_attributes()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var cats = await api.JsonBody(await api.Get("/api/v1/categories"));
        var slugs = cats.EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ToList();
        foreach (var s in new[] { "electronics", "home-kitchen", "books", "grocery-food", "sports-outdoors", "toys-games", "beauty-personal-care" }) Assert.Contains(s, slugs);
        var food = await ProdApi.CategoryId(api, "bakery");
        var attrs = await api.JsonBody(await api.Get($"/api/v1/categories/{food}/attributes"));
        var keys = attrs.EnumerateArray().Select(a => a.GetProperty("key").GetString()).ToList();
        Assert.Contains("allergens", keys); Assert.Contains("ingredients", keys); Assert.Contains("certifications", keys); // inherited from Grocery & Food
        Assert.Equal(HttpStatusCode.NotFound, await api.Get($"/api/v1/categories/{Guid.NewGuid()}/attributes").Code());
    }

    [Fact]
    public async Task Draft_can_be_saved_by_a_pending_seller_but_not_activated()
    {
        var (api, _) = await ProdApi.Approved(f, approve: false);
        var id = await ProdApi.ReadyProduct(api);
        var r = await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("seller_not_approved", await r.ErrCode());
        Assert.Equal("Draft", (string)(await ProdApi.Get(api, id))["status"]!);
    }

    [Fact]
    public async Task Publish_rules_return_a_structured_missing_list_then_activate()
    {
        var (api, _) = await ProdApi.Approved(f);
        var created = await ProdApi.Create(api, "Mug", slug: "kitchen-dining");
        var id = Id(created);
        var r = await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var body = await api.JsonBody(r);
        var issues = body.GetProperty("extra").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Contains("title", issues); Assert.Contains("images", issues); Assert.Contains("price", issues); Assert.Contains("stock", issues);
        var read = await api.JsonBody(await api.Get($"/api/v1/seller/products/{id}/readiness"));
        Assert.False(read.GetProperty("canPublish").GetBoolean());

        var ready = await ProdApi.ReadyProduct(api, "Speckled stoneware coffee mug");
        var detail = await ProdApi.Get(api, ready);
        Assert.True((bool)detail["canPublish"]!); Assert.InRange((int)detail["qualityScore"]!, 1, 100);
        Assert.Equal(HttpStatusCode.OK, await api.Post($"/api/v1/seller/products/{ready}/status", new { status = "active" }).Code());
        detail = await ProdApi.Get(api, ready);
        Assert.Equal("Active", (string)detail["status"]!); Assert.Equal("speckled-stoneware-coffee-mug", (string)detail["handle"]!);
        Assert.Contains(f.WithDb(db => db.AuditEvents.Select(a => a.Type).ToList()), t => t == "product.activated");
    }

    [Fact]
    public async Task Required_category_attributes_block_publish_and_are_cleared_on_category_change()
    {
        var (api, _) = await ProdApi.Approved(f);
        var bakery = await ProdApi.CategoryId(api, "bakery");
        var id = Guid.Parse((string)(await ProdApi.Create(api, "Sourdough loaf, 800 g, organic", "bakery"))["id"]!);
        Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(api, id, TestImages.Png())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => { d["price"] = 6; d["quantity"] = 5; d["noBarcode"] = true; })).StatusCode);
        var r = await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var missing = (await api.JsonBody(r)).GetProperty("extra").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Contains("attr:ingredients", missing); Assert.Contains("attr:allergens", missing); Assert.Contains("attr:net_weight", missing);

        // invalid select/multiselect options are rejected
        var bad = await ProdApi.Put(api, id, d => d["attributes"] = new JsonObject { ["allergens"] = new JsonArray("Moonrock") });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await ProdApi.Put(api, id, d => d["attributes"] = new JsonObject
        { ["ingredients"] = "Flour, water, salt", ["allergens"] = new JsonArray("Wheat"), ["net_weight"] = "800 g", ["certifications"] = new JsonArray("Organic", "Kosher") });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" }).Code());
        _ = bakery;

        // moving to a category without these fields drops the values (server side), and a live listing stays valid
        var kitchen = await ProdApi.CategoryId(api, "kitchen-dining");
        await api.Post($"/api/v1/seller/products/{id}/status", new { status = "inactive" });
        var moved = await ProdApi.Put(api, id, d => d["categoryId"] = kitchen);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var attrs = (JsonObject)(await ProdApi.Get(api, id))["attributes"]!;
        Assert.Empty(attrs);
    }

    [Fact]
    public async Task Active_listings_cannot_be_saved_into_an_unpublishable_state()
    {
        var (api, _) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api);
        await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" });
        var r = await ProdApi.Put(api, id, d => d["price"] = null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("not_publishable", await r.ErrCode());
        Assert.Equal(19.99, (double)(await ProdApi.Get(api, id))["price"]!);
        Assert.Equal(HttpStatusCode.Conflict, await api.Delete($"/api/v1/seller/products/{id}").Code()); // deactivate first
        Assert.Equal(HttpStatusCode.OK, await api.Post($"/api/v1/seller/products/{id}/status", new { status = "inactive" }).Code());
        Assert.Equal(HttpStatusCode.NoContent, await api.Delete($"/api/v1/seller/products/{id}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await api.Get($"/api/v1/seller/products/{id}").Code());
        Assert.Contains(f.WithDb(db => db.AuditEvents.Select(a => a.Type).ToList()), t => t == "product.deleted");
    }

    [Fact]
    public async Task Invalid_barcode_is_saved_on_drafts_but_blocks_publishing_and_live_edits()
    {
        var (api, _) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => { d["barcode"] = "036000291453"; d["noBarcode"] = false; })).StatusCode);
        var d1 = await ProdApi.Get(api, id);
        Assert.False((bool)d1["barcodeCheck"]!["valid"]!); Assert.False((bool)d1["canPublish"]!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" }).Code());
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => d["barcode"] = "0-36000-29145-2")).StatusCode);
        var d2 = await ProdApi.Get(api, id);
        Assert.Equal("036000291452", (string)d2["barcode"]!); Assert.Equal("UPC-A", (string)d2["barcodeCheck"]!["type"]!);
        Assert.Equal(HttpStatusCode.OK, await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" }).Code());
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Put(api, id, d => d["barcode"] = "036000291453")).StatusCode);
    }

    [Fact]
    public async Task Field_validation_ranges_sku_uniqueness_and_sale_dates()
    {
        var (api, _) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api, sku: "SKU-A1");
        var other = await ProdApi.ReadyProduct(api, "Another speckled mug", sku: "SKU-B1");
        async Task<HttpStatusCode> Try(Action<JsonObject> t) => (await ProdApi.Put(api, id, t)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["price"] = -1));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["price"] = 10_000_000));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["price"] = 19.999));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["title"] = new string('x', 201)));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["quantity"] = -5));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["bullets"] = new JsonArray("1", "2", "3", "4", "5", "6")));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["sku"] = "bad sku <script>"));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["sku"] = "sku-b1")); // case-insensitive per-seller uniqueness
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["compareAtPrice"] = 10)); // must be above the price
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["salePrice"] = 25)); // must be below the price
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => { d["salePrice"] = 15; d["saleStart"] = "2030-02-01T00:00:00Z"; d["saleEnd"] = "2030-01-01T00:00:00Z"; }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["saleStart"] = "2030-02-01T00:00:00Z")); // dates need a sale price
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["weightGrams"] = 0));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(d => d["metaDescription"] = new string('x', 161)));
        Assert.Equal(HttpStatusCode.OK, await Try(d => d["sku"] = "SKU-A1"));
        _ = other;
    }

    [Fact]
    public async Task Sale_price_is_only_in_effect_between_its_dates()
    {
        var (api, _) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api);
        var now = f.Time.GetUtcNow();
        async Task<JsonNode> With(DateTimeOffset? start, DateTimeOffset? end)
        {
            Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => { d["salePrice"] = 14.5; d["saleStart"] = start?.UtcDateTime; d["saleEnd"] = end?.UtcDateTime; })).StatusCode);
            return await ProdApi.Get(api, id);
        }
        var future = await With(now.AddDays(1), now.AddDays(3));
        Assert.False((bool)future["onSale"]!); Assert.Equal(19.99, (double)future["currentPrice"]!);
        var current = await With(now.AddDays(-1), now.AddDays(3));
        Assert.True((bool)current["onSale"]!); Assert.Equal(14.5, (double)current["currentPrice"]!);
        var expired = await With(now.AddDays(-3), now.AddDays(-1));
        Assert.False((bool)expired["onSale"]!); Assert.Equal(19.99, (double)expired["currentPrice"]!);
        var open = await With(null, null);
        Assert.True((bool)open["onSale"]!);
        var list = await api.JsonBody(await api.Get("/api/v1/seller/products"));
        Assert.Equal(14.5, list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == id).GetProperty("currentPrice").GetDouble());
        // the rule itself, at explicit instants
        var p = new Product { PriceCents = 1999, SaleCents = 1450, SaleStart = now.UtcDateTime.AddDays(1), SaleEnd = now.UtcDateTime.AddDays(3) };
        Assert.Equal(1999, ProductRules.CurrentPriceCents(p, now.UtcDateTime));
        Assert.Equal(1450, ProductRules.CurrentPriceCents(p, now.UtcDateTime.AddDays(2)));
        Assert.Equal(1999, ProductRules.CurrentPriceCents(p, now.UtcDateTime.AddDays(3)));
    }

    [Fact]
    public async Task Optimistic_concurrency_rejects_stale_writes()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var id = Id(await ProdApi.Create(api));
        var stale = (JsonObject)await ProdApi.Get(api, id);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => d["title"] = "First writer wins mug")).StatusCode);
        stale["title"] = "Second writer loses";
        var r = await api.Put($"/api/v1/seller/products/{id}", JsonSerializer.Deserialize<JsonElement>(stale.ToJsonString()));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("version_conflict", await r.ErrCode());
        Assert.Equal("First writer wins mug", (string)(await ProdApi.Get(api, id))["title"]!);
        var q = await api.Patch($"/api/v1/seller/products/{id}/quick", new { version = 1, price = 5 });
        Assert.Equal(HttpStatusCode.Conflict, q.StatusCode);
    }

    [Fact]
    public async Task Image_upload_validates_content_strips_metadata_and_limits_count()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var id = Id(await ProdApi.Create(api));
        // spoofed extension / content
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Upload(api, id, TestImages.Png(), "photo.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Upload(api, id, "<html><script>alert(1)</script></html>"u8.ToArray(), "photo.png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Upload(api, id, TestImages.Png(), "photo.gif")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Upload(api, id, TestImages.Png(50, 50), "tiny.png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Upload(api, id, new byte[11 * 1024 * 1024], "huge.png")).StatusCode);
        // good images in all three formats
        var first = await ProdApi.Upload(api, id, TestImages.Png(1200, 800), "a.png", "Front");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var img = (await ProdApi.Node(first))["image"]!;
        Assert.True((bool)img["isMain"]!);
        Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(api, id, TestImages.Jpeg(), "b.jpeg")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(api, id, TestImages.WebP(), "c.webp")).StatusCode);
        // served through the API with nosniff and the stored copy has no metadata
        var file = await api.Get((string)img["url"]!);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/png", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.DoesNotContain("SECRET", System.Text.Encoding.Latin1.GetString(await file.Content.ReadAsByteArrayAsync()));
        // max 9
        for (var i = 3; i < 9; i++) Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(api, id, TestImages.Png(), $"{i}.png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Upload(api, id, TestImages.Png(), "tenth.png")).StatusCode);
        // storage keys are random 32-hex ids, not file names
        var keys = f.WithDb(db => db.ProductImages.Where(x => x.ProductId == id).Select(x => x.StorageKey).ToList());
        Assert.All(keys, k => Assert.True(Guid.TryParseExact(k, "N", out _)));
    }

    [Fact]
    public async Task Images_reorder_set_main_alt_and_delete()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var id = Id(await ProdApi.Create(api));
        var ids = new List<string>();
        for (var i = 0; i < 3; i++) ids.Add((string)(await ProdApi.Node(await ProdApi.Upload(api, id, TestImages.Png(), $"{i}.png")))["image"]!["id"]!);
        var reorder = await api.Put($"/api/v1/seller/products/{id}/images/order", new { ids = new[] { ids[2], ids[0], ids[1] } });
        Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
        var imgs = (JsonArray)(await ProdApi.Get(api, id))["images"]!;
        Assert.Equal(ids[2], (string)imgs[0]!["id"]!); Assert.True((bool)imgs[0]!["isMain"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/seller/products/{id}/images/order", new { ids = new[] { ids[0] } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/v1/seller/products/{id}/images/{ids[1]}", new { alt = "Side view", isMain = true })).StatusCode);
        imgs = (JsonArray)(await ProdApi.Get(api, id))["images"]!;
        Assert.Equal(ids[1], (string)imgs[0]!["id"]!); Assert.Equal("Side view", (string)imgs[0]!["alt"]!); Assert.Single(imgs.Where(i => (bool)i!["isMain"]!));
        Assert.Equal(HttpStatusCode.OK, await api.Delete($"/api/v1/seller/products/{id}/images/{ids[1]}").Code());
        imgs = (JsonArray)(await ProdApi.Get(api, id))["images"]!;
        Assert.Equal(2, imgs.Count); Assert.True((bool)imgs[0]!["isMain"]!);
    }

    [Fact]
    public async Task Variants_are_generated_validated_and_images_are_limited_per_variant()
    {
        var (api, _) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api);
        var pre = await api.Post($"/api/v1/seller/products/{id}/variants/preview", new { hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S", "M" } }, new { name = "Color", values = new[] { "Red", "Blue" } } } });
        Assert.Equal(4, (await api.JsonBody(pre)).GetProperty("combinations").GetArrayLength());
        var put = await api.Put($"/api/v1/seller/products/{id}/variants", new
        {
            hasVariants = true,
            options = new[] { new { name = "Size", values = new[] { "S", "M" } }, new { name = "Color", values = new[] { "Red", "Blue" } } },
            variants = new[] { new { values = new[] { "S", "Red" }, sku = "MUG-S-R", price = 18.5, quantity = 7, barcode = (string?)null, enabled = true } }
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var d = await ProdApi.Node(put);
        var vs = (JsonArray)d["variants"]!;
        Assert.Equal(4, vs.Count); Assert.Equal("MUG-S-R", (string)vs[0]!["sku"]!); Assert.Equal(7, (int)vs[0]!["quantity"]!); Assert.Equal(0, (int)vs[3]!["quantity"]!);
        // duplicate SKU, bad combo, too many options
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/seller/products/{id}/variants", new
        { hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S" } } },
          variants = new[] { new { values = new[] { "XL" }, sku = "X1", price = 1, quantity = 1, barcode = (string?)null, enabled = true } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/seller/products/{id}/variants", new
        { hasVariants = true, options = new[] { new { name = "A", values = new[] { "1" } }, new { name = "B", values = new[] { "1" } }, new { name = "C", values = new[] { "1" } }, new { name = "D", values = new[] { "1" } } }, variants = Array.Empty<object>() })).StatusCode);
        // images per variant: max 9
        var vid = Guid.Parse((string)vs[0]!["id"]!);
        for (var i = 0; i < 9; i++) Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(api, id, TestImages.Png(), $"{i}.png", variantId: vid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Upload(api, id, TestImages.Png(), "x.png", variantId: vid)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(api, id, TestImages.Png(), "main.png")).StatusCode); // the product's own set is separate
        // stock for variants: ready while a variant has stock; re-saving keeps existing stock rows
        var again = await ProdApi.Node(await api.Put($"/api/v1/seller/products/{id}/variants", new
        { hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S", "M" } }, new { name = "Color", values = new[] { "Red", "Blue" } } },
          variants = new[] { new { values = new[] { "S", "Red" }, sku = "MUG-S-R", price = 18.5, quantity = 9, barcode = (string?)null, enabled = true } } }));
        Assert.Equal(9, (int)again["variants"]![0]!["quantity"]!);
        Assert.Equal(HttpStatusCode.OK, await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" }).Code());
        // dropping an option value removes its variants and their images
        var shrink = await api.Put($"/api/v1/seller/products/{id}/variants", new { hasVariants = true, options = new[] { new { name = "Size", values = new[] { "M" } }, new { name = "Color", values = new[] { "Red" } } }, variants = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, shrink.StatusCode);
        Assert.Empty(f.WithDb(db => db.ProductImages.Where(x => x.VariantId == vid).ToList()));
    }

    [Fact]
    public async Task Faqs_and_listing_fields_roundtrip()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var id = Id(await ProdApi.Create(api));
        var r = await api.Put($"/api/v1/seller/products/{id}/faqs", new[] { new { question = "Is it dishwasher safe?", answer = "Yes." }, new { question = "Microwave?", answer = "Yes, too." } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/seller/products/{id}/faqs", new[] { new { question = "No answer?", answer = "" } })).StatusCode);
        var put = await ProdApi.Put(api, id, d =>
        {
            d["bullets"] = new JsonArray("350 ml", "Dishwasher safe", "Hand glazed"); d["description"] = "# Mug\n**Great** <b>mug</b> [x](javascript:alert(1))";
            d["weightGrams"] = 420; d["lengthCm"] = 12.5; d["handlingDays"] = 2; d["returnsDays"] = 30; d["warrantyMonths"] = 12; d["searchTerms"] = "mug cup";
            d["shipping"] = new JsonObject { ["standardEnabled"] = true, ["standardPrice"] = 4.5, ["expressEnabled"] = true, ["expressPrice"] = 12, ["freeOver"] = 50 };
            d["localPickup"] = true; d["price"] = 20; d["cost"] = 8; d["condition"] = "Used"; d["conditionNote"] = "Tiny chip on the base";
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var d = await ProdApi.Get(api, id);
        Assert.Equal(2, ((JsonArray)d["faqs"]!).Count); Assert.Equal(3, ((JsonArray)d["bullets"]!).Count);
        Assert.Equal(12.5, (double)d["lengthCm"]!); Assert.Equal(50, (double)d["shipping"]!["freeOver"]!); Assert.True((bool)d["localPickup"]!);
        Assert.Equal(60.0, (double)d["margin"]!, 1);
        var html = (string)d["descriptionHtml"]!;
        Assert.DoesNotContain("<b>", html); Assert.DoesNotContain("javascript:", html); Assert.Contains("<strong>Great</strong>", html);
    }

    [Fact]
    public async Task List_filters_search_sort_pagination_quick_edit_duplicate_and_bulk_status()
    {
        var (api, _) = await ProdApi.Approved(f);
        var a = await ProdApi.ReadyProduct(api, "Alpha stoneware mug set", sku: "ALPHA-1");
        var b = await ProdApi.ReadyProduct(api, "Bravo travel tumbler", sku: "BRAVO-1");
        var c = Id(await ProdApi.Create(api, "Charlie draft only"));
        await api.Post($"/api/v1/seller/products/{a}/status", new { status = "active" });
        async Task<JsonElement> List(string qs) => await api.JsonBody(await api.Get("/api/v1/seller/products" + qs));
        Assert.Equal(3, (await List("")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await List("?status=active")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await List("?q=bravo-1")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await List("?q=mug")).GetProperty("total").GetInt32() + 1);
        Assert.Equal("Alpha stoneware mug set", (await List("?sort=title&dir=asc")).GetProperty("items")[0].GetProperty("title").GetString());
        var p1 = await List("?pageSize=2&page=1&sort=title&dir=asc"); var p2 = await List("?pageSize=2&page=2&sort=title&dir=asc");
        Assert.Equal(2, p1.GetProperty("items").GetArrayLength()); Assert.Equal(1, p2.GetProperty("items").GetArrayLength()); Assert.Equal(3, p2.GetProperty("total").GetInt32());
        Assert.Contains((await List("?filter=attention")).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == c);
        Assert.Equal(HttpStatusCode.BadRequest, await api.Get("/api/v1/seller/products?status=bogus").Code());

        // inline quick edit of price and stock; the sale price must stay below the price
        var q = await api.Patch($"/api/v1/seller/products/{b}/quick", new { price = 24.5, quantity = 0 });
        Assert.Equal(HttpStatusCode.OK, q.StatusCode);
        var db1 = await ProdApi.Get(api, b);
        Assert.Equal(24.5, (double)db1["price"]!); Assert.Equal(0, (int)db1["available"]!);
        Assert.Equal(1, (await List("?filter=outofstock")).GetProperty("items").EnumerateArray().Count(i => i.GetProperty("id").GetGuid() == b));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Patch($"/api/v1/seller/products/{b}/quick", new { price = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Patch($"/api/v1/seller/products/{b}/quick", new { })).StatusCode);

        // duplicate: new draft, no SKU, images copied
        var dup = await api.Post($"/api/v1/seller/products/{a}/duplicate");
        Assert.Equal(HttpStatusCode.Created, dup.StatusCode);
        var dd = await ProdApi.Node(dup);
        Assert.Equal("Draft", (string)dd["status"]!); Assert.Null((string?)dd["sku"]); Assert.StartsWith("Copy of", (string)dd["title"]!);
        Assert.Single((JsonArray)dd["images"]!);
        Assert.NotEqual(Id(dd), a);
        var keys = f.WithDb(x => x.ProductImages.Where(i => i.ProductId == a || i.ProductId == Id(dd)).Select(i => i.StorageKey).ToList());
        Assert.Equal(2, keys.Distinct().Count());

        Assert.Equal(HttpStatusCode.OK, (await api.Patch($"/api/v1/seller/products/{b}/quick", new { quantity = 3 })).StatusCode);
        // bulk status: b is ready (activate), c isn't (reported as failed), unknown id reported
        var bulk = await api.JsonBody(await api.Post("/api/v1/seller/products/bulk-status", new { ids = new[] { b, c, Guid.NewGuid() }, status = "active" }));
        Assert.Equal(1, bulk.GetProperty("updated").GetInt32()); Assert.Equal(2, bulk.GetProperty("failed").GetArrayLength());
        var bulk2 = await api.JsonBody(await api.Post("/api/v1/seller/products/bulk-status", new { ids = new[] { a, b }, status = "inactive" }));
        Assert.Equal(2, bulk2.GetProperty("updated").GetInt32());
        var bulk3 = await api.JsonBody(await api.Post("/api/v1/seller/products/bulk-status", new { ids = new[] { a }, status = "archived" }));
        Assert.Equal(1, bulk3.GetProperty("updated").GetInt32());
        Assert.Equal(1, (await List("?status=archived")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/seller/products/bulk-status", new { ids = Array.Empty<Guid>(), status = "active" })).StatusCode);
    }

    [Fact]
    public async Task Writes_are_rate_limited()
    {
        var f2 = new ApiFactory(); f2.Extra["RateLimit:CatalogPerMinute"] = "5";
        using var _ = f2;
        var (api, _) = await ProdApi.Approved(f2, false);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++) codes.Add(await api.Get("/api/v1/seller/products").Code());
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }
}
