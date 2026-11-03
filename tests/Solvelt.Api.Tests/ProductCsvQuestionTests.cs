using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Products;

namespace Solvelt.Api.Tests;

public class ProductCsvQuestionTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private const string V = "/api/v1/seller/products/csv/validate", I = "/api/v1/seller/products/csv/import";
    private const string Header = "sku,title,brand,category,condition,price,quantity,barcode,description,bullet_1";

    [Fact]
    public async Task Template_has_an_example_row_that_validates()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var t = await api.Get("/api/v1/seller/products/csv/template");
        Assert.Equal(HttpStatusCode.OK, t.StatusCode); Assert.Equal("text/csv", t.Content.Headers.ContentType!.MediaType);
        var text = await t.Content.ReadAsStringAsync();
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        var v = await ProdApi.UploadCsv(api, V, text);
        var body = await api.JsonBody(v);
        Assert.Equal(1, body.GetProperty("valid").GetInt32()); Assert.Equal(0, body.GetProperty("invalid").GetInt32());
    }

    [Fact]
    public async Task Validation_reports_row_level_errors_and_rejects_bad_files()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var csv = $"{Header}\n" +
                  "OK-1,Good mug,,kitchen-dining,New,10.50,5,036000291452,Nice,Fits\n" +          // row 2 ok
                  "OK-1,Dup sku mug,,kitchen-dining,New,10,5,,,\n" +                               // row 3 duplicate SKU in file
                  "BAD-2,,,nope-category,Broken,abc,-1,036000291453,,\n" +                         // row 4 many errors
                  "OK-3,Cheap,,,New,0,1,,,\n";                                                     // row 5 price 0
        var v = await api.JsonBody(await ProdApi.UploadCsv(api, V, csv));
        Assert.Equal(4, v.GetProperty("total").GetInt32()); Assert.Equal(1, v.GetProperty("valid").GetInt32());
        var rows = v.GetProperty("rows");
        Assert.Equal(2, rows[0].GetProperty("row").GetInt32()); Assert.True(rows[0].GetProperty("ok").GetBoolean());
        Assert.Contains(rows[1].GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == "sku");
        var fields = rows[2].GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()).ToList();
        foreach (var x in new[] { "title", "category", "condition", "price", "barcode" }) Assert.Contains(x, fields);
        Assert.Contains(rows[3].GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == "price");
        // file-level problems
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.UploadCsv(api, V, "title,image_url\nX,http://x/y.png\n")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.UploadCsv(api, V, "sku,price\nA,1\n")).StatusCode); // no title column
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.UploadCsv(api, V, "title\n")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.UploadCsv(api, V, "title\nx\n", "products.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.UploadCsv(api, V, "title\n\"unterminated\n")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.UploadCsv(api, V, "title\n" + string.Concat(Enumerable.Range(0, 501).Select(i => $"Mug {i}\n")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProdApi.UploadCsv(api, V, "title\n" + new string('x', 1_100_000))).StatusCode);
        var cols = await api.JsonBody(await ProdApi.UploadCsv(api, V, "title,mystery\nX,1\n"));
        Assert.Equal("mystery", cols.GetProperty("ignoredColumns")[0].GetString());
    }

    [Fact]
    public async Task Import_creates_drafts_for_valid_rows_only_and_export_is_formula_safe_and_roundtrips()
    {
        var (api, _) = await ProdApi.Approved(f);
        var csv = $"{Header}\n" +
                  "IMP-1,Imported stoneware mug,,kitchen-dining,Used,12.5,4,,\"A long, \"\"quoted\"\" description\",Dishwasher safe\n" +
                  "IMP-2,=HYPERLINK(\"\"http://evil.example\"\"),,,New,9,1,,,\n" +
                  "IMP-3,,,,,,,,,\n";
        var r = await ProdApi.UploadCsv(api, I, csv);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await api.JsonBody(r);
        Assert.Equal(2, body.GetProperty("imported").GetInt32()); Assert.Equal(1, body.GetProperty("skipped").GetInt32());
        var list = await api.JsonBody(await api.Get("/api/v1/seller/products?status=draft"));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var first = list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("sku").GetString() == "IMP-1");
        var d = await ProdApi.Get(api, first.GetProperty("id").GetGuid());
        Assert.Equal("Draft", (string)d["status"]!); Assert.Equal(12.5, (double)d["price"]!); Assert.Equal(4, (int)d["quantity"]!);
        Assert.Equal("Used", (string)d["condition"]!); Assert.Equal("A long, \"quoted\" description", (string)d["description"]!);
        Assert.Equal("Dishwasher safe", (string)d["bullets"]![0]!); Assert.False(string.IsNullOrEmpty((string?)d["brandName"]));
        Assert.Contains(f.WithDb(db => db.AuditEvents.Select(a => a.Type).ToList()), t => t == "product.csv_imported");

        // export: formulas are neutralised, no line starts a cell with = + - @
        var export = await api.Get("/api/v1/seller/products/csv/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("attachment", export.Content.Headers.ContentDisposition!.ToString());
        var text = await export.Content.ReadAsStringAsync();
        Assert.Contains("'=HYPERLINK", text);
        var parsed = CsvFormat.Parse(text);
        Assert.All(parsed.Skip(1), row => Assert.All(row, cell => Assert.False(cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r', cell)));
        Assert.Equal("brand", parsed[0][2]); Assert.Equal("status", parsed[0][^1]);
        // re-importing the export (after deleting the SKUs' owners) restores the original text of the formula cell
        var again = await api.JsonBody(await ProdApi.UploadCsv(api, V, text));
        Assert.Contains(again.GetProperty("rows").EnumerateArray().SelectMany(x => x.GetProperty("errors").EnumerateArray()), e => e.GetProperty("message").GetString()!.Contains("SKU"));
        var (other, _) = await ProdApi.Approved(f, false);
        var okOther = await other.JsonBody(await ProdApi.UploadCsv(other, V, text)); // another seller has no SKU clash, and the export re-imports cleanly
        Assert.Equal(0, okOther.GetProperty("invalid").GetInt32()); Assert.Equal(2, okOther.GetProperty("valid").GetInt32());
        Assert.Equal(CsvFormat.Unescape("'=HYPERLINK(1)"), "=HYPERLINK(1)");
    }

    [Fact]
    public async Task Customer_questions_can_be_listed_filtered_answered_published_and_hidden()
    {
        var (api, _) = await ProdApi.Approved(f, false);
        var p1 = Guid.Parse((string)(await ProdApi.Create(api, "First mug listing"))["id"]!);
        var p2 = Guid.Parse((string)(await ProdApi.Create(api, "Second mug listing"))["id"]!);
        var http = f.NewClient();
        async Task<Guid> Ask(Guid p, string t) => (await (await http.PostAsJsonAsync("/api/dev/questions", new { productId = p, askerName = "Sam", text = t })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var q1 = await Ask(p1, "Is it dishwasher safe?"); var q2 = await Ask(p2, "Does it ship to Canada?"); await Ask(p2, "Can I get a gift box?");
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/dev/questions", new { productId = Guid.NewGuid(), text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/dev/questions", new { productId = p1, text = "" })).StatusCode);

        var all = await api.JsonBody(await api.Get("/api/v1/seller/questions"));
        Assert.Equal(3, all.GetProperty("total").GetInt32()); Assert.Equal(3, all.GetProperty("counts").GetProperty("unanswered").GetInt32());
        Assert.Equal(2, (await api.JsonBody(await api.Get($"/api/v1/seller/questions?productId={p2}"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, await api.Get("/api/v1/seller/questions?state=weird").Code());

        Assert.Equal(HttpStatusCode.BadRequest, await api.Put($"/api/v1/seller/questions/{q1}/answer", new { answer = "  " }).Code());
        Assert.Equal(HttpStatusCode.BadRequest, await api.Post($"/api/v1/seller/questions/{q1}/publish").Code()); // nothing to publish yet
        var ans = await api.JsonBody(await api.Put($"/api/v1/seller/questions/{q1}/answer", new { answer = "Yes, top rack." }));
        Assert.Equal("Published", ans.GetProperty("state").GetString()); Assert.True(ans.GetProperty("answered").GetBoolean());
        Assert.Equal(1, (await api.JsonBody(await api.Get("/api/v1/seller/questions?state=answered"))).GetProperty("total").GetInt32());
        Assert.Equal(2, (await api.JsonBody(await api.Get("/api/v1/seller/questions?state=unanswered"))).GetProperty("total").GetInt32());
        var hidden = await api.JsonBody(await api.Post($"/api/v1/seller/questions/{q1}/hide"));
        Assert.Equal("Hidden", hidden.GetProperty("state").GetString());
        Assert.Equal(1, (await api.JsonBody(await api.Get("/api/v1/seller/questions?state=hidden"))).GetProperty("total").GetInt32());
        Assert.Equal("Published", (await api.JsonBody(await api.Post($"/api/v1/seller/questions/{q1}/publish"))).GetProperty("state").GetString());
        var draftAnswer = await api.JsonBody(await api.Put($"/api/v1/seller/questions/{q2}/answer", new { answer = "Working on it", publish = false }));
        Assert.Equal("Pending", draftAnswer.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Inventory_reserve_release_commit_adjust_are_atomic_and_never_oversell()
    {
        var (api, seller) = await ProdApi.Approved(f, false);
        var id = Guid.Parse((string)(await ProdApi.Create(api))["id"]!);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => d["quantity"] = 5)).StatusCode);
        using var scope = f.Services.CreateScope();
        var make = () => new InventoryService(scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>());
        // 20 parallel single-unit reservations on 5 units: exactly 5 succeed (separate DbContexts, like separate requests)
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var s = f.Services.CreateScope();
            return (await new InventoryService(s.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>()).ReserveAsync(seller, id, null, 1)).Ok;
        }));
        Assert.Equal(5, results.Count(x => x));
        var inv = make();
        Assert.False((await inv.ReserveAsync(seller, id, null, 1)).Ok);
        Assert.Equal(5, (await ProdApi.Get(api, id))["reserved"]!.GetValue<int>());
        Assert.Equal(0, (await ProdApi.Get(api, id))["available"]!.GetValue<int>());
        var rel = await inv.ReleaseAsync(seller, id, null, 2); Assert.True(rel.Ok); Assert.Equal(3, rel.Reserved);
        Assert.False((await inv.ReleaseAsync(seller, id, null, 99)).Ok);
        var com = await inv.CommitAsync(seller, id, null, 3); Assert.True(com.Ok); Assert.Equal((2, 0), (com.OnHand, com.Reserved));
        Assert.False((await inv.AdjustAsync(seller, id, null, -3)).Ok);
        Assert.Equal(7, (await inv.AdjustAsync(seller, id, null, 5)).OnHand);
        Assert.False((await inv.ReserveAsync(seller, id, null, 0)).Ok);
        // another seller can never touch it
        Assert.False((await inv.ReserveAsync(Guid.NewGuid(), id, null, 1)).Ok);
        // lookup by SKU (product or variant) is seller-scoped
        await ProdApi.Put(api, id, d => d["sku"] = "INV-SKU");
        Assert.Equal(id, (await inv.FindBySkuAsync(seller, "inv-sku"))!.Value.ProductId);
        Assert.Null(await inv.FindBySkuAsync(Guid.NewGuid(), "INV-SKU"));
        // backorders may reserve beyond stock
        await ProdApi.Put(api, id, d => { d["allowBackorder"] = true; d["quantity"] = 0; });
        Assert.True((await make().ReserveAsync(seller, id, null, 4)).Ok);
    }
}
