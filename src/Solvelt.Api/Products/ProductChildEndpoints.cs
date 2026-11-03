using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Products;

public record ImagePatch(string? Alt, bool? IsMain);
public record ImageOrder(List<Guid>? Ids, Guid? VariantId);
public record OptionWrite(string? Name, List<string>? Values);
public record VariantWrite(List<string>? Values, string? Sku, decimal? Price, int? Quantity, string? Barcode, bool? Enabled);
public record VariantsWrite(bool HasVariants, List<OptionWrite>? Options, List<VariantWrite>? Variants);
public record FaqWrite(string? Question, string? Answer);

public static class ProductChildEndpoints
{
    public const long MaxImageBytes = 10 * 1024 * 1024;
    public const int MinSide = 100, MaxSide = 12_000;

    private static void Renumber(IEnumerable<ProductImage> scope)
    {
        var i = 0;
        foreach (var im in scope.OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt)) { im.SortOrder = i; im.IsMain = i == 0; i++; }
    }

    private static async Task<IResult> Touch(AppDbContext db, CategoryService cats, Product prod, TimeProvider time, object? payload = null, int status = 200)
    {
        await ProductRules.RefreshAsync(db, cats, prod);
        prod.UpdatedAt = time.UtcNow();
        if (!await ProductEndpoints.TrySaveAsync(db)) return ProductEndpoints.Conflict();
        return payload is null ? Results.NoContent() : Results.Json(payload, ProductJson.Opts, statusCode: status);
    }

    public static void MapProductChildEndpoints(this RouteGroupBuilder p)
    {
        // ---- images ------------------------------------------------------------------------------------------------------
        p.MapPost("/{id:guid}/images", async (Guid id, IFormFile file, string? alt, Guid? variantId, HttpContext ctx, AppDbContext db, CategoryService cats, IFileStorage storage, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var prod = await db.LoadAsync(a, id, write: true);
            if (prod is null) return Results.NotFound();
            if (variantId is { } vid && prod.Variants.All(v => v.Id != vid)) return Results.NotFound();
            if (prod.Images.Count(i => i.VariantId == variantId) >= ProductService.MaxImages)
                return Validation.Invalid("file", $"You can add up to {ProductService.MaxImages} images{(variantId is null ? "" : " per variation")}.");
            if (file.Length == 0 || file.Length > MaxImageBytes) return Validation.Invalid("file", "Images must be between 1 byte and 10 MB.");
            if (alt is { Length: > 200 }) return Validation.Invalid("alt", "Maximum 200 characters.");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) return Validation.Invalid("file", "Only JPEG, PNG or WebP images are allowed.");
            byte[] data;
            await using (var s = file.OpenReadStream()) { using var ms = new MemoryStream(); await s.CopyToAsync(ms); data = ms.ToArray(); }
            var sniffed = ImageProcessing.Sniff(data);
            var okExt = sniffed switch { "image/jpeg" => ext is ".jpg" or ".jpeg", "image/png" => ext == ".png", "image/webp" => ext == ".webp", _ => false };
            if (sniffed is null || !okExt) return Validation.Invalid("file", "The file content doesn't match a JPEG, PNG or WebP image.");
            var img = ImageProcessing.Process(data);
            if (img is null) return Validation.Invalid("file", "This image file looks damaged. Try exporting it again.");
            if (img.Width < MinSide || img.Height < MinSide || img.Width > MaxSide || img.Height > MaxSide)
                return Validation.Invalid("file", $"Image size must be between {MinSide} and {MaxSide} pixels on each side.");
            var key = Guid.NewGuid().ToString("N");
            await storage.SaveAsync(key, new MemoryStream(img.Data));
            var row = new ProductImage
            {
                ProductId = prod.Id, SellerId = a.SellerId, VariantId = variantId, StorageKey = key, ContentType = img.ContentType, Size = img.Data.Length,
                Width = img.Width, Height = img.Height, Alt = ProductService.Trim(alt), CreatedAt = time.UtcNow(),
                SortOrder = prod.Images.Where(i => i.VariantId == variantId).Select(i => i.SortOrder + 1).DefaultIfEmpty(0).Max()
            };
            row.IsMain = row.SortOrder == 0;
            db.ProductImages.Add(row); prod.Images.Add(row); prod.ImageSeq++; // token: a parallel upload that also passed the count check fails on save
            var res = await Touch(db, cats, prod, time, new { image = ProductService.ImageDto(row), qualityScore = prod.QualityScore, canPublish = prod.IsReady }, 201);
            if (res is IStatusCodeHttpResult { StatusCode: 409 }) await storage.DeleteAsync(key);
            return res;
        }).RequirePermission(Permission.BrandWorkWrite).DisableAntiforgery();

        p.MapPut("/{id:guid}/images/order", async (Guid id, ImageOrder r, HttpContext ctx, AppDbContext db, CategoryService cats, TimeProvider time) =>
        {
            var prod = await db.LoadAsync(ctx.GetAccess()!, id, write: true);
            if (prod is null) return Results.NotFound();
            var scope = prod.Images.Where(i => i.VariantId == r.VariantId).ToList();
            var ids = r.Ids ?? [];
            if (ids.Count != scope.Count || ids.Distinct().Count() != ids.Count || scope.Any(i => !ids.Contains(i.Id)))
                return Validation.Invalid("ids", "Send every image of this set exactly once.");
            for (var i = 0; i < ids.Count; i++) { var im = scope.First(x => x.Id == ids[i]); im.SortOrder = i; im.IsMain = i == 0; }
            return await Touch(db, cats, prod, time, new { images = prod.Images.OrderBy(i => i.VariantId == null ? 0 : 1).ThenBy(i => i.SortOrder).Select(ProductService.ImageDto) });
        }).RequirePermission(Permission.BrandWorkWrite);

        p.MapPut("/{id:guid}/images/{imageId:guid}", async (Guid id, Guid imageId, ImagePatch r, HttpContext ctx, AppDbContext db, CategoryService cats, TimeProvider time) =>
        {
            var prod = await db.LoadAsync(ctx.GetAccess()!, id, write: true);
            var im = prod?.Images.FirstOrDefault(i => i.Id == imageId);
            if (prod is null || im is null) return Results.NotFound();
            if (r.Alt is { Length: > 200 }) return Validation.Invalid("alt", "Maximum 200 characters.");
            if (r.Alt is not null) im.Alt = ProductService.Trim(r.Alt);
            if (r.IsMain == true)
            {
                foreach (var o in prod.Images.Where(x => x.VariantId == im.VariantId)) o.SortOrder++;
                im.SortOrder = 0; Renumber(prod.Images.Where(x => x.VariantId == im.VariantId));
            }
            return await Touch(db, cats, prod, time, new { images = prod.Images.OrderBy(i => i.VariantId == null ? 0 : 1).ThenBy(i => i.SortOrder).Select(ProductService.ImageDto), qualityScore = prod.QualityScore });
        }).RequirePermission(Permission.BrandWorkWrite);

        p.MapDelete("/{id:guid}/images/{imageId:guid}", async (Guid id, Guid imageId, HttpContext ctx, AppDbContext db, CategoryService cats, IFileStorage storage, TimeProvider time) =>
        {
            var prod = await db.LoadAsync(ctx.GetAccess()!, id, write: true);
            var im = prod?.Images.FirstOrDefault(i => i.Id == imageId);
            if (prod is null || im is null) return Results.NotFound();
            if (prod.Status == ProductStatus.Active && im.VariantId is null && prod.Images.Count(i => i.VariantId == null) == 1)
                return ProductEndpoints.Problem(422, "not_publishable", "An active listing needs at least one photo. Add another one first, or deactivate the listing.");
            prod.Images.Remove(im); db.ProductImages.Remove(im); prod.ImageSeq++;
            Renumber(prod.Images.Where(x => x.VariantId == im.VariantId));
            var res = await Touch(db, cats, prod, time, new { images = prod.Images.OrderBy(i => i.VariantId == null ? 0 : 1).ThenBy(i => i.SortOrder).Select(ProductService.ImageDto), qualityScore = prod.QualityScore });
            if (res is not IStatusCodeHttpResult { StatusCode: 409 }) await storage.DeleteAsync(im.StorageKey);
            return res;
        }).RequirePermission(Permission.BrandWorkWrite);

        p.MapGet("/{id:guid}/images/{imageId:guid}/file", async (Guid id, Guid imageId, HttpContext ctx, AppDbContext db, IFileStorage storage) =>
        {
            var a = ctx.GetAccess()!;
            var im = await db.ProductImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == id && i.SellerId == a.SellerId);
            if (im is null) return Results.NotFound();
            var brand = await db.Products.AsNoTracking().Where(x => x.Id == id).Select(x => x.BrandId).FirstOrDefaultAsync();
            if (!a.CanWorkOnBrand(brand, write: false)) return Results.NotFound();
            var stream = await storage.OpenReadAsync(im.StorageKey);
            if (stream is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=86400, immutable"; // image bytes of a given id never change
            return Results.File(stream, im.ContentType); // inline; X-Content-Type-Options: nosniff is added globally
        }).RequirePermission(Permission.BrandWorkRead);

        // ---- options + variants --------------------------------------------------------------------------------------------
        p.MapPost("/{id:guid}/variants/preview", (Guid id, VariantsWrite r) =>
        {
            var (opts, err) = ParseOptions(r.Options);
            if (err is not null) return err;
            return Results.Ok(new { combinations = VariantGenerator.Combos(opts.Select(o => o.Values).ToList()) });
        }).RequirePermission(Permission.BrandWorkRead);

        p.MapPut("/{id:guid}/variants", async (Guid id, VariantsWrite r, HttpContext ctx, AppDbContext db, CategoryService cats, IInventoryService inv, IFileStorage storage, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var prod = await db.LoadAsync(a, id, write: true);
            if (prod is null) return Results.NotFound();
            var (opts, err) = ParseOptions(r.Options);
            if (err is not null) return err;
            if (r.HasVariants && opts.Count == 0) return Validation.Invalid("options", "Add at least one option such as Size or Color.");
            var combos = VariantGenerator.Combos(opts.Select(o => o.Values).ToList());
            if (combos.Count > VariantGenerator.MaxVariants) return Validation.Invalid("options", $"These options make {combos.Count} variations; the maximum is {VariantGenerator.MaxVariants}.");
            var incoming = new Dictionary<string, VariantWrite>();
            var errors = new Dictionary<string, string[]>();
            var skuSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (var v in r.Variants ?? [])
            {
                var key = VariantGenerator.Key(v.Values ?? []);
                var field = $"variants[{n++}]";
                if (v.Values is null || v.Values.Count != opts.Count || !combos.Any(c => VariantGenerator.Key(c) == key)) { errors[field] = ["This combination doesn't match the options."]; continue; }
                if (!incoming.TryAdd(key, v)) { errors[field] = ["Duplicate combination."]; continue; }
                var sku = ProductService.Trim(v.Sku);
                if (sku is not null && !ProductService.ValidSku(sku)) errors[field + ".sku"] = ["Use letters, numbers and . - _ / # (max 64 characters)."];
                else if (sku is not null && !skuSeen.Add(sku)) errors[field + ".sku"] = ["Each variation needs its own SKU."];
                if (v.Price is { } pr && (pr < 0.01m || pr > 999_999.99m || decimal.Round(pr, 2) != pr)) errors[field + ".price"] = ["Enter an amount between 0.01 and 999,999.99."];
                if (v.Quantity is { } q && (q < 0 || q > InventoryService.MaxStock)) errors[field + ".quantity"] = ["Enter a quantity between 0 and 1,000,000."];
                else if (v.Quantity is { } q2 && prod.Variants.FirstOrDefault(x => x.ComboKey == key) is { } cur && q2 < cur.Reserved) errors[field + ".quantity"] = [$"{cur.Reserved} unit(s) are reserved by open orders, so the quantity can't go below {cur.Reserved}."];
                if (Barcode.Strip(v.Barcode).Length > 20) errors[field + ".barcode"] = ["A barcode has at most 20 characters."];
                else if (prod.Status == ProductStatus.Active && Barcode.Strip(v.Barcode) is { Length: > 0 } bc && !Barcode.Check(bc).Valid) errors[field + ".barcode"] = [Barcode.Check(bc).Error!];
                if (errors.ContainsKey(field + ".sku") || sku is null) continue;
                var existing = prod.Variants.FirstOrDefault(x => x.ComboKey == key);
                if (await ProductService.SkuConflictAsync(db, a.SellerId, sku, prod.Id, existing?.Id) is { } m) errors[field + ".sku"] = [m];
                if (prod.Sku is not null && string.Equals(prod.Sku, sku, StringComparison.OrdinalIgnoreCase)) errors[field + ".sku"] = ["This SKU is used by the main listing."];
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            // options
            db.ProductOptions.RemoveRange(prod.Options); prod.Options.Clear();
            for (var i = 0; i < opts.Count; i++)
            {
                var o = new ProductOption { ProductId = prod.Id, Name = opts[i].Name, ValuesJson = JsonSerializer.Serialize(opts[i].Values), SortOrder = i };
                prod.Options.Add(o); db.Entry(o).State = EntityState.Added;
            }
            // variants: keep rows of surviving combinations (stock history intact), drop the rest, add the missing ones
            var wanted = combos.Select(c => (Key: VariantGenerator.Key(c), Values: c)).ToList();
            var removed = prod.Variants.Where(v => wanted.All(w => w.Key != v.ComboKey)).ToList();
            var dropKeys = new List<string>();
            foreach (var v in removed)
            {
                foreach (var im in prod.Images.Where(i => i.VariantId == v.Id).ToList()) { dropKeys.Add(im.StorageKey); prod.Images.Remove(im); db.ProductImages.Remove(im); }
                prod.Variants.Remove(v); db.Variants.Remove(v);
            }
            var stockSets = new List<(Guid Id, int Qty)>();
            for (var i = 0; i < wanted.Count; i++)
            {
                var w = wanted[i];
                incoming.TryGetValue(w.Key, out var inc);
                var v = prod.Variants.FirstOrDefault(x => x.ComboKey == w.Key);
                var isNew = v is null;
                if (v is null) { v = new ProductVariant { ProductId = prod.Id, SellerId = a.SellerId, ComboKey = w.Key }; prod.Variants.Add(v); db.Entry(v).State = EntityState.Added; }
                v.ValuesJson = JsonSerializer.Serialize(w.Values); v.SortOrder = i;
                if (inc is not null)
                {
                    v.Sku = ProductService.Trim(inc.Sku); v.PriceCents = Money.ToCents(inc.Price);
                    var bc = Barcode.Strip(inc.Barcode); v.Barcode = bc.Length == 0 ? null : bc; v.Enabled = inc.Enabled ?? true;
                    if (isNew) v.Stock = inc.Quantity ?? 0; else if (inc.Quantity is { } q) stockSets.Add((v.Id, q));
                }
            }
            prod.HasVariants = r.HasVariants;
            if (await ProductService.SyncSkuClaimsAsync(db, prod) is { } claimErr) { db.ChangeTracker.Clear(); return Validation.Invalid("variants", claimErr); }
            if (prod.Status == ProductStatus.Active)
            {
                // a live listing must stay publishable: evaluate the would-be state before anything is saved
                var would = ProductRules.Evaluate(prod, await cats.AttributesAsync(prod.CategoryId));
                if (!would.CanPublish) { db.ChangeTracker.Clear(); return ProductEndpoints.Problem(422, "not_publishable", "Active listings must stay complete. Fix the items below, or set the listing to inactive first.", would.Issues); }
            }
            prod.Version++;
            var res = await Touch(db, cats, prod, time);
            if (res is IStatusCodeHttpResult { StatusCode: 409 }) return res;
            foreach (var (vid, q) in stockSets) await inv.SetOnHandAsync(a.SellerId, prod.Id, vid, q);
            foreach (var k in dropKeys) await storage.DeleteAsync(k);
            db.ChangeTracker.Clear();
            var fresh = (await db.LoadAsync(a, id, write: false))!;
            await ProductRules.RefreshAsync(db, cats, fresh); await db.SaveChangesAsync();
            return await ProductEndpoints.OkDetail(db, cats, fresh, time);
        }).RequirePermission(Permission.BrandWorkWrite);

        // ---- FAQ ---------------------------------------------------------------------------------------------------------
        p.MapPut("/{id:guid}/faqs", async (Guid id, List<FaqWrite> r, HttpContext ctx, AppDbContext db, CategoryService cats, TimeProvider time) =>
        {
            var prod = await db.LoadAsync(ctx.GetAccess()!, id, write: true);
            if (prod is null) return Results.NotFound();
            var items = r.Where(f => !string.IsNullOrWhiteSpace(f.Question) || !string.IsNullOrWhiteSpace(f.Answer)).ToList();
            if (items.Count > 20) return Validation.Invalid("faqs", "Up to 20 questions.");
            for (var i = 0; i < items.Count; i++)
            {
                var f = items[i];
                if (string.IsNullOrWhiteSpace(f.Question) || f.Question.Trim().Length > 200) return Validation.Invalid($"faqs[{i}].question", "Enter a question (max 200 characters).");
                if (string.IsNullOrWhiteSpace(f.Answer) || f.Answer.Trim().Length > 1000) return Validation.Invalid($"faqs[{i}].answer", "Enter an answer (max 1000 characters).");
            }
            db.ProductFaqs.RemoveRange(prod.Faqs); prod.Faqs.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                var f = new ProductFaq { ProductId = prod.Id, Question = items[i].Question!.Trim(), Answer = items[i].Answer!.Trim(), SortOrder = i };
                prod.Faqs.Add(f); db.Entry(f).State = EntityState.Added;
            }
            return await Touch(db, cats, prod, time, new { faqs = prod.Faqs.OrderBy(f => f.SortOrder).Select(f => new { id = f.Id, question = f.Question, answer = f.Answer }) });
        }).RequirePermission(Permission.BrandWorkWrite);
    }

    private static (List<(string Name, string[] Values)> Options, IResult? Error) ParseOptions(List<OptionWrite>? raw)
    {
        var res = new List<(string, string[])>();
        var list = raw ?? [];
        if (list.Count > VariantGenerator.MaxOptions) return (res, Validation.Invalid("options", $"Up to {VariantGenerator.MaxOptions} options."));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var name = list[i].Name?.Trim() ?? "";
            if (name.Length is 0 or > 40) return (res, Validation.Invalid($"options[{i}].name", "Enter an option name (max 40 characters), e.g. Size."));
            if (!names.Add(name)) return (res, Validation.Invalid($"options[{i}].name", "Option names must be different."));
            var vals = (list[i].Values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            if (vals.Count is 0 or > VariantGenerator.MaxValuesPerOption) return (res, Validation.Invalid($"options[{i}].values", $"Add 1-{VariantGenerator.MaxValuesPerOption} values."));
            if (vals.Any(v => v.Length > 60 || v.Contains('|'))) return (res, Validation.Invalid($"options[{i}].values", "Values can have up to 60 characters and no | sign."));
            if (vals.Select(v => v.ToLowerInvariant()).Distinct().Count() != vals.Count) return (res, Validation.Invalid($"options[{i}].values", "Values must be different."));
            res.Add((name, vals.ToArray()));
        }
        return (res, null);
    }
}
