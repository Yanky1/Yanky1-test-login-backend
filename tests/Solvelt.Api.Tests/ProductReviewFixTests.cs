using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the products review (items 1-8).</summary>
public class ProductReviewFixTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static async Task<int> Ver(Api api, Guid id) => (int)(await ProdApi.Get(api, id))["version"]!;
    private static object OneVariant(bool enabled, string? sku = null, int qty = 3, string? barcode = null) => new
    {
        hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S" } } },
        variants = new[] { new { values = new[] { "S" }, sku, price = 10, quantity = qty, barcode, enabled } }
    };

    [Fact]
    public async Task Item1_variations_cannot_make_an_active_listing_unpublishable()
    {
        var (api, _) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api);
        Assert.Equal(HttpStatusCode.OK, await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" }).Code());
        var r = await api.Put($"/api/v1/seller/products/{id}/variants", OneVariant(enabled: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); Assert.Equal("not_publishable", await r.ErrCode());
        var d = await ProdApi.Get(api, id);
        Assert.False((bool)d["hasVariants"]!); Assert.Empty((JsonArray)d["variants"]!); Assert.Equal("Active", (string)d["status"]!); Assert.True((bool)d["canPublish"]!);
        // a variation change that keeps it publishable is fine
        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/v1/seller/products/{id}/variants", OneVariant(enabled: true))).StatusCode);
    }

    [Fact]
    public async Task Item2_overlong_barcode_is_a_400_not_a_conflict_and_unique_violations_are_400s()
    {
        var (api, _) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api);
        var r = await ProdApi.Put(api, id, d => { d["barcode"] = new string('1', 21); d["noBarcode"] = false; });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/seller/products/{id}/variants", OneVariant(true, barcode: new string('2', 21)))).StatusCode);
        // the draft is still saveable afterwards (no false 409)
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => d["title"] = "Still saves fine after the 400")).StatusCode);
        // a SKU that is already taken (case-insensitively) is a 400 field error, never a 409 version conflict. The database-level race
        // (unique index hit after the pre-check) is covered by Item3's parallel test and Item8's CSV test.
        await ProdApi.ReadyProduct(api, "Other mug listing", sku: "FIRST");
        var res = await ProdApi.Put(api, id, d => d["sku"] = "first");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode); Assert.NotEqual("version_conflict", await res.ErrCode());
    }

    [Fact]
    public async Task Item3_sku_uniqueness_is_case_insensitive_shared_with_variations_and_race_safe()
    {
        var (api, _) = await ProdApi.Approved(f);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++) ids.Add(await ProdApi.ReadyProduct(api, $"Race mug number {i}"));
        var skus = new[] { "RACE", "race", "RaCe", "rACE " };
        var codes = await Task.WhenAll(ids.Select((id, i) => ProdApi.Put(api, id, d => d["sku"] = skus[i]).ContinueWith(t => t.Result.StatusCode)));
        Assert.Single(codes, HttpStatusCode.OK);
        Assert.All(codes.Where(c => c != HttpStatusCode.OK), c => Assert.Equal(HttpStatusCode.BadRequest, c));
        Assert.Equal(1, f.WithDb(db => db.SkuClaims.Count(c => c.Sku == "race")));

        // a product can't take its own variation's SKU, and a variation can't take another product's SKU
        var p = await ProdApi.ReadyProduct(api, "Own sku mug", sku: "OWN-1");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/seller/products/{p}/variants", OneVariant(true, sku: "own-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/v1/seller/products/{p}/variants", OneVariant(true, sku: "OWN-1-S"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Put(api, p, d => d["sku"] = "own-1-s")).StatusCode);
        var q = await ProdApi.ReadyProduct(api, "Another sku mug");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/seller/products/{q}/variants", OneVariant(true, sku: "OWN-1-S"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.Put(api, q, d => d["sku"] = "OWN-1-s")).StatusCode);
        // freeing it (removing the variation SKU) lets another product have it; deleting a product frees its claims
        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/v1/seller/products/{p}/variants", OneVariant(true, sku: null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, q, d => d["sku"] = "OWN-1-S")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, await api.Delete($"/api/v1/seller/products/{q}").Code());
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, p, d => d["sku"] = "own-1-s")).StatusCode);

        // CSV: a case-variant of an existing SKU is a row error
        var v = await api.JsonBody(await ProdApi.UploadCsv(api, "/api/v1/seller/products/csv/validate", "sku,title\nown-1-S,Case variant csv mug\nBRAND-NEW,Fresh csv mug\n"));
        Assert.Equal(1, v.GetProperty("valid").GetInt32());
        Assert.Contains(v.GetProperty("rows")[0].GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == "sku");
        // ... and the import reports it per row instead of failing
        var imp = await api.JsonBody(await ProdApi.UploadCsv(api, "/api/v1/seller/products/csv/import", "sku,title\nown-1-S,Case variant csv mug\nBRAND-NEW,Fresh csv mug\n"));
        Assert.Equal(1, imp.GetProperty("imported").GetInt32()); Assert.Equal(1, imp.GetProperty("skipped").GetInt32());
        Assert.Equal(1, f.WithDb(db => db.SkuClaims.Count(c => c.Sku == "brand-new")));
    }

    [Fact]
    public async Task Item4_parallel_uploads_cannot_exceed_nine_images()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var id = Guid.Parse((string)(await ProdApi.Create(api))["id"]!);
        for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(api, id, TestImages.Png(), $"{i}.png")).StatusCode);
        var codes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => ProdApi.Upload(api, id, TestImages.Png(), $"p{i}.png").ContinueWith(t => t.Result.StatusCode)));
        Assert.InRange(f.WithDb(db => db.ProductImages.Count(x => x.ProductId == id && x.VariantId == null)), 9, 9);
        Assert.Single(codes, HttpStatusCode.Created);
        Assert.All(codes.Where(c => c != HttpStatusCode.Created), c => Assert.True(c is HttpStatusCode.Conflict or HttpStatusCode.BadRequest));
        // and the numbering stays sane
        var imgs = (JsonArray)(await ProdApi.Get(api, id))["images"]!;
        Assert.Equal(9, imgs.Count); Assert.Single(imgs.Where(i => (bool)i!["isMain"]!));
    }

    [Fact]
    public async Task Item5_on_hand_can_never_drop_below_reserved()
    {
        var (api, seller) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => d["quantity"] = 10)).StatusCode);
        using var scope = f.Services.CreateScope();
        var inv = new InventoryService(scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>());
        Assert.True((await inv.ReserveAsync(seller, id, null, 10)).Ok);
        var adj = await inv.AdjustAsync(seller, id, null, -9);
        Assert.False(adj.Ok); Assert.Equal("below_reserved", adj.Error); Assert.Equal((10, 10), (adj.OnHand, adj.Reserved));
        var set = await inv.SetOnHandAsync(seller, id, null, 0);
        Assert.False(set.Ok); Assert.Equal("below_reserved", set.Error);
        Assert.True((await inv.AdjustAsync(seller, id, null, 5)).Ok);                   // raising is fine
        Assert.True((await inv.SetOnHandAsync(seller, id, null, 10)).Ok);               // exactly the reserved amount is fine
        Assert.Equal("not_found", (await inv.AdjustAsync(seller, Guid.NewGuid(), null, 1)).Error);
        // the API refuses it too, with an explanation, and the stock is unchanged
        var q = await api.Patch($"/api/v1/seller/products/{id}/quick", new { version = await Ver(api, id), quantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, q.StatusCode); Assert.Contains("reserved by open orders", await q.Content.ReadAsStringAsync());
        var put = await ProdApi.Put(api, id, d => d["quantity"] = 3);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(10, (int)(await ProdApi.Get(api, id))["quantity"]!);
        // variations: same rule per variation
        await api.Put($"/api/v1/seller/products/{id}/variants", OneVariant(true, qty: 5));
        var vid = f.WithDb(db => db.Variants.Where(v => v.ProductId == id).Select(v => v.Id).Single());
        Assert.True((await inv.ReserveAsync(seller, id, vid, 4)).Ok);
        Assert.False((await inv.SetOnHandAsync(seller, id, vid, 3)).Ok);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/seller/products/{id}/variants", OneVariant(true, qty: 2))).StatusCode);
    }

    [Fact]
    public async Task Item6_quick_edit_requires_the_version()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var id = Guid.Parse((string)(await ProdApi.Create(api))["id"]!);
        var r = await api.Patch($"/api/v1/seller/products/{id}/quick", new { price = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Assert.Contains("version", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await api.Patch($"/api/v1/seller/products/{id}/quick", new { version = await Ver(api, id), price = 5 })).StatusCode);
    }

    [Fact]
    public void Item7_bytes_after_the_end_of_an_image_and_unknown_chunks_are_dropped()
    {
        var jpeg = TestImages.Jpeg().Concat(Encoding.ASCII.GetBytes("<script>alert(1)</script>")).ToArray();
        var j = ImageProcessing.Process(jpeg)!;
        Assert.DoesNotContain("script", Encoding.Latin1.GetString(j.Data)); Assert.Equal((0xFF, 0xD9), (j.Data[^2], j.Data[^1]));
        Assert.Null(ImageProcessing.Process(TestImages.Jpeg()[..^2])); // no end-of-image marker -> not a valid JPEG
        var png = TestImages.Png(); var iend = png.Length - 12;
        var custom = png[..iend].Concat(new byte[] { 0, 0, 0, 4 }).Concat(Encoding.ASCII.GetBytes("prVt")).Concat(Encoding.ASCII.GetBytes("EVIL")).Concat(new byte[4]).Concat(png[iend..]).Concat(Encoding.ASCII.GetBytes("<script>")).ToArray();
        var p = ImageProcessing.Process(custom)!;
        var text = Encoding.Latin1.GetString(p.Data);
        Assert.DoesNotContain("EVIL", text); Assert.DoesNotContain("script", text); Assert.Contains("IDAT", text); Assert.Contains("IEND", text);
    }
}
