using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Products;

public record ShippingWrite(bool StandardEnabled = true, decimal? StandardPrice = null, bool ExpressEnabled = false, decimal? ExpressPrice = null, decimal? FreeOver = null);

/// <summary>Full replacement of the main document (every field of every simple section). Null clears an optional field.</summary>
public record ProductWrite(
    int Version, Guid? BrandId, Guid? CategoryId, string? Title, string? Handle, string? Condition, string? ConditionNote, string? Sku,
    decimal? Price, decimal? CompareAtPrice, decimal? SalePrice, DateTime? SaleStart, DateTime? SaleEnd, decimal? Cost,
    int? Quantity, int? LowStockAlert, bool AllowBackorder, int? MaxPerOrder, string? Barcode, bool NoBarcode,
    List<string>? Bullets, string? Description,
    int? WeightGrams, decimal? LengthCm, decimal? WidthCm, decimal? HeightCm, int? HandlingDays, ShippingWrite? Shipping, bool LocalPickup,
    int? ReturnsDays, string? ReturnsCondition, int? WarrantyMonths, string? WarrantyNote,
    string? SearchTerms, string? MetaTitle, string? MetaDescription, bool HasVariants,
    Dictionary<string, JsonElement>? Attributes);

public static partial class ProductService
{
    public const int MaxImages = 9;

    public static string ImageUrl(Guid productId, Guid imageId) => $"/api/v1/seller/products/{productId}/images/{imageId}/file";

    public static IQueryable<Product> WithChildren(this IQueryable<Product> q) =>
        q.Include(p => p.Options).Include(p => p.Variants).Include(p => p.Images).Include(p => p.Attributes).Include(p => p.Faqs).Include(p => p.Service).AsSplitQuery();

    /// <summary>The ONLY way endpoints load a product: account + brand scope + permission checked; null means answer 404 (no IDOR, no existence leak).</summary>
    public static async Task<Product?> LoadAsync(this AppDbContext db, AccountAccess a, Guid id, bool write)
    {
        var p = await db.Products.Where(x => x.Id == id && x.SellerId == a.SellerId).WithChildren().FirstOrDefaultAsync();
        return p is not null && a.CanWorkOnBrand(p.BrandId, write) ? p : null;
    }

    /// <summary>Product ids/query restricted to the caller's account and brand scope. Use for every list/aggregate query.</summary>
    public static IQueryable<Product> ProductsFor(this AppDbContext db, AccountAccess a)
    {
        var q = db.Products.Where(p => p.SellerId == a.SellerId);
        if (a.AllBrands || a.Role >= AccountRole.Admin) return q;
        var ids = a.BrandIds.ToList();
        return q.Where(p => ids.Contains(p.BrandId));
    }

    public static object ImageDto(ProductImage i) => new
    {
        id = i.Id, url = ImageUrl(i.ProductId, i.Id), alt = i.Alt, isMain = i.IsMain, variantId = i.VariantId, width = i.Width, height = i.Height,
        size = i.Size, sortOrder = i.SortOrder, lowResolution = Math.Min(i.Width, i.Height) < 1000
    };

    public static object Detail(Product p, Readiness r, DateTime now, string? brandName, IEnumerable<CategoryAttribute>? attrs = null)
    {
        var sh = ProductJson.Shipping(p);
        return new
        {
            id = p.Id, type = p.Type.ToString(), service = p.Service is null ? null : Bookings.ServiceView.Detail(p.Service), brandId = p.BrandId, brandName, categoryId = p.CategoryId, status = p.Status.ToString(), version = p.Version,
            title = p.Title, handle = p.Handle, condition = p.Condition.ToString(), conditionNote = p.ConditionNote, sku = p.Sku,
            price = Money.FromCents(p.PriceCents), compareAtPrice = Money.FromCents(p.CompareAtCents), salePrice = Money.FromCents(p.SaleCents),
            saleStart = p.SaleStart, saleEnd = p.SaleEnd, cost = Money.FromCents(p.CostCents),
            margin = p.CostCents is { } c && p.PriceCents is > 0 ? Math.Round((p.PriceCents.Value - c) * 100m / p.PriceCents.Value, 1) : (decimal?)null,
            currentPrice = Money.FromCents(ProductRules.CurrentPriceCents(p, now)), onSale = ProductRules.EffectiveSale(p, now) is not null,
            quantity = p.Stock, reserved = p.Reserved, available = ProductRules.AvailableStock(p), lowStockAlert = p.LowStockAlert,
            allowBackorder = p.AllowBackorder, maxPerOrder = p.MaxPerOrder,
            barcode = p.Barcode, noBarcode = p.NoBarcode,
            barcodeCheck = string.IsNullOrEmpty(p.Barcode) ? null : Barcode.Check(p.Barcode) is var b ? new { valid = b.Valid, type = b.Type, error = b.Error } : null,
            bullets = ProductJson.Bullets(p), description = p.Description, descriptionHtml = SafeMarkdown.Render(p.Description),
            weightGrams = p.WeightGrams, lengthCm = p.LengthMm / 10m, widthCm = p.WidthMm / 10m, heightCm = p.HeightMm / 10m, handlingDays = p.HandlingDays,
            shipping = new { standardEnabled = sh.StandardEnabled, standardPrice = Money.FromCents(sh.StandardCents), expressEnabled = sh.ExpressEnabled,
                expressPrice = Money.FromCents(sh.ExpressCents), freeOver = Money.FromCents(sh.FreeOverCents) },
            localPickup = p.LocalPickup,
            returnsDays = p.ReturnsDays, returnsCondition = p.ReturnsCondition, warrantyMonths = p.WarrantyMonths, warrantyNote = p.WarrantyNote,
            searchTerms = p.SearchTerms, metaTitle = p.MetaTitle, metaDescription = p.MetaDescription,
            hasVariants = p.HasVariants,
            options = p.Options.OrderBy(o => o.SortOrder).Select(o => new { id = o.Id, name = o.Name, values = ProductJson.Values(o.ValuesJson) }),
            variants = p.Variants.OrderBy(v => v.SortOrder).Select(v => new
            {
                id = v.Id, values = ProductJson.Values(v.ValuesJson), sku = v.Sku, price = Money.FromCents(v.PriceCents), quantity = v.Stock, reserved = v.Reserved,
                barcode = v.Barcode, enabled = v.Enabled
            }),
            images = p.Images.OrderBy(i => i.VariantId == null ? 0 : 1).ThenBy(i => i.SortOrder).Select(ImageDto),
            attributes = p.Attributes.ToDictionary(a => a.Key, a => JsonSerializer.Deserialize<JsonElement>(a.ValueJson)),
            attributeConfig = attrs?.Select(CategoryService.AttrDto),
            faqs = p.Faqs.OrderBy(f => f.SortOrder).Select(f => new { id = f.Id, question = f.Question, answer = f.Answer }),
            qualityScore = r.Score, canPublish = r.CanPublish, issues = r.Issues,
            createdAt = p.CreatedAt, updatedAt = p.UpdatedAt, publishedAt = p.PublishedAt
        };
    }

    [GeneratedRegex(@"^[\w.\-/# ]{1,64}$")] private static partial Regex SkuRe();
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")] private static partial Regex HandleRe();

    public static bool ValidSku(string? s) => s is not null && SkuRe().IsMatch(s);

    public static string? Trim(string? s, int max = int.MaxValue) { var t = s?.Trim(); return string.IsNullOrEmpty(t) ? null : t; }

    /// <summary>Validates ranges/lengths of the main document. Does not need the database.</summary>
    public static Dictionary<string, string[]> Validate(ProductWrite r)
    {
        var e = new Dictionary<string, string[]>();
        void Err(string f, string m) => e[f] = [m];
        void Money_(string f, decimal? v, decimal min = 0.01m, decimal max = 999_999.99m)
        { if (v is not null && (v < min || v > max || decimal.Round(v.Value, 2) != v)) Err(f, $"Enter an amount between {min:0.00} and {max:0.00} with at most 2 decimals."); }
        void Int(string f, int? v, int min, int max, string label)
        { if (v is not null && (v < min || v > max)) Err(f, $"{label} must be between {min} and {max}."); }
        if ((r.Title?.Trim().Length ?? 0) > 200) Err("title", "Maximum 200 characters.");
        if (r.Condition is not null && !Enum.TryParse<ProductCondition>(r.Condition, true, out _)) Err("condition", "Choose New, Used or Refurbished.");
        if ((r.ConditionNote?.Length ?? 0) > 300) Err("conditionNote", "Maximum 300 characters.");
        if (Trim(r.Sku) is { } sku && !ValidSku(sku)) Err("sku", "Use letters, numbers and . - _ / # (max 64 characters).");
        if (Trim(r.Handle) is { } h && (h.Length > 100 || !HandleRe().IsMatch(h))) Err("handle", "Use lowercase letters, numbers and single hyphens (max 100).");
        Money_("price", r.Price); Money_("compareAtPrice", r.CompareAtPrice); Money_("salePrice", r.SalePrice); Money_("cost", r.Cost, 0m);
        if (r.Price is { } pr && r.CompareAtPrice is { } cp && cp <= pr) Err("compareAtPrice", "The compare-at price must be higher than your price.");
        if (r.SalePrice is { } sp)
        {
            if (r.Price is null) Err("salePrice", "Set the regular price first.");
            else if (sp >= r.Price) Err("salePrice", "The sale price must be lower than the regular price.");
        }
        if ((r.SaleStart is not null || r.SaleEnd is not null) && r.SalePrice is null) Err("saleStart", "Add a sale price to use sale dates.");
        if (r.SaleStart is { } ss && r.SaleEnd is { } se && se <= ss) Err("saleEnd", "The sale must end after it starts.");
        Int("quantity", r.Quantity, 0, InventoryService.MaxStock, "Quantity"); Int("lowStockAlert", r.LowStockAlert, 0, InventoryService.MaxStock, "Low-stock alert");
        Int("maxPerOrder", r.MaxPerOrder, 1, 1000, "Max per order");
        if (Barcode.Strip(r.Barcode).Length > 20) Err("barcode", "A barcode has at most 20 characters.");
        if (r.Bullets is { Count: > 5 }) Err("bullets", "Up to 5 key features.");
        else if (r.Bullets?.Any(b => b.Trim().Length > 500) == true) Err("bullets", "Each feature can have up to 500 characters.");
        if ((r.Description?.Length ?? 0) > 10_000) Err("description", "Maximum 10,000 characters.");
        Int("weightGrams", r.WeightGrams, 1, 1_000_000, "Weight");
        foreach (var (f, v) in new[] { ("lengthCm", r.LengthCm), ("widthCm", r.WidthCm), ("heightCm", r.HeightCm) })
            if (v is not null && (v < 0.1m || v > 1000m)) Err(f, "Enter a size between 0.1 and 1000 cm.");
        Int("handlingDays", r.HandlingDays, 0, 60, "Handling time");
        if (r.Shipping is { } s) { Money_("shipping.standardPrice", s.StandardPrice, 0m, 9999.99m); Money_("shipping.expressPrice", s.ExpressPrice, 0m, 9999.99m); Money_("shipping.freeOver", s.FreeOver, 0m, 99999.99m); }
        Int("returnsDays", r.ReturnsDays, 0, 365, "Returns window");
        if ((r.ReturnsCondition?.Length ?? 0) > 300) Err("returnsCondition", "Maximum 300 characters.");
        Int("warrantyMonths", r.WarrantyMonths, 0, 600, "Warranty");
        if ((r.WarrantyNote?.Length ?? 0) > 300) Err("warrantyNote", "Maximum 300 characters.");
        if ((r.SearchTerms?.Length ?? 0) > 250) Err("searchTerms", "Maximum 250 characters.");
        if ((r.MetaTitle?.Length ?? 0) > 70) Err("metaTitle", "Maximum 70 characters.");
        if ((r.MetaDescription?.Length ?? 0) > 160) Err("metaDescription", "Maximum 160 characters.");
        return e;
    }

    /// <summary>Validates attribute values against the category configuration. Unknown keys are ignored (they belong to a previous category).</summary>
    public static (Dictionary<string, string> Values, Dictionary<string, string[]> Errors) ValidateAttributes(IReadOnlyList<CategoryAttribute> config, Dictionary<string, JsonElement>? input)
    {
        var values = new Dictionary<string, string>(); var errors = new Dictionary<string, string[]>();
        foreach (var (key, el) in input ?? [])
        {
            var a = config.FirstOrDefault(c => c.Key == key);
            if (a is null) continue;
            if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            string? err = null; object? clean = null;
            var opts = JsonSerializer.Deserialize<string[]>(a.OptionsJson) ?? [];
            switch (a.Type)
            {
                case "text" or "longtext":
                    if (el.ValueKind != JsonValueKind.String) { err = "Enter text."; break; }
                    var t = el.GetString()!.Trim();
                    if (t.Length > (a.Type == "text" ? 200 : 2000)) err = $"Maximum {(a.Type == "text" ? 200 : 2000)} characters.";
                    else if (t.Length > 0) clean = t;
                    break;
                case "number":
                    if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ds)) clean = ds;
                    else if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var dn)) clean = dn;
                    else if (el.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(el.GetString())) break;
                    else { err = "Enter a number."; break; }
                    if (clean is double dd && (double.IsNaN(dd) || Math.Abs(dd) > 1e9)) { err = "Enter a smaller number."; clean = null; }
                    break;
                case "bool":
                    if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) clean = el.GetBoolean(); else err = "Choose yes or no.";
                    break;
                case "select":
                    if (el.ValueKind != JsonValueKind.String) { err = "Choose an option."; break; }
                    var sv = el.GetString()!;
                    if (sv.Length == 0) break;
                    if (!opts.Contains(sv)) err = "Choose one of the listed options."; else clean = sv;
                    break;
                case "multiselect":
                    if (el.ValueKind != JsonValueKind.Array) { err = "Choose from the list."; break; }
                    var items = el.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "\u0000").ToList();
                    if (items.Count > 30 || items.Any(i => !opts.Contains(i))) err = "Choose only from the listed options."; else if (items.Count > 0) clean = items.Distinct().ToArray();
                    break;
            }
            if (err is not null) errors[$"attributes.{key}"] = [err];
            else if (clean is not null) values[key] = JsonSerializer.Serialize(clean);
        }
        return (values, errors);
    }

    public static string Slugify(string s) => CategorySeed.Slugify(s);

    public static string? NormSku(string? sku) => string.IsNullOrWhiteSpace(sku) ? null : sku.Trim().ToLowerInvariant();

    /// <summary>Friendly pre-check (the unique index on <see cref="SkuClaim"/> is the real guard): is the SKU used by another product or any variation of another product?</summary>
    public static async Task<string?> SkuConflictAsync(AppDbContext db, Guid sellerId, string? sku, Guid? exceptProduct, Guid? exceptVariant = null)
    {
        var n = NormSku(sku);
        if (n is null) return null;
        var taken = await db.SkuClaims.AnyAsync(c => c.SellerId == sellerId && c.Sku == n && c.ProductId != exceptProduct);
        return taken ? "You already use this SKU." : null;
    }

    /// <summary>Makes the claim rows of one product match its current SKU + variation SKUs. A SKU used twice inside the product is a validation error (returned).</summary>
    public static async Task<string?> SyncSkuClaimsAsync(AppDbContext db, Product p)
    {
        var want = new List<(string Sku, Guid? Variant)>();
        if (NormSku(p.Sku) is { } ps) want.Add((ps, null));
        foreach (var v in p.Variants) if (NormSku(v.Sku) is { } vs) want.Add((vs, v.Id));
        if (want.Select(w => w.Sku).Distinct().Count() != want.Count) return "Each SKU can be used only once: the product and its variations all need different SKUs.";
        var have = await db.SkuClaims.Where(c => c.ProductId == p.Id).ToListAsync();
        foreach (var c in have.Where(c => !want.Any(w => w.Sku == c.Sku && w.Variant == c.VariantId))) db.SkuClaims.Remove(c);
        foreach (var w in want.Where(w => !have.Any(c => c.Sku == w.Sku && c.VariantId == w.Variant)))
            db.SkuClaims.Add(new SkuClaim { SellerId = p.SellerId, ProductId = p.Id, Sku = w.Sku, VariantId = w.Variant });
        return null;
    }

    /// <summary>Applies a validated main-document write to the tracked product (no inventory/attribute side effects).</summary>
    public static void Apply(Product p, ProductWrite r, long? salePriceCents)
    {
        p.Title = r.Title?.Trim() ?? "";
        p.Handle = Trim(r.Handle);
        p.Condition = Enum.TryParse<ProductCondition>(r.Condition, true, out var c) ? c : ProductCondition.New;
        p.ConditionNote = Trim(r.ConditionNote);
        p.Sku = Trim(r.Sku);
        p.PriceCents = Money.ToCents(r.Price); p.CompareAtCents = Money.ToCents(r.CompareAtPrice); p.SaleCents = salePriceCents;
        p.SaleStart = r.SalePrice is null ? null : r.SaleStart?.ToUniversalTime(); p.SaleEnd = r.SalePrice is null ? null : r.SaleEnd?.ToUniversalTime();
        p.CostCents = Money.ToCents(r.Cost);
        p.LowStockAlert = r.LowStockAlert; p.AllowBackorder = r.AllowBackorder; p.MaxPerOrder = r.MaxPerOrder;
        var bc = Barcode.Strip(r.Barcode);
        p.Barcode = bc.Length == 0 ? null : bc; p.NoBarcode = bc.Length == 0 && r.NoBarcode;
        p.BulletsJson = JsonSerializer.Serialize((r.Bullets ?? []).Select(b => b.Trim()).Where(b => b.Length > 0).Take(5).ToList());
        p.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        p.WeightGrams = r.WeightGrams;
        int? Mm(decimal? cm) => cm is null ? null : (int)Math.Round(cm.Value * 10m);
        p.LengthMm = Mm(r.LengthCm); p.WidthMm = Mm(r.WidthCm); p.HeightMm = Mm(r.HeightCm);
        p.HandlingDays = r.HandlingDays;
        var s = r.Shipping ?? new ShippingWrite();
        p.ShippingJson = JsonSerializer.Serialize(new ShippingSettings(s.StandardEnabled, Money.ToCents(s.StandardPrice), s.ExpressEnabled, Money.ToCents(s.ExpressPrice), Money.ToCents(s.FreeOver)), ProductJson.Opts);
        p.LocalPickup = r.LocalPickup;
        p.ReturnsDays = r.ReturnsDays; p.ReturnsCondition = Trim(r.ReturnsCondition); p.WarrantyMonths = r.WarrantyMonths; p.WarrantyNote = Trim(r.WarrantyNote);
        p.SearchTerms = Trim(r.SearchTerms); p.MetaTitle = Trim(r.MetaTitle); p.MetaDescription = Trim(r.MetaDescription);
        p.HasVariants = r.HasVariants;
    }
}
