using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Offers;

public sealed record OfferTier(int MinQuantity, long UnitPriceCents);
public sealed record OfferGift(int MinQuantity, Guid ProductId, int Quantity);

/// <summary>The write body of the seller API (create, update) and of the preview.</summary>
public sealed record OfferWrite(string? Name, Guid? BrandId, string? Status, string? Scope, List<Guid>? CategoryIds, List<Guid>? ProductIds,
    List<OfferTier>? Tiers, List<OfferGift>? Gifts, DateTime? ActiveFrom, DateTime? ActiveTo, int? Version = null,
    Guid? SampleProductId = null, List<int>? Quantities = null);

/// <summary>One validated, normalized version ready to be written (or previewed).</summary>
public sealed record OfferDraft(string Name, OfferStatus Status, OfferScope Scope, List<Guid> ScopeIds, List<OfferTier> Tiers, List<OfferGift> Gifts, DateTime? ActiveFrom, DateTime? ActiveTo);

public sealed record OfferResult(int Status, string Code, string Message, Offer? Offer = null, object? Extra = null);

/// <summary>Pure money rules of C18, shared by the preview, the feed and the order intake: integer cents only.</summary>
public static class OfferPricing
{
    public const int MaxTiers = 10, MaxGifts = 5, MaxScopeIds = 200, MaxGiftQuantity = 10, MaxMinQuantity = 10000;

    /// <summary>The highest step reached by <paramref name="units"/> eligible units, or null below the first step.</summary>
    public static OfferTier? TierFor(IReadOnlyList<OfferTier> tiers, int units) =>
        tiers.Where(t => t.MinQuantity <= units).OrderByDescending(t => t.MinQuantity).FirstOrDefault();

    /// <summary>A tier never raises a price: the unit price is min(the line's price, the tier price); the discount is the difference times the quantity.</summary>
    public static long LineDiscount(long unitPriceCents, int quantity, OfferTier? tier) =>
        tier is null ? 0 : Math.Max(0, unitPriceCents - tier.UnitPriceCents) * quantity;

    /// <summary>The gifts earned by <paramref name="units"/> eligible units (once per order, never stacked).</summary>
    public static List<OfferGift> GiftsFor(IReadOnlyList<OfferGift> gifts, int units) => gifts.Where(g => g.MinQuantity <= units).ToList();

    public static string Money(long cents) => (cents < 0 ? "-" : "") + "$" + (Math.Abs(cents) / 100).ToString("N0", CultureInfo.InvariantCulture) + "." + (Math.Abs(cents) % 100).ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>Specificity of a scope: products before category before all (lower wins).</summary>
    public static int Rank(OfferScope s) => s switch { OfferScope.Products => 0, OfferScope.Category => 1, _ => 2 };

    public static string ScopeName(OfferScope s) => s switch { OfferScope.Products => "products", OfferScope.Category => "category", _ => "all" };
    public static string StatusName(OfferStatus s) => s.ToString().ToLowerInvariant();
}

/// <summary>A version read back with its JSON parsed.</summary>
public sealed class OfferVersionView
{
    public required OfferVersion Row { get; init; }
    public required List<Guid> ScopeIds { get; init; }
    public required List<OfferTier> Tiers { get; init; }
    public required List<OfferGift> Gifts { get; init; }

    public static OfferVersionView Of(OfferVersion v) => new()
    {
        Row = v,
        ScopeIds = JsonSerializer.Deserialize<List<Guid>>(v.ScopeIdsJson, OrderJson.Opts) ?? [],
        Tiers = (JsonSerializer.Deserialize<List<OfferTier>>(v.TiersJson, OrderJson.Opts) ?? []).OrderBy(t => t.MinQuantity).ToList(),
        Gifts = JsonSerializer.Deserialize<List<OfferGift>>(v.GiftsJson, OrderJson.Opts) ?? []
    };

    /// <summary>When this version applied: from max(written, activeFrom) to min(superseded, activeTo); null when it never applied (draft / ended).</summary>
    public (DateTime From, DateTime To)? ActiveInterval
    {
        get
        {
            if (Row.Status != OfferStatus.Active) return null;
            var from = Row.ActiveFrom is { } af && af > Row.CreatedAt ? af : Row.CreatedAt;
            var to = Row.SupersededAt ?? DateTime.MaxValue;
            if (Row.ActiveTo is { } at && at < to) to = at;
            return to > from ? (from, to) : null;
        }
    }
}

/// <summary>Category tree helper: a category scope covers the named categories and all their descendants.</summary>
public sealed class CategoryTree(List<Category> all)
{
    private readonly ILookup<Guid?, Guid> _children = all.ToLookup(c => c.ParentId, c => c.Id);

    public HashSet<Guid> Expand(IEnumerable<Guid> roots)
    {
        var set = new HashSet<Guid>();
        var stack = new Stack<Guid>(roots);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!set.Add(id)) continue;
            foreach (var c in _children[id]) stack.Push(c);
        }
        return set;
    }

    public static async Task<CategoryTree> LoadAsync(AppDbContext db, CancellationToken ct = default) => new(await db.Categories.AsNoTracking().ToListAsync(ct));
}

/// <summary>
/// Quantity offers (C18): validation, versioned writes (each in one transaction with its catalog-feed version and its outbox event), the feed documents and the preview.
/// See docs/CONTRACT.md, docs/CATALOG.md section 11 and docs/DECISIONS.md (W1..).
/// </summary>
public class OfferService(AppDbContext db, Outbox outbox, TimeProvider time, IAuditLog audit)
{
    public static string Scope(Guid offerId) => "offer:" + offerId.ToString("N");

    // ---- eligibility ---------------------------------------------------------------------------------------------------------------
    /// <summary>Whether a product is eligible for a version of an offer: a physical product of the offer's seller and brand, inside the scope.</summary>
    public static bool Eligible(Offer o, OfferVersionView v, Product p, CategoryTree tree) =>
        p.SellerId == o.SellerId && p.BrandId == o.BrandId && p.Type == ListingType.Product && v.Row.Scope switch
        {
            OfferScope.Products => v.ScopeIds.Contains(p.Id),
            OfferScope.Category => p.CategoryId is { } c && tree.Expand(v.ScopeIds).Contains(c),
            _ => true
        };

    private IQueryable<Product> EligibleQuery(Guid sellerId, Guid brandId, OfferScope scope, List<Guid> ids, CategoryTree tree)
    {
        var q = db.Products.AsNoTracking().Where(p => p.SellerId == sellerId && p.BrandId == brandId && p.Type == ListingType.Product);
        switch (scope)
        {
            case OfferScope.Products: q = q.Where(p => ids.Contains(p.Id)); break;
            case OfferScope.Category: var cats = tree.Expand(ids).ToList(); q = q.Where(p => p.CategoryId != null && cats.Contains(p.CategoryId.Value)); break;
        }
        return q;
    }

    // ---- validation ----------------------------------------------------------------------------------------------------------------
    /// <summary>Validates and normalizes a write. Errors are keyed by field like the product API (400 validation problem).</summary>
    public async Task<(OfferDraft? Draft, Dictionary<string, string[]> Errors)> ValidateAsync(Guid sellerId, Guid brandId, OfferWrite w, bool preview = false)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string k, string m) => errors[k] = errors.TryGetValue(k, out var a) ? [.. a, m] : [m];

        var name = (w.Name ?? "").Trim();
        if (!preview && name.Length is 0 or > 80) Err("name", "Enter a name of 1 to 80 characters.");
        var status = OfferStatus.Draft;
        switch ((w.Status ?? "draft").Trim().ToLowerInvariant())
        {
            case "draft": break;
            case "active": status = OfferStatus.Active; break;
            default: Err("status", "Use draft or active (end an offer with the end action)."); break;
        }
        var scope = OfferScope.All;
        switch ((w.Scope ?? "all").Trim().ToLowerInvariant())
        {
            case "all": break;
            case "category": scope = OfferScope.Category; break;
            case "products": scope = OfferScope.Products; break;
            default: Err("scope", "Use all, category or products."); break;
        }
        var ids = scope switch
        {
            OfferScope.Category => (w.CategoryIds ?? []).Distinct().ToList(),
            OfferScope.Products => (w.ProductIds ?? []).Distinct().ToList(),
            _ => new List<Guid>()
        };
        if (scope == OfferScope.Category)
        {
            if (ids.Count is 0 or > 20) Err("categoryIds", "Choose 1 to 20 categories.");
            else if (await db.Categories.AsNoTracking().CountAsync(c => ids.Contains(c.Id)) != ids.Count) Err("categoryIds", "One of the categories does not exist.");
        }
        if (scope == OfferScope.Products)
        {
            if (ids.Count is 0 or > OfferPricing.MaxScopeIds) Err("productIds", $"Choose 1 to {OfferPricing.MaxScopeIds} products.");
            else if (await db.Products.AsNoTracking().CountAsync(p => ids.Contains(p.Id) && p.SellerId == sellerId && p.BrandId == brandId && p.Type == ListingType.Product) != ids.Count)
                Err("productIds", "Every product must be one of this brand's products (services can't be in an offer).");
        }

        var tiers = (w.Tiers ?? []).OrderBy(t => t.MinQuantity).ToList();
        if (tiers.Count > OfferPricing.MaxTiers) Err("tiers", $"At most {OfferPricing.MaxTiers} price steps.");
        for (var i = 0; i < tiers.Count; i++)
        {
            var t = tiers[i];
            if (t.MinQuantity is < 1 or > OfferPricing.MaxMinQuantity) { Err("tiers", $"Minimum quantities must be between 1 and {OfferPricing.MaxMinQuantity}."); break; }
            if (t.UnitPriceCents is <= 0 or > OrderIngestService.MaxCents) { Err("tiers", "Each price must be more than $0.00."); break; }
            if (i > 0 && t.MinQuantity == tiers[i - 1].MinQuantity) { Err("tiers", "Each step needs a different minimum quantity."); break; }
            if (i > 0 && t.UnitPriceCents >= tiers[i - 1].UnitPriceCents) { Err("tiers", "A step with a higher minimum quantity must have a lower price each."); break; }
        }
        var gifts = w.Gifts ?? [];
        if (gifts.Count > OfferPricing.MaxGifts) Err("gifts", $"At most {OfferPricing.MaxGifts} gifts.");
        else if (gifts.Count > 0)
        {
            if (gifts.Any(g => g.MinQuantity is < 1 or > OfferPricing.MaxMinQuantity)) Err("gifts", $"Gift minimum quantities must be between 1 and {OfferPricing.MaxMinQuantity}.");
            if (gifts.Any(g => g.Quantity is < 1 or > OfferPricing.MaxGiftQuantity)) Err("gifts", $"A gift quantity must be between 1 and {OfferPricing.MaxGiftQuantity}.");
            if (gifts.Select(g => g.ProductId).Distinct().Count() != gifts.Count) Err("gifts", "Each gift product can be added once.");
            var gids = gifts.Select(g => g.ProductId).Distinct().ToList();
            var ok = await db.Products.AsNoTracking().CountAsync(p => gids.Contains(p.Id) && p.SellerId == sellerId && p.BrandId == brandId
                && p.Type == ListingType.Product && p.Status == ProductStatus.Active && !p.HasVariants && p.ModeratedAt == null);
            if (ok != gids.Count) Err("gifts", "A gift must be an active physical product of this brand, without variations.");
        }
        if (tiers.Count == 0 && gifts.Count == 0) Err("tiers", "Add at least one price step or one gift.");
        DateTime? from = w.ActiveFrom is { } f ? OrderSupport.Utc(f) : null, to = w.ActiveTo is { } tt ? OrderSupport.Utc(tt) : null;
        if (from is not null && to is not null && to <= from) Err("activeTo", "The offer must end after it starts.");
        if (errors.Count > 0) return (null, errors);
        return (new OfferDraft(name, status, scope, ids, tiers, gifts.Select(g => g with { }).ToList(), from, to), errors);
    }

    /// <summary>One active offer per scope: an active version may not overlap the scope of another active offer of the same brand with the same scope kind (dates overlapping).</summary>
    private async Task<Offer?> ConflictAsync(Guid sellerId, Guid brandId, Guid? self, OfferDraft d)
    {
        if (d.Status != OfferStatus.Active) return null;
        var others = await db.Offers.AsNoTracking().Where(o => o.SellerId == sellerId && o.BrandId == brandId && o.Status == OfferStatus.Active && o.Id != self).ToListAsync();
        if (others.Count == 0) return null;
        var oids = others.Select(o => o.Id).ToList();
        var cur = await db.OfferVersions.AsNoTracking().Where(v => oids.Contains(v.OfferId) && v.SupersededAt == null).ToListAsync();
        foreach (var o in others)
        {
            var v = cur.FirstOrDefault(x => x.OfferId == o.Id);
            if (v is null || v.Scope != d.Scope) continue;
            var vv = OfferVersionView.Of(v);
            var datesOverlap = (d.ActiveTo is null || v.ActiveFrom is null || v.ActiveFrom < d.ActiveTo) && (v.ActiveTo is null || d.ActiveFrom is null || d.ActiveFrom < v.ActiveTo);
            if (!datesOverlap) continue;
            if (d.Scope == OfferScope.All || vv.ScopeIds.Intersect(d.ScopeIds).Any()) return o;
        }
        return null;
    }

    // ---- writes --------------------------------------------------------------------------------------------------------------------
    public async Task<OfferResult> CreateAsync(AccountAccess a, Guid brandId, OfferDraft d)
    {
        if (await ConflictAsync(a.SellerId, brandId, null, d) is { } c) return Conflict(c);
        var now = time.UtcNow();
        var offer = new Offer { SellerId = a.SellerId, BrandId = brandId, Name = d.Name, Status = d.Status, CurrentVersion = 1, CreatedAt = now, CreatedBy = a.UserId, UpdatedAt = now };
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.Offers.Add(offer);
            db.OfferVersions.Add(NewVersion(offer.Id, 1, d, now, a.UserId));
            await FeedAsync(offer, d.Status, 1, "updated");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await audit.WriteAsync("offer.created", a.UserId, null, $"seller={a.SellerId};offer={offer.Id};version=1;status={OfferPricing.StatusName(d.Status)}");
        return new(201, "ok", "", offer);
    }

    public async Task<OfferResult> UpdateAsync(AccountAccess a, Offer current, int expectedVersion, OfferDraft d)
    {
        if (current.Status == OfferStatus.Ended) return new(409, "offer_ended", "This offer has ended and can't be changed. Create a new offer instead.");
        if (await ConflictAsync(a.SellerId, current.BrandId, current.Id, d) is { } c) return Conflict(c);
        var now = time.UtcNow();
        var next = expectedVersion + 1;
        Offer offer;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            if (!await ClaimAsync(current.Id, a.SellerId, expectedVersion)) return VersionConflict();
            offer = await db.Offers.FirstAsync(o => o.Id == current.Id);
            await db.OfferVersions.Where(v => v.OfferId == offer.Id && v.Version == expectedVersion).ExecuteUpdateAsync(s => s.SetProperty(v => v.SupersededAt, (DateTime?)now));
            offer.CurrentVersion = next; offer.Name = d.Name; offer.Status = d.Status; offer.UpdatedAt = now;
            db.OfferVersions.Add(NewVersion(offer.Id, next, d, now, a.UserId));
            await FeedAsync(offer, d.Status, next, d.Status == OfferStatus.Active ? "updated" : "paused");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await audit.WriteAsync("offer.updated", a.UserId, null, $"seller={a.SellerId};offer={offer.Id};version={next};status={OfferPricing.StatusName(d.Status)}");
        return new(200, "ok", "", offer);
    }

    public async Task<OfferResult> EndAsync(AccountAccess a, Offer current, int? expectedVersion)
    {
        if (current.Status == OfferStatus.Ended) return new(409, "offer_ended", "This offer has already ended.");
        var expected = expectedVersion ?? current.CurrentVersion;
        var now = time.UtcNow();
        Offer offer;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            if (!await ClaimAsync(current.Id, a.SellerId, expected)) return VersionConflict();
            offer = await db.Offers.FirstAsync(o => o.Id == current.Id);
            await EndInTxAsync(offer, expected, now, a.UserId, "ended");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await audit.WriteAsync("offer.ended", a.UserId, null, $"seller={a.SellerId};offer={offer.Id};version={offer.CurrentVersion}");
        return new(200, "ok", "", offer);
    }

    /// <summary>
    /// Removes demo offers for good (inside the demo remover's transaction): offer and versions are deleted; an offer that was ever in the feed leaves a
    /// <c>catalog_tombstones</c> row of kind <c>offer</c> with a new feed version, so the feed reports it as removed (<c>reason: deleted</c>), like a deleted listing. No event (demo data).
    /// </summary>
    public async Task RemoveForDemoAsync(Guid sellerId, IReadOnlyCollection<Guid> offerIds)
    {
        if (offerIds.Count == 0) return;
        var now = time.UtcNow();
        var offers = await db.Offers.AsNoTracking().Where(o => o.SellerId == sellerId && offerIds.Contains(o.Id)).ToListAsync();
        foreach (var offer in offers.Where(o => o.EverListed))
            db.CatalogTombstones.Add(new CatalogTombstone { ListingId = offer.Id, SellerId = offer.SellerId, Kind = TombstoneKinds.Offer, Reason = "deleted", CreatedAt = now,
                Version = await Solvelt.Api.Catalog.CatalogTracking.AllocateAsync(db, 1, CancellationToken.None) });
        await db.SaveChangesAsync();
        var ids = offers.Select(o => o.Id).ToList();
        await db.OfferVersions.Where(v => ids.Contains(v.OfferId)).ExecuteDeleteAsync();
        await db.Offers.Where(o => ids.Contains(o.Id)).ExecuteDeleteAsync();
    }

    private async Task EndInTxAsync(Offer offer, int expected, DateTime now, Guid userId, string reason)
    {
        var prev = await db.OfferVersions.AsNoTracking().FirstAsync(v => v.OfferId == offer.Id && v.Version == expected);
        await db.OfferVersions.Where(v => v.Id == prev.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SupersededAt, (DateTime?)now));
        var ended = new OfferVersion
        {
            OfferId = offer.Id, Version = expected + 1, Name = prev.Name, Status = OfferStatus.Ended, Scope = prev.Scope, ScopeIdsJson = prev.ScopeIdsJson, TiersJson = prev.TiersJson,
            GiftsJson = prev.GiftsJson, ActiveFrom = prev.ActiveFrom, ActiveTo = prev.ActiveTo, CreatedAt = now, CreatedBy = userId
        };
        db.OfferVersions.Add(ended);
        offer.CurrentVersion = expected + 1; offer.Status = OfferStatus.Ended; offer.EndedAt = now; offer.EndedReason = reason; offer.UpdatedAt = now;
        await FeedAsync(offer, OfferStatus.Ended, expected + 1, reason);
    }

    /// <summary>
    /// Feed position + event for a change, in the caller's transaction: an active version gets a new feed version and <c>offer.updated</c>; an offer that was in the feed and stops
    /// applying (paused to draft, ended, deleted) gets a new feed version and <c>offer.ended</c>. Drafts that were never active stay out of the feed and send nothing.
    /// </summary>
    private async Task FeedAsync(Offer offer, OfferStatus status, int version, string reason)
    {
        if (status != OfferStatus.Active && !offer.EverListed) return;
        if (status == OfferStatus.Active) offer.EverListed = true;
        offer.FeedVersion = await Solvelt.Api.Catalog.CatalogTracking.AllocateAsync(db, 1, CancellationToken.None);   // takes the counter row lock until commit: feed order = commit order
        if (status == OfferStatus.Active)
            outbox.Enqueue("offer.updated", offer.SellerId, offer.Id.ToString(), new { offerId = offer.Id, version, feedVersion = offer.FeedVersion, sellerId = offer.SellerId, brandId = offer.BrandId }, scope: Scope(offer.Id));
        else
            outbox.Enqueue("offer.ended", offer.SellerId, offer.Id.ToString(), new { offerId = offer.Id, version, feedVersion = offer.FeedVersion, sellerId = offer.SellerId, brandId = offer.BrandId, reason }, scope: Scope(offer.Id));
    }

    private async Task<bool> ClaimAsync(Guid id, Guid sellerId, int expected) =>
        await db.Offers.Where(o => o.Id == id && o.SellerId == sellerId && o.CurrentVersion == expected)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.CurrentVersion, expected + 1)) == 1;

    private static OfferVersion NewVersion(Guid offerId, int version, OfferDraft d, DateTime now, Guid userId) => new()
    {
        OfferId = offerId, Version = version, Name = d.Name, Status = d.Status, Scope = d.Scope, ScopeIdsJson = OrderJson.Ser(d.ScopeIds),
        TiersJson = OrderJson.Ser(d.Tiers), GiftsJson = OrderJson.Ser(d.Gifts), ActiveFrom = d.ActiveFrom, ActiveTo = d.ActiveTo, CreatedAt = now, CreatedBy = userId
    };

    private static OfferResult VersionConflict() => new(409, "version_conflict", "This offer was changed somewhere else. Reload to see the latest version.");
    private static OfferResult Conflict(Offer other) =>
        new(409, "scope_conflict", $"The active offer \"{other.Name}\" already covers this scope. End it first, or choose other products.", null, new { conflictingOfferId = other.Id, conflictingOfferName = other.Name });

    // ---- reads ---------------------------------------------------------------------------------------------------------------------
    public async Task<List<OfferVersionView>> VersionsAsync(Guid offerId) =>
        (await db.OfferVersions.AsNoTracking().Where(v => v.OfferId == offerId).OrderBy(v => v.Version).ToListAsync()).Select(OfferVersionView.Of).ToList();

    /// <summary>Display state for the seller: draft, scheduled, active, expired or ended (the date window is evaluated now).</summary>
    public static string State(Offer o, OfferVersion? v, DateTime now) => o.Status switch
    {
        OfferStatus.Draft => "draft",
        OfferStatus.Ended => "ended",
        _ when v?.ActiveFrom is { } f && f > now => "scheduled",
        _ when v?.ActiveTo is { } t && t <= now => "expired",
        _ => "active"
    };

    /// <summary>The feed entry of a removed offer (demo data): no content, like a deleted listing's tombstone.</summary>
    public static object Tombstone(CatalogTombstone t) =>
        new { offerId = t.ListingId, version = (int?)null, feedVersion = t.Version, listed = false, status = "ended", reason = t.Reason, sellerId = t.SellerId, brandId = (Guid?)null };

    /// <summary>The catalog-feed document of an offer (docs/CATALOG.md section 11): the full document while its current version is active, else an ended entry.</summary>
    public async Task<List<object>> FeedDocsAsync(IReadOnlyList<Offer> offers, CancellationToken ct = default)
    {
        var now = time.UtcNow();
        var ids = offers.Select(o => o.Id).ToList();
        var versions = (await db.OfferVersions.AsNoTracking().Where(v => ids.Contains(v.OfferId) && v.SupersededAt == null).ToListAsync(ct)).ToDictionary(v => v.OfferId);
        var demo = (await db.DemoRecords.AsNoTracking().Where(r => ids.Contains(r.EntityId)).Select(r => r.EntityId).ToListAsync(ct)).ToHashSet();
        var tree = await CategoryTree.LoadAsync(db, ct);
        var docs = new List<object>();
        foreach (var o in offers)
        {
            if (o.Status != OfferStatus.Active || !versions.TryGetValue(o.Id, out var row))
            {
                docs.Add(new { offerId = o.Id, version = o.CurrentVersion, feedVersion = o.FeedVersion, listed = false, status = o.Status == OfferStatus.Ended ? "ended" : "inactive",
                    reason = o.Status == OfferStatus.Ended ? o.EndedReason ?? "ended" : "paused", sellerId = o.SellerId, brandId = o.BrandId });
                continue;
            }
            var v = OfferVersionView.Of(row);
            var eligible = await EligibleQuery(o.SellerId, o.BrandId, v.Row.Scope, v.ScopeIds, tree).Where(p => p.CatalogListed).OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(ct);
            var giftIds = v.Gifts.Select(g => g.ProductId).ToList();
            var giftProducts = await db.Products.AsNoTracking().Where(p => giftIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            docs.Add(new
            {
                offerId = o.Id, version = v.Row.Version, feedVersion = o.FeedVersion, listed = true, status = "active", sellerId = o.SellerId, brandId = o.BrandId, name = v.Row.Name,
                currency = "USD",
                scope = new { kind = OfferPricing.ScopeName(v.Row.Scope), categoryIds = v.Row.Scope == OfferScope.Category ? v.ScopeIds : new List<Guid>(), productIds = v.Row.Scope == OfferScope.Products ? v.ScopeIds : new List<Guid>() },
                productIds = eligible,
                tiers = v.Tiers.Select(t => new { minQuantity = t.MinQuantity, unitPriceCents = t.UnitPriceCents }),
                gifts = v.Gifts.Select(g =>
                {
                    var p = giftProducts.GetValueOrDefault(g.ProductId);
                    var available = p is not null && p.Status == ProductStatus.Active && p.ModeratedAt == null && (p.AllowBackorder || p.Stock - p.Reserved >= g.Quantity);
                    return new { minQuantity = g.MinQuantity, productId = g.ProductId, quantity = g.Quantity, title = p?.Title, available };
                }),
                activeFrom = v.Row.ActiveFrom, activeTo = v.Row.ActiveTo,
                activeNow = (v.Row.ActiveFrom is null || v.Row.ActiveFrom <= now) && (v.Row.ActiveTo is null || now < v.Row.ActiveTo),
                demo = demo.Contains(o.Id), updatedAt = o.UpdatedAt
            });
        }
        return docs;
    }

    // ---- preview -------------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// What a buyer pays at a few quantities, computed here (the UI never does money maths): the unit price is the sample product's current price (sale included), each row applies
    /// the tier reached (never above the product's price) and lists the gifts earned.
    /// </summary>
    public async Task<object> PreviewAsync(Guid sellerId, Guid brandId, OfferDraft d, Guid? sampleProductId, List<int>? quantities)
    {
        var now = time.UtcNow();
        var tree = await CategoryTree.LoadAsync(db);
        var eligible = EligibleQuery(sellerId, brandId, d.Scope, d.ScopeIds, tree).Where(p => p.Status == ProductStatus.Active);
        Product? sample = null;
        if (sampleProductId is { } sid) sample = await eligible.Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == sid);
        sample ??= await eligible.Include(p => p.Variants).OrderBy(p => p.Title).ThenBy(p => p.Id).FirstOrDefaultAsync();
        long? unit = sample is null ? null : ProductRules.CurrentPriceCents(sample, now) ?? sample.Variants.Where(v => v.Enabled && v.PriceCents != null).OrderBy(v => v.SortOrder).Select(v => v.PriceCents).FirstOrDefault();
        var giftIds = d.Gifts.Select(g => g.ProductId).ToList();
        var titles = await db.Products.AsNoTracking().Where(p => giftIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Title);
        var eligibleCount = await eligible.CountAsync();

        var defaults = new List<int>();
        defaults.AddRange(d.Tiers.Select(t => t.MinQuantity));
        defaults.AddRange(d.Gifts.Select(g => g.MinQuantity));
        if (d.Tiers.Count > 0) defaults.Add(d.Tiers[^1].MinQuantity + 5);
        var qs = (quantities is { Count: > 0 } ? quantities : defaults)
            .Where(q => q is >= 1 and <= OfferPricing.MaxMinQuantity).Distinct().OrderBy(q => q).Take(12).ToList();
        var texts = new List<string>();
        var rows = new List<object>();
        foreach (var q in qs)
        {
            var tier = OfferPricing.TierFor(d.Tiers, q);
            long? each = unit is { } u ? (tier is null ? u : Math.Min(u, tier.UnitPriceCents)) : tier?.UnitPriceCents;
            long? total = each is { } e ? e * q : null;
            long? discount = unit is { } u2 ? OfferPricing.LineDiscount(u2, q, tier) : null;
            var earned = OfferPricing.GiftsFor(d.Gifts, q);
            var gifts = earned.Select(g => new { productId = g.ProductId, title = titles.GetValueOrDefault(g.ProductId) ?? "Gift", quantity = g.Quantity }).ToList();
            rows.Add(new { quantity = q, unitPriceCents = each, totalCents = total, tierMinQuantity = tier?.MinQuantity, discountCents = discount, gifts });
            var text = $"{q} items: " + (total is { } t ? OfferPricing.Money(t) : "price per item not set");
            foreach (var g in gifts) text += " + free " + (g.quantity > 1 ? g.quantity + " x " : "") + g.title;
            texts.Add(text);
        }
        var summary = texts.Count > 0 ? string.Join(". ", texts) + "." : "";
        return new
        {
            sample = sample is null ? null : new { productId = sample.Id, title = sample.Title, unitPriceCents = unit },
            eligibleProducts = eligibleCount, rows, summary, currency = "USD"
        };
    }
}
