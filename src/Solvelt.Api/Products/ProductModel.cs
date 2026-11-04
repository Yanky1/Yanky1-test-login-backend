using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Products;

public static class ProductModel
{
    public static void ConfigureProducts(this ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.ParentId);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Slug).HasMaxLength(120);
        });
        b.Entity<CategoryAttribute>(e =>
        {
            e.ToTable("category_attributes");
            e.HasIndex(x => new { x.CategoryId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Label).HasMaxLength(120);
            e.Property(x => x.Type).HasMaxLength(16);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.HasIndex(x => new { x.SellerId, x.Status });
            e.HasIndex(x => new { x.SellerId, x.BrandId });
            e.HasIndex(x => new { x.SellerId, x.Sku }); // plain lookup index: uniqueness lives in sku_claims (case-insensitive, shared with variations)
            e.HasIndex(x => new { x.SellerId, x.Handle });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Condition).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Handle).HasMaxLength(120);
            e.Property(x => x.Barcode).HasMaxLength(20);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.ImageSeq).IsConcurrencyToken();
            e.HasIndex(x => x.CatalogVersion).IsUnique().HasFilter("\"CatalogVersion\" > 0");   // versions are handed out one per listing: duplicates mean the counter was bypassed
            e.Property(x => x.CatalogUnlistReason).HasMaxLength(24);
            e.Property(x => x.CatalogStockState).HasMaxLength(64);
            e.Property(x => x.CatalogDeliveryState).HasMaxLength(64);   // SHA-256 of the per-variant availability signature
            e.HasOne<SellerAccount>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Options).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Variants).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Images).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Attributes).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Faqs).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CatalogCounter>(e => { e.ToTable("catalog_counter"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.HasData(new CatalogCounter { Id = 1, Value = 0 }); });
        b.Entity<CatalogRefreshRequest>(e => { e.ToTable("catalog_refresh_requests"); e.HasIndex(x => new { x.ProcessedAt, x.CreatedAt }); });
        b.Entity<SkuClaim>(e =>
        {
            e.ToTable("sku_claims");
            e.HasIndex(x => new { x.SellerId, x.Sku }).IsUnique();
            e.HasIndex(x => x.ProductId);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProductOption>(e => { e.ToTable("product_options"); e.Property(x => x.Name).HasMaxLength(40); });
        b.Entity<ProductVariant>(e =>
        {
            e.ToTable("product_variants");
            e.HasIndex(x => new { x.ProductId, x.ComboKey }).IsUnique();
            e.HasIndex(x => new { x.SellerId, x.Sku });
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.ComboKey).HasMaxLength(400);
            e.Property(x => x.Barcode).HasMaxLength(20);
        });
        b.Entity<ProductImage>(e =>
        {
            e.ToTable("product_images");
            e.HasIndex(x => new { x.ProductId, x.VariantId });
            e.Property(x => x.StorageKey).HasMaxLength(40);
            e.Property(x => x.Alt).HasMaxLength(200);
            e.Property(x => x.ContentType).HasMaxLength(32);
        });
        b.Entity<ProductAttributeValue>(e =>
        {
            e.ToTable("product_attribute_values");
            e.HasIndex(x => new { x.ProductId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64);
        });
        b.Entity<ProductFaq>(e => { e.ToTable("product_faqs"); e.Property(x => x.Question).HasMaxLength(200); e.Property(x => x.Answer).HasMaxLength(1000); });
        b.Entity<ProductQuestion>(e =>
        {
            e.ToTable("product_questions");
            e.HasIndex(x => new { x.SellerId, x.State });
            e.HasIndex(x => x.ProductId);
            e.HasIndex(x => x.ExternalId).IsUnique();   // NULLs (dev/test questions) never collide
            e.Property(x => x.ExternalId).HasMaxLength(100);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.AskerName).HasMaxLength(80);
            e.Property(x => x.Text).HasMaxLength(1000);
            e.Property(x => x.Answer).HasMaxLength(2000);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
