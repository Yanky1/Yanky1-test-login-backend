using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Products;

public record CreateProductRequest(string? Title, Guid? BrandId, Guid? CategoryId);
public record StatusRequest(string? Status);
public record BulkStatusRequest(List<Guid>? Ids, string? Status);
public record QuickEditRequest(int? Version, decimal? Price, int? Quantity);

public static class ProductEndpoints
{
    internal static IResult Problem(int status, string code, string title, object? extra = null) =>
        Results.Json(new { type = "about:blank", title, status, code, extra }, statusCode: status);

    internal static IResult Conflict() => Problem(409, "version_conflict", "This listing was changed somewhere else. Reload to see the latest version.");

    internal static async Task<string?> BrandNameAsync(AppDbContext db, Guid brandId) =>
        await db.Brands.AsNoTracking().Where(b => b.Id == brandId).Select(b => b.Name).FirstOrDefaultAsync();

    internal static async Task<IResult> OkDetail(AppDbContext db, CategoryService cats, Product p, TimeProvider time, int status = 200)
    {
        var attrs = await cats.AttributesAsync(p.CategoryId);
        var r = ProductRules.Evaluate(p, attrs);
        return Results.Json(ProductService.Detail(p, r, time.UtcNow(), await BrandNameAsync(db, p.BrandId), attrs), ProductJson.Opts, statusCode: status);
    }

    /// <summary>Saves, translating a lost optimistic-concurrency race into 409.</summary>
    internal static async Task<bool> TrySaveAsync(AppDbContext db)
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return false; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }

    private static async Task<(Brand? Brand, IResult? Error)> ResolveBrandAsync(AppDbContext db, AccountAccess a, Guid? requested)
    {
        if (requested is { } id)
        {
            var b = await db.RequireBrandAccess(a, id, write: true);
            return b is null || b.Status != BrandStatus.Active ? (null, Results.NotFound()) : (b, null);
        }
        var all = (await db.BrandsFor(a).Where(b => b.Status == BrandStatus.Active).OrderBy(b => b.Name).ToListAsync()).Where(b => a.CanWorkOnBrand(b.Id, true)).ToList();
        var pick = all.Count == 1 ? all[0] : all.FirstOrDefault(b => b.IsDefault);
        return pick is null ? (null, Validation.Invalid("brandId", "Choose a brand for this product.")) : (pick, null);
    }

    public static void MapProductEndpoints(this RouteGroupBuilder g)
    {
        var p = g.MapGroup("/seller/products").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("catalog");

        // ---- list --------------------------------------------------------------------------------------------------------
        p.MapGet("", async (HttpContext ctx, AppDbContext db, TimeProvider time, string? q, string? status, string? filter, Guid? brandId, Guid? categoryId,
            string? sort, string? dir, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!;
            var now = time.UtcNow();
            var all = db.ProductsFor(a).AsNoTracking();
            var query = all;
            if (brandId is { } bid) query = query.Where(x => x.BrandId == bid);
            if (categoryId is { } cid) query = query.Where(x => x.CategoryId == cid);
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<ProductStatus>(status, true, out var st)) return Validation.Invalid("status", "Unknown status.");
                query = query.Where(x => x.Status == st);
            }
            else query = query.Where(x => x.Status != ProductStatus.Archived);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var s = q.Trim().ToLower();
                if (s.Length > 100) s = s[..100];
                query = query.Where(x => x.Title.ToLower().Contains(s) || (x.Sku != null && x.Sku.ToLower().Contains(s)) || x.Variants.Any(v => v.Sku != null && v.Sku.ToLower().Contains(s)));
            }
            if (filter == "outofstock")
                query = query.Where(x => x.Status != ProductStatus.Archived && !x.AllowBackorder &&
                    (x.HasVariants ? !x.Variants.Any(v => v.Enabled && v.Stock - v.Reserved > 0) : x.Stock - x.Reserved <= 0));
            else if (filter == "attention") query = query.Where(x => x.Status != ProductStatus.Archived && (!x.IsReady || x.QualityScore < 60));
            var total = await query.CountAsync();
            var desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) || (dir is null && sort is null or "updated");
            IOrderedQueryable<Product> ordered = (sort?.ToLowerInvariant()) switch
            {
                "title" => desc ? query.OrderByDescending(x => x.Title) : query.OrderBy(x => x.Title),
                "price" => desc ? query.OrderByDescending(x => x.PriceCents) : query.OrderBy(x => x.PriceCents),
                "stock" => desc ? query.OrderByDescending(x => x.HasVariants ? x.Variants.Sum(v => v.Stock) : x.Stock) : query.OrderBy(x => x.HasVariants ? x.Variants.Sum(v => v.Stock) : x.Stock),
                "quality" => desc ? query.OrderByDescending(x => x.QualityScore) : query.OrderBy(x => x.QualityScore),
                _ => desc ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt)
            };
            var size = Math.Clamp(pageSize ?? 25, 1, 100); var pg = Math.Max(page ?? 1, 1);
            var rows = await ordered.ThenBy(x => x.Id).Skip((pg - 1) * size).Take(size).Select(x => new
            {
                x.Id, x.Title, x.Sku, x.Status, x.BrandId, x.CategoryId, x.PriceCents, x.SaleCents, x.SaleStart, x.SaleEnd, x.HasVariants, x.QualityScore, x.IsReady, x.Version, x.UpdatedAt,
                x.AllowBackorder, x.LowStockAlert,
                Stock = x.HasVariants ? x.Variants.Where(v => v.Enabled).Sum(v => v.Stock - v.Reserved) : x.Stock - x.Reserved,
                Variants = x.Variants.Count,
                Thumb = x.Images.Where(i => i.VariantId == null).OrderBy(i => i.SortOrder).Select(i => (Guid?)i.Id).FirstOrDefault()
            }).ToListAsync();
            var brandIds = rows.Select(r => r.BrandId).Distinct().ToList();
            var brands = await db.Brands.AsNoTracking().Where(b => brandIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name);
            var counts = await all.GroupBy(x => x.Status).Select(gr => new { gr.Key, N = gr.Count() }).ToListAsync();
            var outOfStock = await all.CountAsync(x => x.Status != ProductStatus.Archived && !x.AllowBackorder &&
                (x.HasVariants ? !x.Variants.Any(v => v.Enabled && v.Stock - v.Reserved > 0) : x.Stock - x.Reserved <= 0));
            var attention = await all.CountAsync(x => x.Status != ProductStatus.Archived && (!x.IsReady || x.QualityScore < 60));
            return Results.Json(new
            {
                items = rows.Select(r =>
                {
                    long? sale = r.SaleCents is { } sc && (r.SaleStart is null || r.SaleStart <= now) && (r.SaleEnd is null || now < r.SaleEnd) ? sc : null;
                    return new
                    {
                        id = r.Id, title = r.Title, sku = r.Sku, status = r.Status.ToString(), brandId = r.BrandId, brandName = brands.GetValueOrDefault(r.BrandId),
                        categoryId = r.CategoryId, price = Money.FromCents(r.PriceCents), currentPrice = Money.FromCents(sale ?? r.PriceCents), onSale = sale is not null,
                        quantity = Math.Max(r.Stock, 0), hasVariants = r.HasVariants, variantCount = r.Variants, qualityScore = r.QualityScore, canPublish = r.IsReady,
                        needsAttention = r.Status != ProductStatus.Archived && (!r.IsReady || r.QualityScore < 60),
                        lowStock = r.LowStockAlert is { } l && r.Stock <= l, version = r.Version, updatedAt = r.UpdatedAt,
                        thumbnailUrl = r.Thumb is { } t ? ProductService.ImageUrl(r.Id, t) : null
                    };
                }),
                total, page = pg, pageSize = size,
                counts = new
                {
                    all = counts.Where(c => c.Key != ProductStatus.Archived).Sum(c => c.N),
                    draft = counts.FirstOrDefault(c => c.Key == ProductStatus.Draft)?.N ?? 0,
                    active = counts.FirstOrDefault(c => c.Key == ProductStatus.Active)?.N ?? 0,
                    inactive = counts.FirstOrDefault(c => c.Key == ProductStatus.Inactive)?.N ?? 0,
                    archived = counts.FirstOrDefault(c => c.Key == ProductStatus.Archived)?.N ?? 0,
                    outOfStock, attention
                }
            }, ProductJson.Opts);
        }).RequirePermission(Permission.BrandWorkRead);

        // ---- create ------------------------------------------------------------------------------------------------------
        p.MapPost("", async (CreateProductRequest r, HttpContext ctx, AppDbContext db, CategoryService cats, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var title = r.Title?.Trim() ?? "";
            if (title.Length is 0 or > 200) return Validation.Invalid("title", "Enter a title (1-200 characters).");
            if (r.CategoryId is { } cid && !await db.Categories.AnyAsync(c => c.Id == cid)) return Validation.Invalid("categoryId", "Unknown category.");
            var (brand, err) = await ResolveBrandAsync(db, a, r.BrandId);
            if (err is not null) return err;
            var now = time.UtcNow();
            var prod = new Product { SellerId = a.SellerId, BrandId = brand!.Id, CategoryId = r.CategoryId, Title = title, CreatedAt = now, UpdatedAt = now, CreatedBy = a.UserId };
            await ProductRules.RefreshAsync(db, cats, prod);
            db.Products.Add(prod);
            await db.SaveChangesAsync();
            return await OkDetail(db, cats, prod, time, 201);
        }).RequirePermission(Permission.BrandWorkWrite);

        // ---- read --------------------------------------------------------------------------------------------------------
        p.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CategoryService cats, TimeProvider time) =>
            await db.LoadAsync(ctx.GetAccess()!, id, write: false) is { } prod ? await OkDetail(db, cats, prod, time) : Results.NotFound())
            .RequirePermission(Permission.BrandWorkRead);

        p.MapGet("/{id:guid}/readiness", async (Guid id, HttpContext ctx, AppDbContext db, CategoryService cats) =>
        {
            var prod = await db.LoadAsync(ctx.GetAccess()!, id, write: false);
            if (prod is null) return Results.NotFound();
            var r = ProductRules.Evaluate(prod, await cats.AttributesAsync(prod.CategoryId));
            return Results.Json(new { score = r.Score, canPublish = r.CanPublish, issues = r.Issues }, ProductJson.Opts);
        }).RequirePermission(Permission.BrandWorkRead);

        // ---- update (full document; autosave target) -----------------------------------------------------------------
        p.MapPut("/{id:guid}", async (Guid id, ProductWrite r, HttpContext ctx, AppDbContext db, CategoryService cats, IInventoryService inv, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var prod = await db.LoadAsync(a, id, write: true);
            if (prod is null) return Results.NotFound();
            if (r.Version != prod.Version) return Conflict();
            var errors = ProductService.Validate(r);
            if (string.IsNullOrWhiteSpace(r.Title)) errors["title"] = ["Enter a title."];
            // brand + category
            var brandId = r.BrandId ?? prod.BrandId;
            if (brandId != prod.BrandId)
            {
                var nb = await db.RequireBrandAccess(a, brandId, write: true);
                if (nb is null || nb.Status != BrandStatus.Active) return Results.NotFound();
            }
            if (r.CategoryId is { } cid && !await db.Categories.AnyAsync(c => c.Id == cid)) errors["categoryId"] = ["Unknown category."];
            var sku = ProductService.Trim(r.Sku);
            if (!errors.ContainsKey("sku") && await ProductService.SkuConflictAsync(db, a.SellerId, sku, prod.Id) is { } skuErr) errors["sku"] = [skuErr];
            var handle = ProductService.Trim(r.Handle);
            if (handle is not null && !errors.ContainsKey("handle") && await db.Products.AnyAsync(x => x.SellerId == a.SellerId && x.Handle == handle && x.Id != prod.Id))
                errors["handle"] = ["You already use this URL handle."];
            // category-driven attributes (values of keys that don't exist in the new category are dropped)
            var attrConfig = await cats.AttributesAsync(r.CategoryId);
            var (attrValues, attrErrors) = ProductService.ValidateAttributes(attrConfig, r.Attributes);
            foreach (var (k, v) in attrErrors) errors[k] = v;
            // barcode: format/length always; check digits are a publish rule while drafting (so autosave never loses typing), enforced on live listings
            var bc = Barcode.Strip(r.Barcode);
            if (prod.Status == ProductStatus.Active && bc.Length > 0 && !Barcode.Check(bc).Valid) errors["barcode"] = [Barcode.Check(bc).Error!];
            if (r.Quantity is not null && r.HasVariants) { /* quantity is ignored while variations are on */ }
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var saleCents = Money.ToCents(r.SalePrice);
            if (prod.Status == ProductStatus.Active)
            {
                // live listings must stay publishable: evaluate the would-be document first
                var probe = new Product
                {
                    Id = prod.Id, CategoryId = r.CategoryId, Images = prod.Images, Options = prod.Options, Variants = prod.Variants,
                    Stock = r.Quantity ?? prod.Stock, Reserved = prod.Reserved
                };
                ProductService.Apply(probe, r, saleCents);
                probe.Attributes = attrValues.Select(kv => new ProductAttributeValue { Key = kv.Key, ValueJson = kv.Value }).ToList();
                var pr = ProductRules.Evaluate(probe, attrConfig);
                if (!pr.CanPublish) return Problem(422, "not_publishable", "Active listings must stay complete. Fix the items below, or set the listing to inactive first.", pr.Issues);
            }
            var categoryChanged = prod.CategoryId != r.CategoryId;
            prod.BrandId = brandId; prod.CategoryId = r.CategoryId;
            ProductService.Apply(prod, r, saleCents);
            if (prod.Handle is null && prod.Title.Length > 0 && prod.Status != ProductStatus.Draft) { /* handle is generated at activation */ }
            // attributes: replace
            db.ProductAttributeValues.RemoveRange(prod.Attributes);
            prod.Attributes = attrValues.Select(kv => new ProductAttributeValue { ProductId = prod.Id, Key = kv.Key, ValueJson = kv.Value }).ToList();
            foreach (var av in prod.Attributes) db.Entry(av).State = EntityState.Added;
            await ProductRules.RefreshAsync(db, cats, prod);
            prod.Version++; prod.UpdatedAt = time.UtcNow();
            // stock goes through the inventory service (atomic), never by assigning the column while orders may be reserving
            if (!await TrySaveAsync(db)) return Conflict();
            if (r.Quantity is { } qty && !r.HasVariants) await inv.SetOnHandAsync(a.SellerId, prod.Id, null, qty);
            _ = categoryChanged;
            db.ChangeTracker.Clear(); // ExecuteUpdate bypasses the tracker: reload so the response shows the real stock
            var fresh = await db.LoadAsync(a, id, write: false);
            await ProductRules.RefreshAsync(db, cats, fresh!);
            await db.SaveChangesAsync();
            return await OkDetail(db, cats, fresh!, time);
        }).RequirePermission(Permission.BrandWorkWrite);

        // ---- quick edit (inline price / stock in the list) -------------------------------------------------------------
        p.MapPatch("/{id:guid}/quick", async (Guid id, QuickEditRequest r, HttpContext ctx, AppDbContext db, CategoryService cats, IInventoryService inv, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var prod = await db.LoadAsync(a, id, write: true);
            if (prod is null) return Results.NotFound();
            if (r.Version is { } v && v != prod.Version) return Conflict();
            if (r.Price is null && r.Quantity is null) return Validation.Invalid("price", "Nothing to change.");
            if (r.Price is { } price)
            {
                if (price < 0.01m || price > 999_999.99m || decimal.Round(price, 2) != price) return Validation.Invalid("price", "Enter an amount between 0.01 and 999,999.99.");
                var cents = Money.ToCents(price)!.Value;
                if (prod.SaleCents is { } sc && sc >= cents) return Validation.Invalid("price", "The price must stay above the sale price. Edit the listing to change both.");
                if (prod.CompareAtCents is { } cp && cp <= cents) return Validation.Invalid("price", "The price must stay below the compare-at price. Edit the listing to change both.");
                prod.PriceCents = cents;
            }
            if (r.Quantity is { } q)
            {
                if (q is < 0 or > InventoryService.MaxStock) return Validation.Invalid("quantity", "Enter a quantity between 0 and 1,000,000.");
                if (prod.HasVariants) return Validation.Invalid("quantity", "This listing has variations: edit the quantity of each variation.");
            }
            await ProductRules.RefreshAsync(db, cats, prod);
            prod.Version++; prod.UpdatedAt = time.UtcNow();
            if (!await TrySaveAsync(db)) return Conflict();
            if (r.Quantity is { } qq) await inv.SetOnHandAsync(a.SellerId, prod.Id, null, qq);
            db.ChangeTracker.Clear();
            var fresh = (await db.LoadAsync(a, id, write: false))!;
            await ProductRules.RefreshAsync(db, cats, fresh);
            await db.SaveChangesAsync();
            return await OkDetail(db, cats, fresh, time);
        }).RequirePermission(Permission.BrandWorkWrite);

        // ---- status ------------------------------------------------------------------------------------------------------
        p.MapPost("/{id:guid}/status", async (Guid id, StatusRequest r, HttpContext ctx, AppDbContext db, CategoryService cats, IAuditLog audit, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var prod = await db.LoadAsync(a, id, write: true);
            if (prod is null) return Results.NotFound();
            var err = await ChangeStatusAsync(db, cats, a, prod, r.Status, audit, time);
            return err ?? await OkDetail(db, cats, prod, time);
        }).RequirePermission(Permission.BrandWorkWrite);

        p.MapPost("/bulk-status", async (BulkStatusRequest r, HttpContext ctx, AppDbContext db, CategoryService cats, IAuditLog audit, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            if (r.Ids is not { Count: > 0 and <= 100 }) return Validation.Invalid("ids", "Select between 1 and 100 listings.");
            var ok = new List<Guid>(); var failed = new List<object>();
            foreach (var id in r.Ids.Distinct())
            {
                var prod = await db.LoadAsync(a, id, write: true);
                if (prod is null) { failed.Add(new { id, code = "not_found", message = "Listing not found." }); continue; }
                var err = await ChangeStatusAsync(db, cats, a, prod, r.Status, audit, time);
                if (err is null) ok.Add(id);
                else failed.Add(new { id, title = prod.Title, code = "refused", message = ((err as IStatusCodeHttpResult)?.StatusCode == 403) ? "Your account isn't approved yet." : "Listing isn't ready or the change isn't allowed." });
                db.ChangeTracker.Clear();
            }
            return Results.Ok(new { updated = ok.Count, ids = ok, failed });
        }).RequirePermission(Permission.BrandWorkWrite);

        // ---- delete ------------------------------------------------------------------------------------------------------
        p.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, IFileStorage storage, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var prod = await db.LoadAsync(a, id, write: true);
            if (prod is null) return Results.NotFound();
            if (prod.Status == ProductStatus.Active) return Problem(409, "deactivate_first", "Deactivate the listing before deleting it.");
            var keys = prod.Images.Select(i => i.StorageKey).ToList();
            db.Products.Remove(prod);
            await db.SaveChangesAsync();
            foreach (var k in keys) await storage.DeleteAsync(k);
            await audit.WriteAsync("product.deleted", a.UserId, null, $"product={id};seller={a.SellerId}");
            return Results.NoContent();
        }).RequirePermission(Permission.BrandWorkWrite);

        // ---- duplicate ---------------------------------------------------------------------------------------------------
        p.MapPost("/{id:guid}/duplicate", async (Guid id, HttpContext ctx, AppDbContext db, CategoryService cats, IFileStorage storage, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var src = await db.LoadAsync(a, id, write: true);
            if (src is null) return Results.NotFound();
            var now = time.UtcNow();
            var copy = new Product
            {
                SellerId = a.SellerId, BrandId = src.BrandId, CategoryId = src.CategoryId, Status = ProductStatus.Draft,
                Title = ("Copy of " + src.Title) is { Length: > 200 } t ? t[..200] : "Copy of " + src.Title,
                Condition = src.Condition, ConditionNote = src.ConditionNote, PriceCents = src.PriceCents, CompareAtCents = src.CompareAtCents, CostCents = src.CostCents,
                Stock = 0, LowStockAlert = src.LowStockAlert, AllowBackorder = src.AllowBackorder, MaxPerOrder = src.MaxPerOrder, NoBarcode = src.NoBarcode,
                BulletsJson = src.BulletsJson, Description = src.Description, WeightGrams = src.WeightGrams, LengthMm = src.LengthMm, WidthMm = src.WidthMm, HeightMm = src.HeightMm,
                HandlingDays = src.HandlingDays, ShippingJson = src.ShippingJson, LocalPickup = src.LocalPickup, ReturnsDays = src.ReturnsDays, ReturnsCondition = src.ReturnsCondition,
                WarrantyMonths = src.WarrantyMonths, WarrantyNote = src.WarrantyNote, SearchTerms = src.SearchTerms, MetaTitle = src.MetaTitle, MetaDescription = src.MetaDescription,
                HasVariants = src.HasVariants, CreatedAt = now, UpdatedAt = now, CreatedBy = a.UserId
                // SKU, barcode and handle are unique identifiers: never copied
            };
            foreach (var o in src.Options) copy.Options.Add(new ProductOption { ProductId = copy.Id, Name = o.Name, ValuesJson = o.ValuesJson, SortOrder = o.SortOrder });
            var map = new Dictionary<Guid, Guid>();
            foreach (var v in src.Variants)
            {
                var nv = new ProductVariant { ProductId = copy.Id, SellerId = a.SellerId, ValuesJson = v.ValuesJson, ComboKey = v.ComboKey, PriceCents = v.PriceCents, Enabled = v.Enabled, SortOrder = v.SortOrder };
                map[v.Id] = nv.Id; copy.Variants.Add(nv);
            }
            foreach (var at in src.Attributes) copy.Attributes.Add(new ProductAttributeValue { ProductId = copy.Id, Key = at.Key, ValueJson = at.ValueJson });
            foreach (var f in src.Faqs) copy.Faqs.Add(new ProductFaq { ProductId = copy.Id, Question = f.Question, Answer = f.Answer, SortOrder = f.SortOrder });
            foreach (var im in src.Images.OrderBy(i => i.SortOrder))
            {
                await using var s = await storage.OpenReadAsync(im.StorageKey);
                if (s is null) continue;
                var key = Guid.NewGuid().ToString("N");
                await storage.SaveAsync(key, s);
                copy.Images.Add(new ProductImage
                {
                    ProductId = copy.Id, SellerId = a.SellerId, VariantId = im.VariantId is { } vi && map.TryGetValue(vi, out var nvid) ? nvid : null, StorageKey = key,
                    ContentType = im.ContentType, Size = im.Size, Width = im.Width, Height = im.Height, Alt = im.Alt, SortOrder = im.SortOrder, IsMain = im.IsMain, CreatedAt = now
                });
            }
            await ProductRules.RefreshAsync(db, cats, copy);
            db.Products.Add(copy);
            await db.SaveChangesAsync();
            return await OkDetail(db, cats, copy, time, 201);
        }).RequirePermission(Permission.BrandWorkWrite);

        p.MapGroup("").MapProductChildEndpoints();
    }

    /// <summary>One status transition with every rule (approval, readiness, audit). Returns null on success, else the error result.</summary>
    internal static async Task<IResult?> ChangeStatusAsync(AppDbContext db, CategoryService cats, AccountAccess a, Product prod, string? target, IAuditLog audit, TimeProvider time)
    {
        if (!Enum.TryParse<ProductStatus>(target, true, out var to) || !Enum.IsDefined(to) || to == ProductStatus.Draft) return Validation.Invalid("status", "Choose active, inactive or archived.");
        var from = prod.Status;
        if (from == to) return null;
        switch (to)
        {
            case ProductStatus.Active:
                if (from == ProductStatus.Archived) return Problem(409, "restore_first", "Restore the listing before activating it.");
                if (a.AccountStatus is not (SellerStatus.Approved or SellerStatus.Live))
                    return Problem(403, "seller_not_approved", "Your seller account must be approved before you can activate listings. You can keep saving drafts.");
                var r = await ProductRules.RefreshAsync(db, cats, prod);
                if (!r.CanPublish) return Problem(422, "not_publishable", "This listing isn't ready to publish yet.", r.Issues);
                if (prod.Handle is null)
                {
                    var baseHandle = ProductService.Slugify(prod.Title); if (baseHandle.Length == 0) baseHandle = "product"; if (baseHandle.Length > 90) baseHandle = baseHandle[..90];
                    var h = baseHandle;
                    for (var i = 2; await db.Products.AnyAsync(x => x.SellerId == prod.SellerId && x.Handle == h && x.Id != prod.Id); i++) h = $"{baseHandle}-{i}";
                    prod.Handle = h;
                }
                prod.PublishedAt ??= time.UtcNow();
                break;
            case ProductStatus.Inactive:
                if (from != ProductStatus.Active && from != ProductStatus.Archived) return Problem(409, "not_active", "Only active listings can be deactivated.");
                break;
            case ProductStatus.Archived: break;
        }
        prod.Status = to; prod.UpdatedAt = time.UtcNow(); prod.Version++;
        if (!await TrySaveAsync(db)) return Conflict();
        await audit.WriteAsync(to switch { ProductStatus.Active => "product.activated", ProductStatus.Inactive => "product.deactivated", _ => "product.archived" },
            a.UserId, null, $"product={prod.Id};seller={a.SellerId};from={from}");
        return null;
    }
}
