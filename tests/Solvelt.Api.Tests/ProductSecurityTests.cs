using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solvelt.Api.Tests;

public class ProductSecurityTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private async Task<Guid> Ask(Guid productId, string text = "Does it fit a standard cup holder?")
    {
        var r = await f.NewClient().PostAsJsonAsync("/api/dev/questions", new { productId, askerName = "Sam", text });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid product, Guid image, Guid variant)> Rich(Api owner)
    {
        var p = await ProdApi.ReadyProduct(owner, sku: "OWN-" + Guid.NewGuid().ToString("N")[..6]);
        var img = (await ProdApi.Get(owner, p))["images"]![0]!["id"]!.GetValue<string>();
        var v = await ProdApi.Node(await owner.Put($"/api/v1/seller/products/{p}/variants", new
        { hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S" } } }, variants = Array.Empty<object>() }));
        return (p, Guid.Parse(img), Guid.Parse((string)v["variants"]![0]!["id"]!));
    }

    [Fact]
    public async Task Another_seller_gets_404_on_every_product_question_and_image_endpoint()
    {
        var (a, _) = await ProdApi.Approved(f);
        var (b, _) = await ProdApi.Approved(f);
        var (p, img, v) = await Rich(a);
        var q = await Ask(p);
        var pu = $"/api/v1/seller/products/{p}";
        var png = TestImages.Png();

        Assert.Equal(HttpStatusCode.NotFound, await b.Get(pu).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Get($"{pu}/readiness").Code());
        var body = JsonSerializer.Deserialize<JsonElement>((await ProdApi.Get(a, p)).ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, await b.Put(pu, body).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Patch($"{pu}/quick", new { price = 1 }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Post($"{pu}/status", new { status = "active" }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Post($"{pu}/duplicate").Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Delete(pu).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Get($"{pu}/images/{img}/file").Code());
        Assert.Equal(HttpStatusCode.NotFound, (await ProdApi.Upload(b, p, png)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ProdApi.Upload(b, p, png, variantId: v)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await b.Put($"{pu}/images/order", new { ids = new[] { img } }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Put($"{pu}/images/{img}", new { alt = "x", isMain = true }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Delete($"{pu}/images/{img}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Put($"{pu}/variants", new { hasVariants = false, options = Array.Empty<object>(), variants = Array.Empty<object>() }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Put($"{pu}/faqs", new[] { new { question = "q", answer = "a" } }).Code());
        // questions
        Assert.Equal(HttpStatusCode.NotFound, await b.Put($"/api/v1/seller/questions/{q}/answer", new { answer = "Hijacked" }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Post($"/api/v1/seller/questions/{q}/hide").Code());
        Assert.Equal(HttpStatusCode.NotFound, await b.Post($"/api/v1/seller/questions/{q}/publish").Code());
        // lists and exports never include A's data; A's data is untouched
        Assert.Equal(0, (await b.JsonBody(await b.Get("/api/v1/seller/products"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await b.JsonBody(await b.Get("/api/v1/seller/questions"))).GetProperty("total").GetInt32());
        Assert.DoesNotContain(p.ToString(), await (await b.Get("/api/v1/seller/products?q=speckled")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("Speckled", await (await b.Get("/api/v1/seller/products/csv/export")).Content.ReadAsStringAsync());
        Assert.Equal(1, (await a.JsonBody(await a.Get("/api/v1/seller/questions?state=unanswered"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, await a.Get(pu).Code());
        // cannot create into / move onto A's brand
        var aBrand = await TeamHelpers.BrandId(a);
        Assert.Equal(HttpStatusCode.NotFound, await b.Post("/api/v1/seller/products", new { title = "Sneaky listing", brandId = aBrand }).Code());
        var mine = await ProdApi.Create(b);
        var mineId = Guid.Parse((string)mine["id"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await ProdApi.Put(b, mineId, d => d["brandId"] = aBrand)).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_and_unverified_callers_are_rejected_everywhere()
    {
        var anon = new Api(f);
        foreach (var url in new[] { "/api/v1/seller/products", "/api/v1/seller/questions", "/api/v1/categories", "/api/v1/seller/products/csv/export", "/api/v1/seller/products/csv/template" })
            Assert.Equal(HttpStatusCode.Unauthorized, await anon.Get(url, auth: false).Code());
        Assert.Equal(HttpStatusCode.Unauthorized, await anon.Post("/api/v1/seller/products", new { title = "x" }, false).Code());
        var unverified = new Api(f); await unverified.UnverifiedSession();
        Assert.Equal(HttpStatusCode.Forbidden, await unverified.Get("/api/v1/seller/products").Code());
    }

    [Fact]
    public async Task Viewer_is_read_only_and_manager_is_limited_to_their_brands()
    {
        var (owner, _) = await ProdApi.Approved(f);
        var brandA = await TeamHelpers.BrandId(owner);
        var brandB = await TeamHelpers.NewBrand(owner, "Second Brand");
        var pa = await ProdApi.ReadyProduct(owner, "Alpha brand speckled mug", brandId: brandA);
        var pb = await ProdApi.ReadyProduct(owner, "Beta brand speckled mug", brandId: brandB);
        var qb = await Ask(pb); var qa = await Ask(pa);

        // ---- Viewer (all brands): reads, never writes
        var (viewer, _) = await TeamHelpers.JoinAs(f, owner, "Viewer");
        Assert.Equal(2, (await viewer.JsonBody(await viewer.Get("/api/v1/seller/products"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, await viewer.Get($"/api/v1/seller/products/{pa}").Code());
        Assert.Equal(HttpStatusCode.OK, await viewer.Get($"/api/v1/seller/products/{pa}/images/{(await ProdApi.Get(owner, pa))["images"]![0]!["id"]}/file").Code());
        Assert.Equal(HttpStatusCode.OK, await viewer.Get("/api/v1/seller/products/csv/export").Code());
        Assert.Equal(HttpStatusCode.OK, await viewer.Get("/api/v1/categories").Code());
        Assert.Equal(HttpStatusCode.OK, await viewer.Get("/api/v1/seller/questions").Code());
        var body = JsonSerializer.Deserialize<JsonElement>((await ProdApi.Get(owner, pa)).ToJsonString());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Post("/api/v1/seller/products", new { title = "Viewer listing" }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Put($"/api/v1/seller/products/{pa}", body).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Patch($"/api/v1/seller/products/{pa}/quick", new { price = 1 }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Post($"/api/v1/seller/products/{pa}/status", new { status = "active" }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Post("/api/v1/seller/products/bulk-status", new { ids = new[] { pa }, status = "archived" }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Post($"/api/v1/seller/products/{pa}/duplicate").Code());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Delete($"/api/v1/seller/products/{pa}").Code());
        Assert.Equal(HttpStatusCode.Forbidden, (await ProdApi.Upload(viewer, pa, TestImages.Png())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Put($"/api/v1/seller/products/{pa}/faqs", new[] { new { question = "q", answer = "a" } }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Put($"/api/v1/seller/questions/{qa}/answer", new { answer = "Nope" }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, (await ProdApi.UploadCsv(viewer, "/api/v1/seller/products/csv/import", "title\nX\n")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ProdApi.UploadCsv(viewer, "/api/v1/seller/products/csv/validate", "title\nX\n")).StatusCode);

        // ---- Manager scoped to brand A only
        var (mgr, _) = await TeamHelpers.JoinAs(f, owner, "Manager", [brandA]);
        var list = await mgr.JsonBody(await mgr.Get("/api/v1/seller/products"));
        Assert.Equal(1, list.GetProperty("total").GetInt32()); Assert.Equal(pa, list.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, await mgr.Get($"/api/v1/seller/products/{pa}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await mgr.Get($"/api/v1/seller/products/{pb}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await mgr.Post($"/api/v1/seller/products/{pb}/status", new { status = "archived" }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await mgr.Delete($"/api/v1/seller/products/{pb}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await mgr.Put($"/api/v1/seller/products/{pb}/faqs", Array.Empty<object>()).Code());
        Assert.Equal(HttpStatusCode.NotFound, (await ProdApi.Upload(mgr, pb, TestImages.Png())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await mgr.Post("/api/v1/seller/products", new { title = "Wrong brand listing", brandId = brandB }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await mgr.Put($"/api/v1/seller/questions/{qb}/answer", new { answer = "Out of scope" }).Code());
        Assert.Equal(1, (await mgr.JsonBody(await mgr.Get("/api/v1/seller/questions"))).GetProperty("total").GetInt32());
        Assert.DoesNotContain("Beta brand", await (await mgr.Get("/api/v1/seller/products/csv/export")).Content.ReadAsStringAsync());
        // in-scope writes work; the brand is picked automatically when the scope has exactly one
        var made = await mgr.Post("/api/v1/seller/products", new { title = "Manager made mug" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal(brandA, (await ProdApi.Node(made))["brandId"]!.GetValue<Guid>());
        Assert.Equal(HttpStatusCode.OK, await mgr.Put($"/api/v1/seller/questions/{qa}/answer", new { answer = "Yes, it does." }).Code());
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(mgr, pa, d => d["title"] = "Alpha brand speckled mug v2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ProdApi.Put(mgr, pa, d => d["brandId"] = brandB)).StatusCode); // can't move into an out-of-scope brand
        // CSV: rows for a brand outside the scope are rejected row by row
        var csv = await ProdApi.UploadCsv(mgr, "/api/v1/seller/products/csv/validate", "title,brand\nIn scope mug,Main brand\nOut of scope mug,Second Brand\n");
        var rows = (await mgr.JsonBody(csv)).GetProperty("rows");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.True(rows[1].GetProperty("errors").EnumerateArray().Any(e => e.GetProperty("field").GetString() == "brand"));

        // ---- Admin has full access
        var (admin, _) = await TeamHelpers.JoinAs(f, owner, "Admin");
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/seller/products/{pb}/status", new { status = "archived" }).Code());
        // removal applies on the very next request
        var members = await owner.JsonBody(await owner.Get("/api/v1/team/members"));
        var mgrMember = members.EnumerateArray().First(m => m.GetProperty("role").GetString() == "Manager").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, await owner.Delete($"/api/v1/team/members/{mgrMember}").Code());
        Assert.NotEqual(HttpStatusCode.OK, await mgr.Get($"/api/v1/seller/products/{pa}").Code());
    }

    [Fact]
    public async Task Pending_seller_has_draft_access_but_account_status_gates_activation_for_members_too()
    {
        var (owner, _) = await ProdApi.Approved(f, approve: false);
        var (mgr, _) = await TeamHelpers.JoinAs(f, owner, "Manager");
        var p = await ProdApi.ReadyProduct(mgr);
        var r = await mgr.Post($"/api/v1/seller/products/{p}/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode); Assert.Equal("seller_not_approved", await r.ErrCode());
    }
}
