using Microsoft.EntityFrameworkCore;

namespace Solvelt.Api.Offers;

public enum OfferStatus { Draft, Active, Ended }
public enum OfferScope { All, Category, Products }

/// <summary>
/// A seller's quantity offer (C18, docs/CONTRACT.md): per-unit price tiers by minimum quantity and free gifts at a minimum quantity, counted across the eligible products of the
/// offer's brand in one order. The row is the offer's identity and its current state; every edit (including ending it) writes a new immutable <see cref="OfferVersion"/>, so an
/// order can always name the exact version it was priced with. <see cref="CurrentVersion"/> is also the optimistic-concurrency claim counter.
/// </summary>
public class Offer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    /// <summary>The brand the offer belongs to (brand scope like products): eligible products and gift products are products of this brand.</summary>
    public Guid BrandId { get; set; }
    public string Name { get; set; } = "";
    public OfferStatus Status { get; set; } = OfferStatus.Draft;
    public int CurrentVersion { get; set; }
    /// <summary>Position in the catalog feed (same counter as listing versions): bumped in the same transaction as every change the marketplace can see.</summary>
    public long FeedVersion { get; set; }
    /// <summary>The offer has been in the feed (an active version existed): only such offers get an ended entry in the feed.</summary>
    public bool EverListed { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    /// <summary>ended (the seller ended it). A removed offer (demo data) is deleted and leaves a <c>catalog_tombstones</c> row of kind <c>offer</c> for the feed.</summary>
    public string? EndedReason { get; set; }
}

/// <summary>One immutable version of an offer. Never edited after it is written, except <see cref="SupersededAt"/>, set once when the next version replaces it.</summary>
public class OfferVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OfferId { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public OfferStatus Status { get; set; }
    public OfferScope Scope { get; set; }
    /// <summary>Category ids (scope Category) or product ids (scope Products), JSON array; <c>[]</c> for All.</summary>
    public string ScopeIdsJson { get; set; } = "[]";
    /// <summary>[{minQuantity, unitPriceCents}] ordered by minQuantity.</summary>
    public string TiersJson { get; set; } = "[]";
    /// <summary>[{minQuantity, productId, quantity}].</summary>
    public string GiftsJson { get; set; } = "[]";
    public DateTime? ActiveFrom { get; set; }
    public DateTime? ActiveTo { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? SupersededAt { get; set; }
}

public static class OfferModelConfig
{
    public static void ConfigureOffers(this ModelBuilder b)
    {
        b.Entity<Offer>(e =>
        {
            e.ToTable("offers");
            e.HasIndex(x => new { x.SellerId, x.BrandId, x.Status });
            e.HasIndex(x => x.FeedVersion);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.EndedReason).HasMaxLength(20);
        });
        b.Entity<OfferVersion>(e =>
        {
            e.ToTable("offer_versions");
            e.HasOne<Offer>().WithMany().HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OfferId, x.Version }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(10);
        });
    }
}

public class OfferOptions
{
    public const string Section = "Offers";
    /// <summary>An order placed this many minutes after an offer version stopped applying is still accepted with that version (carts priced just before an edit).</summary>
    public int VersionGraceMinutes { get; set; } = 15;
}
