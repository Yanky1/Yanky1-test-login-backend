using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Products;

public static class Money
{
    public static long? ToCents(decimal? d) => d is null ? null : (long)Math.Round(d.Value * 100m, 0, MidpointRounding.AwayFromZero);
    public static decimal? FromCents(long? c) => c is null ? null : c.Value / 100m;
}

public record ShippingSettings(bool StandardEnabled = true, long? StandardCents = null, bool ExpressEnabled = false, long? ExpressCents = null, long? FreeOverCents = null);

public record Issue(string Key, string Section, string Severity, string Message);
public record Readiness(int Score, bool CanPublish, List<Issue> Issues);

public static class ProductJson
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    public static List<string> Bullets(Product p) => JsonSerializer.Deserialize<List<string>>(p.BulletsJson, Opts) ?? [];
    public static ShippingSettings Shipping(Product p) { try { return JsonSerializer.Deserialize<ShippingSettings>(p.ShippingJson, Opts) ?? new(); } catch (JsonException) { return new(); } }
    public static string[] Values(string json) => JsonSerializer.Deserialize<string[]>(json, Opts) ?? [];
}

public static class ProductRules
{
    /// <summary>Sale price in effect right now (null when not on sale).</summary>
    public static long? EffectiveSale(Product p, DateTime now) =>
        p.SaleCents is { } s && (p.SaleStart is null || p.SaleStart <= now) && (p.SaleEnd is null || now < p.SaleEnd) ? s : null;

    public static long? CurrentPriceCents(Product p, DateTime now) => EffectiveSale(p, now) ?? p.PriceCents;

    public static int AvailableStock(Product p) =>
        p.HasVariants ? p.Variants.Where(v => v.Enabled).Sum(v => Math.Max(0, v.Stock - v.Reserved)) : Math.Max(0, p.Stock - p.Reserved);

    public static bool InStock(Product p) =>
        p.AllowBackorder || (p.HasVariants ? p.Variants.Any(v => v.Enabled && v.Stock - v.Reserved > 0) : p.Stock - p.Reserved > 0);

    /// <summary>The structured "missing / weak" list that drives the publish gate, the checklist and the quality score. Product must be loaded with its children.</summary>
    public static Readiness Evaluate(Product p, IReadOnlyList<CategoryAttribute> attrs)
    {
        var issues = new List<Issue>();
        var earned = 0; var total = 0;
        void Check(bool ok, int points, string key, string section, bool required, string message)
        {
            total += points;
            if (ok) earned += points; else issues.Add(new(key, section, required ? "required" : "recommended", message));
        }
        var title = p.Title.Trim();
        Check(title.Length >= 5, 8, "title", "basics", true, "Add a title (at least 5 characters).");
        if (title.Length >= 5) Check(title.Length is >= 60 and <= 150, 7, "title_length", "basics", false, "Good titles are 60-150 characters: brand + product + key attribute.");
        Check(p.CategoryId is not null, 8, "category", "basics", true, "Choose a category.");
        if (p.Condition != ProductCondition.New) Check(!string.IsNullOrWhiteSpace(p.ConditionNote), 3, "condition_note", "basics", false, "Describe the condition (wear, included parts) so buyers know what to expect.");
        var main = p.Images.Where(i => i.VariantId == null).ToList();
        Check(main.Count >= 1, 12, "images", "media", true, "Add at least one photo.");
        Check(main.Count >= 3, 6, "images_more", "media", false, "Listings with 3 or more photos sell better.");
        if (main.Count > 0)
        {
            Check(main.All(i => Math.Min(i.Width, i.Height) >= 1000), 4, "images_resolution", "media", false, "Use photos at least 1000 px on the shortest side so shoppers can zoom.");
            Check(main.All(i => !string.IsNullOrWhiteSpace(i.Alt)), 3, "images_alt", "media", false, "Add short alt text to each photo for shoppers using screen readers.");
        }
        var bullets = ProductJson.Bullets(p);
        Check(bullets.Count >= 3, 6, "bullets", "description", false, "Add 3-5 key feature bullets.");
        Check((p.Description?.Trim().Length ?? 0) >= 150, 6, "description", "description", false, "Write a description of at least 150 characters.");

        if (p.Type == ListingType.Service)
        {
            // services replace price/stock/variants/shipping with the service sections (Bookings/ServiceRules.cs)
            Bookings.ServiceRules.Check(p.Service, Check);
            var sHave = p.Attributes.ToDictionary(a => a.Key, a => a.ValueJson);
            foreach (var a in attrs.Where(a => a.Required))
                Check(sHave.TryGetValue(a.Key, out var v) && !IsEmpty(v), 6, "attr:" + a.Key, "details", true, $"{a.Label} is required for this category.");
            Check(!string.IsNullOrWhiteSpace(p.SearchTerms), 2, "search_terms", "seo", false, "Add search keywords shoppers might use.");
            var sScore = total == 0 ? 0 : (int)Math.Round(100.0 * earned / total);
            return new(sScore, issues.All(i => i.Severity != "required"), issues);
        }

        var enabled = p.Variants.Where(v => v.Enabled).ToList();
        if (p.HasVariants)
        {
            var optsOk = p.Options.Count > 0 && p.Options.All(o => ProductJson.Values(o.ValuesJson).Length > 0);
            Check(optsOk && enabled.Count > 0, 6, "variants", "variations", true, "Add at least one option with values and keep at least one variation enabled.");
            Check(enabled.Count > 0 && (p.PriceCents is > 0 || enabled.All(v => v.PriceCents is > 0)), 10, "price", "price", true, "Set a price (or a price for every variation).");
            Check(enabled.Any(v => v.Stock - v.Reserved > 0) || p.AllowBackorder, 6, "stock", "price", true, "Add stock to at least one variation (or allow backorders).");
        }
        else
        {
            Check(p.PriceCents is > 0, 10, "price", "price", true, "Set a price.");
            Check(p.Stock - p.Reserved > 0 || p.AllowBackorder, 6, "stock", "price", true, "Add stock (or allow backorders).");
        }
        Check(!string.IsNullOrWhiteSpace(p.Sku), 3, "sku", "price", false, "Add a SKU to find this item quickly.");

        // identifiers
        if (!string.IsNullOrEmpty(p.Barcode))
        {
            var bc = Barcode.Check(p.Barcode);
            Check(bc.Valid, 5, "barcode", "identifiers", true, bc.Error ?? "The barcode isn't valid.");
        }
        else Check(p.NoBarcode, 5, "barcode", "identifiers", false, "Add a barcode (UPC/EAN/ISBN), or switch on \"This product has no barcode\".");

        // category-driven details
        var req = attrs.Where(a => a.Required).ToList();
        var have = p.Attributes.ToDictionary(a => a.Key, a => a.ValueJson);
        foreach (var a in req)
            Check(have.TryGetValue(a.Key, out var v) && !IsEmpty(v), 6, "attr:" + a.Key, "details", true, $"{a.Label} is required for this category.");
        var optional = attrs.Where(a => !a.Required).ToList();
        if (optional.Count > 0)
            Check(optional.Count(a => have.TryGetValue(a.Key, out var v) && !IsEmpty(v)) * 2 >= optional.Count, 4, "details_more", "details", false, "Fill in more product details - they power search filters.");

        Check(p.WeightGrams is > 0, 4, "weight", "shipping", false, "Add the package weight so shipping can be calculated.");
        Check(p.HandlingDays is not null, 3, "handling", "shipping", false, "Tell shoppers how many days you need to ship.");
        Check(p.ReturnsDays is not null, 3, "returns", "policies", false, "State your returns window (0 days = no returns).");
        Check(!string.IsNullOrWhiteSpace(p.SearchTerms), 2, "search_terms", "seo", false, "Add search keywords shoppers might use.");

        var score = total == 0 ? 0 : (int)Math.Round(100.0 * earned / total);
        return new(score, issues.All(i => i.Severity != "required"), issues);
    }

    private static bool IsEmpty(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => true,
                JsonValueKind.String => string.IsNullOrWhiteSpace(d.RootElement.GetString()),
                JsonValueKind.Array => d.RootElement.GetArrayLength() == 0,
                _ => false
            };
        }
        catch (JsonException) { return true; }
    }

    public static async Task<Readiness> RefreshAsync(AppDbContext db, CategoryService cats, Product p)
    {
        var r = Evaluate(p, await cats.AttributesAsync(p.CategoryId));
        p.QualityScore = r.Score; p.IsReady = r.CanPublish;
        return r;
    }
}

/// <summary>Option-value combinations (cartesian product) with stable keys.</summary>
public static class VariantGenerator
{
    public const int MaxOptions = 3, MaxValuesPerOption = 20, MaxVariants = 100;

    public static string Key(IEnumerable<string> values) => string.Join('|', values.Select(v => v.Trim().ToLowerInvariant()));

    public static List<string[]> Combos(IReadOnlyList<string[]> optionValues)
    {
        List<string[]> acc = [[]];
        foreach (var vals in optionValues) acc = acc.SelectMany(a => vals.Select(v => a.Append(v).ToArray())).ToList();
        return optionValues.Count == 0 ? [] : acc;
    }
}

public record InventoryResult(bool Ok, string? Error, int OnHand = 0, int Reserved = 0);

/// <summary>
/// The ONLY place that changes stock after a listing exists. All operations are single atomic UPDATE statements with the guard in the WHERE clause,
/// so concurrent orders can never oversell or drive counts negative. Targets a variant when variantId is given, else the (variation-less) product.
/// Available = OnHand - Reserved. Reserve(qty) holds stock for an order, Release returns it, Commit consumes it (ship), Adjust changes on-hand (+/-).
/// </summary>
public interface IInventoryService
{
    Task<InventoryResult> ReserveAsync(Guid sellerId, Guid productId, Guid? variantId, int quantity, CancellationToken ct = default);
    Task<InventoryResult> ReleaseAsync(Guid sellerId, Guid productId, Guid? variantId, int quantity, CancellationToken ct = default);
    Task<InventoryResult> CommitAsync(Guid sellerId, Guid productId, Guid? variantId, int quantity, CancellationToken ct = default);
    Task<InventoryResult> AdjustAsync(Guid sellerId, Guid productId, Guid? variantId, int delta, CancellationToken ct = default);
    Task<InventoryResult> SetOnHandAsync(Guid sellerId, Guid productId, Guid? variantId, int onHand, CancellationToken ct = default);
    /// <summary>Resolves an order line by SKU (product SKU or variant SKU) within a seller.</summary>
    Task<(Guid ProductId, Guid? VariantId)?> FindBySkuAsync(Guid sellerId, string sku, CancellationToken ct = default);
}

public class InventoryService(AppDbContext db) : IInventoryService
{
    public const int MaxStock = 1_000_000;

    public async Task<InventoryResult> ReserveAsync(Guid sellerId, Guid productId, Guid? variantId, int q, CancellationToken ct = default)
    {
        if (q <= 0) return new(false, "invalid_quantity");
        var n = variantId is { } vid
            ? await db.Variants.Where(v => v.Id == vid && v.ProductId == productId && v.SellerId == sellerId && v.Enabled
                    && (v.Stock - v.Reserved >= q || db.Products.Any(p => p.Id == productId && p.AllowBackorder)))
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Reserved, v => v.Reserved + q), ct)
            : await db.Products.Where(p => p.Id == productId && p.SellerId == sellerId && !p.HasVariants && (p.Stock - p.Reserved >= q || p.AllowBackorder))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Reserved, p => p.Reserved + q), ct);
        return n == 1 ? await SnapshotAsync(productId, variantId, ct) : new(false, "insufficient_stock");
    }

    public async Task<InventoryResult> ReleaseAsync(Guid sellerId, Guid productId, Guid? variantId, int q, CancellationToken ct = default)
    {
        if (q <= 0) return new(false, "invalid_quantity");
        var n = variantId is { } vid
            ? await db.Variants.Where(v => v.Id == vid && v.ProductId == productId && v.SellerId == sellerId && v.Reserved >= q)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Reserved, v => v.Reserved - q), ct)
            : await db.Products.Where(p => p.Id == productId && p.SellerId == sellerId && p.Reserved >= q)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Reserved, p => p.Reserved - q), ct);
        return n == 1 ? await SnapshotAsync(productId, variantId, ct) : new(false, "not_reserved");
    }

    public async Task<InventoryResult> CommitAsync(Guid sellerId, Guid productId, Guid? variantId, int q, CancellationToken ct = default)
    {
        if (q <= 0) return new(false, "invalid_quantity");
        // on-hand never goes below zero (backordered units that were never in stock clamp at 0); reserved never below zero
        var n = variantId is { } vid
            ? await db.Variants.Where(v => v.Id == vid && v.ProductId == productId && v.SellerId == sellerId && v.Reserved >= q)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Reserved, v => v.Reserved - q).SetProperty(v => v.Stock, v => v.Stock >= q ? v.Stock - q : 0), ct)
            : await db.Products.Where(p => p.Id == productId && p.SellerId == sellerId && p.Reserved >= q)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Reserved, p => p.Reserved - q).SetProperty(p => p.Stock, p => p.Stock >= q ? p.Stock - q : 0), ct);
        return n == 1 ? await SnapshotAsync(productId, variantId, ct) : new(false, "not_reserved");
    }

    public async Task<InventoryResult> AdjustAsync(Guid sellerId, Guid productId, Guid? variantId, int delta, CancellationToken ct = default)
    {
        // on-hand may never drop below what open orders have reserved (nor below 0, nor above the cap)
        var n = variantId is { } vid
            ? await db.Variants.Where(v => v.Id == vid && v.ProductId == productId && v.SellerId == sellerId && v.Stock + delta >= v.Reserved && v.Stock + delta >= 0 && v.Stock + delta <= MaxStock)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Stock, v => v.Stock + delta), ct)
            : await db.Products.Where(p => p.Id == productId && p.SellerId == sellerId && p.Stock + delta >= p.Reserved && p.Stock + delta >= 0 && p.Stock + delta <= MaxStock)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + delta), ct);
        return n == 1 ? await SnapshotAsync(productId, variantId, ct) : await WhyNotAsync(productId, variantId, null, ct);
    }

    public async Task<InventoryResult> SetOnHandAsync(Guid sellerId, Guid productId, Guid? variantId, int onHand, CancellationToken ct = default)
    {
        if (onHand is < 0 or > MaxStock) return new(false, "out_of_range");
        var n = variantId is { } vid
            ? await db.Variants.Where(v => v.Id == vid && v.ProductId == productId && v.SellerId == sellerId && onHand >= v.Reserved).ExecuteUpdateAsync(s => s.SetProperty(v => v.Stock, onHand), ct)
            : await db.Products.Where(p => p.Id == productId && p.SellerId == sellerId && onHand >= p.Reserved).ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, onHand), ct);
        return n == 1 ? await SnapshotAsync(productId, variantId, ct) : await WhyNotAsync(productId, variantId, onHand, ct);
    }

    /// <summary>Explains a refused stock change: not found, below what open orders reserved ("below_reserved", with the counts), or out of range.</summary>
    private async Task<InventoryResult> WhyNotAsync(Guid productId, Guid? variantId, int? target, CancellationToken ct)
    {
        var snap = variantId is { } vid
            ? await db.Variants.AsNoTracking().Where(x => x.Id == vid && x.ProductId == productId).Select(x => new { x.Stock, x.Reserved }).FirstOrDefaultAsync(ct)
            : await db.Products.AsNoTracking().Where(x => x.Id == productId).Select(x => new { x.Stock, x.Reserved }).FirstOrDefaultAsync(ct);
        if (snap is null) return new(false, "not_found");
        return target is { } t && t < snap.Reserved || target is null ? new(false, "below_reserved", snap.Stock, snap.Reserved) : new(false, "out_of_range", snap.Stock, snap.Reserved);
    }

    public async Task<(Guid ProductId, Guid? VariantId)?> FindBySkuAsync(Guid sellerId, string sku, CancellationToken ct = default)
    {
        var s = sku.Trim().ToLower();
        var v = await db.Variants.AsNoTracking().Where(x => x.SellerId == sellerId && x.Sku != null && x.Sku.ToLower() == s).Select(x => new { x.ProductId, x.Id }).FirstOrDefaultAsync(ct);
        if (v is not null) return (v.ProductId, v.Id);
        var p = await db.Products.AsNoTracking().Where(x => x.SellerId == sellerId && x.Sku != null && x.Sku.ToLower() == s).Select(x => x.Id).FirstOrDefaultAsync(ct);
        return p == Guid.Empty ? null : (p, null);
    }

    private async Task<InventoryResult> SnapshotAsync(Guid productId, Guid? variantId, CancellationToken ct)
    {
        if (variantId is { } vid)
        {
            var v = await db.Variants.AsNoTracking().Where(x => x.Id == vid).Select(x => new { x.Stock, x.Reserved }).FirstAsync(ct);
            return new(true, null, v.Stock, v.Reserved);
        }
        var p = await db.Products.AsNoTracking().Where(x => x.Id == productId).Select(x => new { x.Stock, x.Reserved }).FirstAsync(ct);
        return new(true, null, p.Stock, p.Reserved);
    }
}
