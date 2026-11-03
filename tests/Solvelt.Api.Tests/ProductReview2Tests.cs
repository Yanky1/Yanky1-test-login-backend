using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;

namespace Solvelt.Api.Tests;

/// <summary>Second review round: stock write race, CSV unique-index path, SKU swap.</summary>
public class ProductReview2Tests(ApiFactory f) : IClassFixture<ApiFactory>
{
    /// <summary>Simulates an order reserving stock in the window between the API's pre-check and its stock write.</summary>
    private sealed class RacingInventory(InventoryService inner, int reserveFirst) : IInventoryService
    {
        public Task<InventoryResult> ReserveAsync(Guid s, Guid p, Guid? v, int q, CancellationToken ct = default) => inner.ReserveAsync(s, p, v, q, ct);
        public Task<InventoryResult> ReleaseAsync(Guid s, Guid p, Guid? v, int q, CancellationToken ct = default) => inner.ReleaseAsync(s, p, v, q, ct);
        public Task<InventoryResult> CommitAsync(Guid s, Guid p, Guid? v, int q, CancellationToken ct = default) => inner.CommitAsync(s, p, v, q, ct);
        public Task<InventoryResult> AdjustAsync(Guid s, Guid p, Guid? v, int d, CancellationToken ct = default) => inner.AdjustAsync(s, p, v, d, ct);
        public async Task<InventoryResult> SetOnHandAsync(Guid s, Guid p, Guid? v, int n, CancellationToken ct = default)
        { await inner.ReserveAsync(s, p, v, reserveFirst, ct); return await inner.SetOnHandAsync(s, p, v, n, ct); }
        public Task<(Guid ProductId, Guid? VariantId)?> FindBySkuAsync(Guid s, string sku, CancellationToken ct = default) => inner.FindBySkuAsync(s, sku, ct);
    }

    private sealed class RacingFactory(int reserveFirst) : ApiFactory
    {
        protected override void ConfigureTestServices(IServiceCollection s)
        {
            s.RemoveAll<IInventoryService>();
            s.AddScoped<IInventoryService>(sp => new RacingInventory(new InventoryService(sp.GetRequiredService<AppDbContext>()), reserveFirst));
        }
    }

    [Fact]
    public async Task Item5_a_reservation_between_check_and_write_is_a_409_and_nothing_is_half_applied()
    {
        using var racing = new RacingFactory(5);
        var (api, _) = await ProdApi.Approved(racing);
        var id = await ProdApi.ReadyProduct(api);                                        // stock 10, nothing reserved yet
        // main PUT: the pre-check passes, then an "order" reserves 5 right before the stock write
        var r = await ProdApi.Put(api, id, d => { d["title"] = "Half applied?"; d["quantity"] = 0; });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("below_reserved", await r.ErrCode());
        var d1 = await ProdApi.Get(api, id);
        Assert.Equal("Speckled stoneware coffee mug", (string)d1["title"]!);            // the document save was rolled back with it
        Assert.Equal(10, (int)d1["quantity"]!); Assert.Equal(0, (int)d1["reserved"]!);   // and so was the simulated reservation (same transaction)
        var v1 = (int)d1["version"]!;
        // quick edit: same behaviour
        var q = await api.Patch($"/api/v1/seller/products/{id}/quick", new { version = v1, quantity = 0, price = 7 });
        Assert.Equal(HttpStatusCode.Conflict, q.StatusCode); Assert.Equal("below_reserved", await q.ErrCode());
        var d2 = await ProdApi.Get(api, id);
        Assert.Equal(19.99, (double)d2["price"]!); Assert.Equal(v1, (int)d2["version"]!);
        // variations: a variation quantity change hits the same race
        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/v1/seller/products/{id}/variants", new
        { hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S" } } }, variants = new[] { new { values = new[] { "S" }, sku = (string?)null, price = 10, quantity = 8, barcode = (string?)null, enabled = true } } })).StatusCode);
        var vr = await api.Put($"/api/v1/seller/products/{id}/variants", new
        { hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S" } } }, variants = new[] { new { values = new[] { "S" }, sku = (string?)null, price = 10, quantity = 1, barcode = (string?)null, enabled = true } } });
        Assert.Equal(HttpStatusCode.Conflict, vr.StatusCode); Assert.Equal("below_reserved", await vr.ErrCode());
        Assert.Equal(8, (int)(await ProdApi.Get(api, id))["variants"]![0]!["quantity"]!);
    }

    [Fact]
    public async Task Item8_csv_import_reports_a_sku_taken_after_validation_as_a_row_error_not_a_500()
    {
        var (api, seller) = await ProdApi.Approved(f, false);
        // another request takes the SKU between validation and the first save (the DB unique index is the only thing that can catch this)
        CsvEndpoints.AfterValidate = async () =>
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pid = Guid.NewGuid();
            db.Products.Add(new Product { Id = pid, SellerId = seller, BrandId = db.Brands.First(b => b.SellerId == seller).Id, Title = "Grabbed it first", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            db.SkuClaims.Add(new SkuClaim { SellerId = seller, ProductId = pid, Sku = "late-sku" });
            await db.SaveChangesAsync();
        };
        try
        {
            var res = await ProdApi.UploadCsv(api, "/api/v1/seller/products/csv/import", "sku,title\nLATE-SKU,Loses the race\nOK-1,Fine row\n");
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var body = await api.JsonBody(res);
            Assert.Equal(1, body.GetProperty("imported").GetInt32()); Assert.Equal(1, body.GetProperty("skipped").GetInt32());
            var err = body.GetProperty("rows")[0];
            Assert.Equal("sku", err.GetProperty("errors")[0].GetProperty("field").GetString());
            Assert.Equal(1, f.WithDb(db => db.SkuClaims.Count(c => c.SellerId == seller && c.Sku == "ok-1")));
        }
        finally { CsvEndpoints.AfterValidate = null; }
    }

    [Fact]
    public async Task ItemA_two_variations_can_swap_their_skus_in_one_request()
    {
        var (api, _) = await ProdApi.Approved(f);
        var id = await ProdApi.ReadyProduct(api);
        object Body(string s, string m) => new
        {
            hasVariants = true, options = new[] { new { name = "Size", values = new[] { "S", "M" } } },
            variants = new[] { new { values = new[] { "S" }, sku = s, price = 10, quantity = 3, barcode = (string?)null, enabled = true }, new { values = new[] { "M" }, sku = m, price = 10, quantity = 3, barcode = (string?)null, enabled = true } }
        };
        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/v1/seller/products/{id}/variants", Body("SWAP-A", "SWAP-B"))).StatusCode);
        var swapped = await api.Put($"/api/v1/seller/products/{id}/variants", Body("SWAP-B", "SWAP-A"));
        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        var vs = (JsonArray)(await ProdApi.Node(swapped))["variants"]!;
        Assert.Equal("SWAP-B", (string)vs[0]!["sku"]!); Assert.Equal("SWAP-A", (string)vs[1]!["sku"]!);
        Assert.Equal(2, f.WithDb(db => db.SkuClaims.Count(c => c.ProductId == id)));
        // and a failed save leaves the claims exactly as they were (the delete is in the same transaction)
        var bad = await api.Put($"/api/v1/seller/products/{id}/variants", Body("NEW-1", "NEW-1"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(new[] { "swap-a", "swap-b" }, f.WithDb(db => db.SkuClaims.Where(c => c.ProductId == id).Select(c => c.Sku).OrderBy(x => x).ToArray()));
    }
}
