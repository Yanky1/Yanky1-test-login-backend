namespace Solvelt.Api.Domain;

public enum ProductStatus { Draft, Active, Inactive, Archived }
public enum ProductCondition { New, Used, Refurbished }
public enum QuestionState { Pending, Published, Hidden }

public class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>Data-driven product detail field of a category (inherited by descendants). Type: text, longtext, number, select, multiselect, bool.</summary>
public class CategoryAttribute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CategoryId { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public string OptionsJson { get; set; } = "[]";
    public bool Required { get; set; }
    public string? Group { get; set; }
    public string? Help { get; set; }
    public string? Unit { get; set; }
    public int SortOrder { get; set; }
}

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public Guid? CategoryId { get; set; }
    public ProductStatus Status { get; set; } = ProductStatus.Draft;
    /// <summary>Product (default) or Service (workstream F: service details live in <see cref="ServiceDetails"/>).</summary>
    public ListingType Type { get; set; } = ListingType.Product;
    /// <summary>Set while Solvelt staff keep the listing hidden; the seller sees the reason and cannot reactivate it.</summary>
    public DateTime? ModeratedAt { get; set; }
    public string? ModerationReason { get; set; }
    public Guid? ModeratedBy { get; set; }
    public ProductStatus? ModerationPrevStatus { get; set; }
    public string Title { get; set; } = "";
    public string? Handle { get; set; }
    public ProductCondition Condition { get; set; } = ProductCondition.New;
    public string? ConditionNote { get; set; }
    public string? Sku { get; set; }
    // money in minor units (cents), single currency (USD) for now
    public long? PriceCents { get; set; }
    public long? CompareAtCents { get; set; }
    public long? SaleCents { get; set; }
    public DateTime? SaleStart { get; set; }
    public DateTime? SaleEnd { get; set; }
    public long? CostCents { get; set; }
    // stock (changed only through IInventoryService after creation of the listing)
    public int Stock { get; set; }
    public int Reserved { get; set; }
    public int? LowStockAlert { get; set; }
    public bool AllowBackorder { get; set; }
    public int? MaxPerOrder { get; set; }
    public string? Barcode { get; set; }
    public bool NoBarcode { get; set; }
    public string BulletsJson { get; set; } = "[]";
    public string? Description { get; set; } // Markdown source; rendered safely on read
    // shipping
    public int? WeightGrams { get; set; }
    public int? LengthMm { get; set; }
    public int? WidthMm { get; set; }
    public int? HeightMm { get; set; }
    public int? HandlingDays { get; set; }
    /// <summary>Default package preset for labels (Shipping/PackagePreset).</summary>
    public Guid? DefaultPackageId { get; set; }
    public string ShippingJson { get; set; } = "{}";
    public bool LocalPickup { get; set; }
    // policies
    public int? ReturnsDays { get; set; }
    public string? ReturnsCondition { get; set; }
    public int? WarrantyMonths { get; set; }
    public string? WarrantyNote { get; set; }
    // search / SEO
    public string? SearchTerms { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public bool HasVariants { get; set; }
    /// <summary>Denormalised listing quality (0-100) and publish readiness; refreshed on every change, used by list filters/sorting.</summary>
    public int QualityScore { get; set; }
    public bool IsReady { get; set; }
    /// <summary>Optimistic concurrency token (incremented by the application on every main-document save; portable across SQLite/PostgreSQL).</summary>
    public int Version { get; set; } = 1;
    // ---- marketplace catalog feed (see Catalog/ and docs/CATALOG.md); maintained ONLY by CatalogTracking, never by endpoints ----
    /// <summary>Monotonic, globally unique version of this listing's public catalog document (0 = never listed). Bumped in the same transaction as every relevant change.</summary>
    public long CatalogVersion { get; set; }
    /// <summary>Whether the listing was visible to the marketplace as of <see cref="CatalogVersion"/>.</summary>
    public bool CatalogListed { get; set; }
    /// <summary>True once the listing has been visible at least once: from then on an unlisting is reported by the feed (as a tombstone).</summary>
    public bool CatalogEverListed { get; set; }
    /// <summary>Why the listing is not visible (not_active, archived, seller_suspended, moderated, brand_removed); null while listed.</summary>
    public string? CatalogUnlistReason { get; set; }
    /// <summary>Availability signature (per variant) as of the last stock-driven version; null = unknown, the next stock refresh bumps once.</summary>
    public string? CatalogStockState { get; set; }
    /// <summary>Delivery signature (ships / local delivery / service locations) as of the last version; null = unknown, the next refresh bumps once.</summary>
    public string? CatalogDeliveryState { get; set; }
    /// <summary>Concurrency token bumped by every image add/remove: parallel uploads can't both pass the 9-image check.</summary>
    public int ImageSeq { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public List<ProductOption> Options { get; set; } = [];
    public List<ProductVariant> Variants { get; set; } = [];
    public List<ProductImage> Images { get; set; } = [];
    public List<ProductAttributeValue> Attributes { get; set; } = [];
    public List<ProductFaq> Faqs { get; set; } = [];
    public ServiceDetails? Service { get; set; }
}

public class ProductOption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public string ValuesJson { get; set; } = "[]";
    public int SortOrder { get; set; }
}

public class ProductVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid SellerId { get; set; }
    /// <summary>JSON array of values, same order as the product's options.</summary>
    public string ValuesJson { get; set; } = "[]";
    /// <summary>Normalized key of the combination, e.g. "red|large" (unique per product).</summary>
    public string ComboKey { get; set; } = "";
    public string? Sku { get; set; }
    public long? PriceCents { get; set; }
    public int Stock { get; set; }
    public int Reserved { get; set; }
    public string? Barcode { get; set; }
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public class ProductImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid SellerId { get; set; }
    /// <summary>Null: a product-level image; set: an image of that variant (max 9 per variant).</summary>
    public Guid? VariantId { get; set; }
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Alt { get; set; }
    public int SortOrder { get; set; }
    public bool IsMain { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ProductAttributeValue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string Key { get; set; } = "";
    /// <summary>JSON-encoded value (string, number, bool or string array).</summary>
    public string ValueJson { get; set; } = "null";
}

public class ProductFaq
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>A customer question (created by the buyer site later; by a test/dev-only endpoint for now).</summary>
public class ProductQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid SellerId { get; set; }
    public string AskerName { get; set; } = "";
    /// <summary>The marketplace's questionId (idempotency key of POST /api/internal/v1/products/{id}/questions); null for dev/test questions.</summary>
    public string? ExternalId { get; set; }
    public string Text { get; set; } = "";
    public string? Answer { get; set; }
    public QuestionState State { get; set; } = QuestionState.Pending;
    public DateTime AskedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public Guid? AnsweredBy { get; set; }
}

/// <summary>
/// One row per SKU in use inside a seller account (products AND variations share the namespace, compared case-insensitively).
/// The unique index (SellerId, Sku) is what makes SKU uniqueness race-safe; <see cref="Sku"/> is stored trimmed + lower-cased.
/// </summary>
public class SkuClaim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string Sku { get; set; } = "";
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
}

/// <summary>Single-row counter that hands out catalog versions. Incrementing it takes the row lock until commit, so versions become visible in version order (feed cursors never skip).</summary>
public class CatalogCounter
{
    public int Id { get; set; } = 1;
    public long Value { get; set; }
}

/// <summary>
/// "Delivery settings of this account (and brand) changed": the catalog refresh job re-derives the delivery options of the listings in scope and bumps (and re-emits
/// <c>listing.upserted</c> for) the ones that actually changed. Written in the same transaction as the settings change, processed in the background in chunks.
/// </summary>
public class CatalogRefreshRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    /// <summary>Null: every listing of the account.</summary>
    public Guid? BrandId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
