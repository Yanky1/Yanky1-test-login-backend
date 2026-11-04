using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;

namespace Solvelt.Api.Catalog;

/// <summary>Why a listing is not visible to the marketplace (the <c>reason</c> of <c>listing.unlisted</c>).</summary>
public static class UnlistReasons
{
    public const string NotActive = "not_active", Archived = "archived", SellerSuspended = "seller_suspended", Moderated = "moderated", BrandRemoved = "brand_removed";
}

/// <summary>The single definition of "is this listing visible to the marketplace" and of the public availability state. Pure functions: used by the version tracker AND the feed.</summary>
public static class CatalogRules
{
    /// <summary>Null when the listing is visible; otherwise the reason. Staff moderation wins over everything else (hiding also sets the product Inactive).</summary>
    public static string? UnlistReason(Product p, SellerStatus? seller, BrandStatus? brand)
    {
        if (p.ModeratedAt is not null) return UnlistReasons.Moderated;
        if (p.Status == ProductStatus.Archived) return UnlistReasons.Archived;
        if (seller is not SellerStatus.Approved) return UnlistReasons.SellerSuspended;
        if (brand is not BrandStatus.Active) return UnlistReasons.BrandRemoved;
        if (p.Status != ProductStatus.Active) return UnlistReasons.NotActive;
        return null;
    }

    /// <summary>Public availability of one unit of stock: in_stock | low_stock | out_of_stock | backorder (exact quantities are never published).</summary>
    public static string StockState(int stock, int reserved, bool allowBackorder, int? lowStockAlert)
    {
        var available = stock - reserved;
        if (available <= 0) return allowBackorder ? "backorder" : "out_of_stock";
        return lowStockAlert is { } l && available <= l ? "low_stock" : "in_stock";
    }

    /// <summary>Product-level state: services are always "available"; products with variants take the best state of their enabled variants.</summary>
    public static string ProductState(ListingType type, bool hasVariants, int stock, int reserved, bool allowBackorder, int? low, IEnumerable<string> variantStates)
    {
        if (type == ListingType.Service) return "available";
        if (!hasVariants) return StockState(stock, reserved, allowBackorder, low);
        var states = variantStates.ToList();
        foreach (var s in new[] { "in_stock", "low_stock", "backorder" }) if (states.Contains(s)) return s;
        return "out_of_stock";
    }

    public readonly record struct VariantStock(Guid Id, int Stock, int Reserved);

    /// <summary>Hash of the per-variant (or product) availability states; changes exactly when the public availability changes.</summary>
    public static string Signature(ListingType type, bool hasVariants, int stock, int reserved, bool allowBackorder, int? low, IEnumerable<VariantStock> enabledVariants)
    {
        var sb = new StringBuilder();
        if (type == ListingType.Service) sb.Append("service");
        else if (!hasVariants) sb.Append("p:").Append(StockState(stock, reserved, allowBackorder, low));
        else foreach (var v in enabledVariants.OrderBy(x => x.Id)) sb.Append(v.Id).Append(':').Append(StockState(v.Stock, v.Reserved, allowBackorder, low)).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>
/// Keeps <see cref="Product.CatalogVersion"/> and the <c>listing.upserted</c> / <c>listing.unlisted</c> outbox events in step with every change that is visible in the catalog feed.
/// It runs inside <see cref="AppDbContext.SaveChangesAsync(bool, CancellationToken)"/>, so ANY write path (endpoints, imports, admin, assistant, background jobs) is covered, and the version,
/// the event and the change itself commit in ONE transaction.
///
/// Versions come from the single-row <c>catalog_counter</c>. Incrementing it takes that row's lock until commit, so catalog-changing transactions are serialized: a later version can never
/// become visible before an earlier one. That is what makes the feed cursor safe under concurrent writes (a reader that has seen version N has seen every version below N).
/// </summary>
public static class CatalogTracking
{
    // Product columns that never change what the marketplace sees (internal numbers, concurrency tokens and the tracker's own columns).
    private static readonly HashSet<string> IgnoredProductColumns =
    [
        nameof(Product.CostCents), nameof(Product.QualityScore), nameof(Product.IsReady), nameof(Product.ImageSeq), nameof(Product.Version), nameof(Product.UpdatedAt),
        nameof(Product.CatalogVersion), nameof(Product.CatalogListed), nameof(Product.CatalogEverListed), nameof(Product.CatalogUnlistReason), nameof(Product.CatalogStockState), nameof(Product.CatalogDeliveryState),
        nameof(Product.ModerationPrevStatus), nameof(Product.ModeratedBy), nameof(Product.CreatedBy), nameof(Product.CreatedAt), nameof(Product.PublishedAt),
        nameof(Product.MetaTitle), nameof(Product.MetaDescription), nameof(Product.DefaultPackageId), nameof(Product.ShippingJson),
        nameof(Product.WeightGrams), nameof(Product.LengthMm), nameof(Product.WidthMm), nameof(Product.HeightMm), nameof(Product.MaxPerOrder)
    ];

    internal sealed class Touch
    {
        public readonly HashSet<Guid> Products = [], Sellers = [], Brands = [], Categories = [];
        /// <summary>Delivery settings changed for (seller, brand or null = whole account): the refresh job re-derives the delivery options of those listings.</summary>
        public readonly HashSet<(Guid Seller, Guid? Brand)> Refresh = [];
        public bool IsEmpty => Products.Count == 0 && Sellers.Count == 0 && Brands.Count == 0 && Categories.Count == 0 && Refresh.Count == 0;
    }

    /// <summary>Remembers what <see cref="ApplyAsync"/> changed in the tracker so a failed save can be undone (a retry must not enqueue the events twice).</summary>
    internal sealed class Undo
    {
        public readonly List<EntityEntry> Outbox = [], Requests = [];
        public readonly List<(EntityEntry Entry, long Version, bool Listed, bool Ever, string? Reason, string? Stock, string? Delivery)> Products = [];

        public void Revert()
        {
            foreach (var e in Outbox) e.State = EntityState.Detached;
            foreach (var e in Requests) e.State = EntityState.Detached;
            foreach (var (e, v, l, ever, r, s, d) in Products)
            {
                if (e.State == EntityState.Detached) continue;
                var p = (Product)e.Entity;
                p.CatalogVersion = v; p.CatalogListed = l; p.CatalogEverListed = ever; p.CatalogUnlistReason = r; p.CatalogStockState = s; p.CatalogDeliveryState = d;
            }
            Outbox.Clear(); Requests.Clear(); Products.Clear();
        }
    }

    // ---- 1. what changed ------------------------------------------------------------------------------------------------------

    internal static Touch Scan(AppDbContext db)
    {
        var t = new Touch();
        foreach (var e in db.ChangeTracker.Entries())
        {
            if (e.State is EntityState.Unchanged or EntityState.Detached) continue;
            switch (e.Entity)
            {
                case Product p:
                    if (e.State != EntityState.Modified || e.Properties.Any(x => x.IsModified && !IgnoredProductColumns.Contains(x.Metadata.Name))) t.Products.Add(p.Id);
                    break;
                case ProductVariant v: t.Products.Add(v.ProductId); break;
                case ProductOption o: t.Products.Add(o.ProductId); break;
                case ProductImage i: t.Products.Add(i.ProductId); break;
                case ProductAttributeValue a: t.Products.Add(a.ProductId); break;
                case ProductFaq f: t.Products.Add(f.ProductId); break;
                case ServiceDetails s: t.Products.Add(s.ProductId); break;
                case ProductQuestion q:
                    {
                        var was = e.State == EntityState.Added ? QuestionState.Hidden : (QuestionState)e.OriginalValues[nameof(ProductQuestion.State)]!;
                        var now = e.State == EntityState.Deleted ? QuestionState.Hidden : q.State;
                        if (was == QuestionState.Published || now == QuestionState.Published) t.Products.Add(q.ProductId);
                        break;
                    }
                case Brand b:
                    if (e.State == EntityState.Modified && e.Properties.Any(x => x.IsModified && x.Metadata.Name is nameof(Brand.Name) or nameof(Brand.Slug) or nameof(Brand.Description) or nameof(Brand.Website) or nameof(Brand.Status)))
                        t.Brands.Add(b.Id);
                    break;
                case SellerAccount s:
                    if (e.State == EntityState.Modified && e.Properties.Any(x => x.IsModified && x.Metadata.Name is nameof(SellerAccount.Status) or nameof(SellerAccount.VendorName) or nameof(SellerAccount.Country)))
                        t.Sellers.Add(s.Id);
                    break;
                case ShippingProfile sp: t.Refresh.Add((sp.SellerId, sp.BrandId)); break;
                case DeliverySetting ds: t.Refresh.Add((ds.SellerId, ds.BrandId)); break;
                case DeliveryZone dz: t.Refresh.Add((dz.SellerId, null)); break;           // a zone does not know its brand without a lookup: the whole account is checked (only real changes bump)
                case Category c: if (e.State != EntityState.Added) t.Categories.Add(c.Id); break;
                case CategoryAttribute ca: t.Categories.Add(ca.CategoryId); break;
            }
        }
        return t;
    }

    // ---- 2. versions + events for a SaveChanges ---------------------------------------------------------------------------------

    /// <summary>Takes the counter lock (held until the surrounding transaction ends).</summary>
    internal static Task LockCounterAsync(AppDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("UPDATE catalog_counter SET \"Value\" = \"Value\" WHERE \"Id\" = 1", ct);

    /// <summary>Hands out <paramref name="n"/> consecutive versions; returns the first. Must run in the transaction that will persist the change.</summary>
    internal static async Task<long> AllocateAsync(AppDbContext db, int n, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE catalog_counter SET \"Value\" = \"Value\" + {n} WHERE \"Id\" = 1", ct);
        var end = await db.CatalogCounters.AsNoTracking().Select(c => c.Value).SingleAsync(ct);
        return end - n + 1;
    }

    /// <summary>Listings this change can affect: the touched ones plus every listing that is (or could become) visible of a touched seller / brand / category.</summary>
    private static async Task<HashSet<Guid>> AffectedAsync(AppDbContext db, Touch t, CancellationToken ct)
    {
        var ids = new HashSet<Guid>(t.Products);
        if (t.Sellers.Count > 0)
            ids.UnionWith(await db.Products.AsNoTracking().Where(p => t.Sellers.Contains(p.SellerId) && (p.CatalogListed || p.Status == ProductStatus.Active)).Select(p => p.Id).ToListAsync(ct));
        if (t.Brands.Count > 0)
            ids.UnionWith(await db.Products.AsNoTracking().Where(p => t.Brands.Contains(p.BrandId) && (p.CatalogListed || p.Status == ProductStatus.Active)).Select(p => p.Id).ToListAsync(ct));
        if (t.Categories.Count > 0)
        {
            var tree = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(ct);
            var all = new HashSet<Guid>(t.Categories);
            for (var grew = true; grew;) { grew = false; foreach (var c in tree) if (c.ParentId is { } pid && all.Contains(pid) && all.Add(c.Id)) grew = true; }
            var cats = all.Select(x => (Guid?)x).ToList();
            ids.UnionWith(await db.Products.AsNoTracking().Where(p => cats.Contains(p.CategoryId) && (p.CatalogListed || p.Status == ProductStatus.Active)).Select(p => p.Id).ToListAsync(ct));
        }
        return ids;
    }

    /// <summary>Cheap gate before the counter lock: editing drafts or other never-visible listings must not serialize on it.</summary>
    private static async Task<bool> MayBeVisibleAsync(AppDbContext db, HashSet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return false;
        var tracked = db.ChangeTracker.Entries<Product>().Where(e => ids.Contains(e.Entity.Id)).ToList();
        if (tracked.Any(e => e.State == EntityState.Deleted ? e.Entity.CatalogListed : e.Entity.CatalogListed || e.Entity.Status == ProductStatus.Active)) return true;
        var rest = ids.Except(tracked.Select(e => e.Entity.Id)).ToList();
        return rest.Count > 0 && await db.Products.AsNoTracking().AnyAsync(p => rest.Contains(p.Id) && (p.CatalogListed || p.Status == ProductStatus.Active), ct);
    }

    internal static async Task ApplyAsync(AppDbContext db, TimeProvider clock, Touch t, Undo undo, CancellationToken ct)
    {
        // delivery settings changed: leave a request for the refresh job (cheap, same transaction; the heavy part runs in the background in chunks)
        foreach (var (seller, brand) in t.Refresh)
            undo.Requests.Add(db.Entry(db.CatalogRefreshRequests.Add(new CatalogRefreshRequest { SellerId = seller, BrandId = brand, CreatedAt = clock.UtcNow() }).Entity));
        if (!await MayBeVisibleAsync(db, await AffectedAsync(db, t, ct), ct)) return;
        await LockCounterAsync(db, ct);
        var ids = await AffectedAsync(db, t, ct);                  // again under the lock: what is committed now decides
        if (ids.Count == 0) return;

        // the products (tracked instances win: they carry the unsaved edits)
        var entries = db.ChangeTracker.Entries<Product>().Where(e => ids.Contains(e.Entity.Id) && e.State != EntityState.Detached).ToDictionary(e => e.Entity.Id);
        var missing = ids.Where(i => !entries.ContainsKey(i)).ToList();
        if (missing.Count > 0) foreach (var p in await db.Products.Where(p => missing.Contains(p.Id)).ToListAsync(ct)) entries[p.Id] = db.Entry(p);

        var sellers = await StatusesAsync<SellerAccount, SellerStatus>(db, entries.Values.Select(e => e.Entity.SellerId).Distinct().ToList(), s => s.Id, s => s.Status, ct);
        var brands = await StatusesAsync<Brand, BrandStatus>(db, entries.Values.Select(e => e.Entity.BrandId).Distinct().ToList(), b => b.Id, b => b.Status, ct);

        var work = new List<(EntityEntry<Product> Entry, string? Reason)>();
        foreach (var e in entries.Values.OrderBy(e => e.Entity.Id))
        {
            var p = e.Entity;
            var reason = e.State == EntityState.Deleted ? UnlistReasons.Archived
                : CatalogRules.UnlistReason(p, sellers.GetValueOrDefault(p.SellerId), brands.GetValueOrDefault(p.BrandId));
            if (reason is not null && !p.CatalogListed) continue;     // not visible before, not visible now: nothing to tell the marketplace
            work.Add((e, reason));
        }
        if (work.Count == 0) return;

        var visible = work.Where(w => w.Reason is null).Select(w => w.Entry.Entity).ToList();
        var stock = await StockSignaturesAsync(db, visible, ct);
        var delivery = await DeliveryContext.ForProductsAsync(db, visible, ct);
        var first = await AllocateAsync(db, work.Count, ct);
        var outbox = new Outbox(db, clock);
        for (var i = 0; i < work.Count; i++)
        {
            var (e, reason) = work[i];
            var p = e.Entity; var version = first + i;
            if (e.State != EntityState.Deleted)
            {
                undo.Products.Add((e, p.CatalogVersion, p.CatalogListed, p.CatalogEverListed, p.CatalogUnlistReason, p.CatalogStockState, p.CatalogDeliveryState));
                p.CatalogVersion = version; p.CatalogListed = reason is null; p.CatalogEverListed |= reason is null; p.CatalogUnlistReason = reason;
                p.CatalogStockState = reason is null ? stock.GetValueOrDefault(p.Id) : null;
                p.CatalogDeliveryState = reason is null ? delivery.GetValueOrDefault(p.Id)?.Signature : null;
            }
            var msg = reason is null ? outbox.Enqueue("listing.upserted", p.SellerId, p.Id.ToString(), new { listingId = p.Id, version }, version)
                                     : outbox.Enqueue("listing.unlisted", p.SellerId, p.Id.ToString(), new { listingId = p.Id, version, reason }, version);
            undo.Outbox.Add(db.Entry(msg));
        }
    }

    private static async Task<Dictionary<Guid, TStatus>> StatusesAsync<TEntity, TStatus>(AppDbContext db, List<Guid> ids, Func<TEntity, Guid> key, Func<TEntity, TStatus> status, CancellationToken ct)
        where TEntity : class where TStatus : struct
    {
        var res = db.ChangeTracker.Entries<TEntity>().Where(e => e.State is not (EntityState.Detached or EntityState.Deleted)).Select(e => e.Entity).Where(e => ids.Contains(key(e))).ToDictionary(key, status);
        var missing = ids.Where(i => !res.ContainsKey(i)).ToList();
        if (missing.Count == 0) return res;
        // AsNoTracking projection by id through the model (key property is always "Id")
        var rows = await db.Set<TEntity>().AsNoTracking().Where(e => missing.Contains(EF.Property<Guid>(e, "Id"))).ToListAsync(ct);
        foreach (var r in rows) res[key(r)] = status(r);
        return res;
    }

    /// <summary>Availability signature of each product from its CURRENT values (tracked variants win over stored ones).</summary>
    private static async Task<Dictionary<Guid, string>> StockSignaturesAsync(AppDbContext db, List<Product> products, CancellationToken ct)
    {
        var res = new Dictionary<Guid, string>();
        if (products.Count == 0) return res;
        var ids = products.Select(p => p.Id).ToList();
        var stored = (await db.Variants.AsNoTracking().Where(v => ids.Contains(v.ProductId) && v.Enabled).Select(v => new { v.Id, v.ProductId, v.Stock, v.Reserved }).ToListAsync(ct))
            .ToDictionary(v => v.Id, v => (v.ProductId, V: new CatalogRules.VariantStock(v.Id, v.Stock, v.Reserved)));
        foreach (var e in db.ChangeTracker.Entries<ProductVariant>().Where(e => ids.Contains(e.Entity.ProductId)))
        {
            if (e.State == EntityState.Deleted || !e.Entity.Enabled) stored.Remove(e.Entity.Id);
            else stored[e.Entity.Id] = (e.Entity.ProductId, new(e.Entity.Id, e.Entity.Stock, e.Entity.Reserved));
        }
        foreach (var p in products)
            res[p.Id] = CatalogRules.Signature(p.Type, p.HasVariants, p.Stock, p.Reserved, p.AllowBackorder, p.LowStockAlert, stored.Values.Where(x => x.ProductId == p.Id).Select(x => x.V));
        return res;
    }

    /// <summary>
    /// Gives every listing that is Active but has no version yet its first version (and a <c>listing.upserted</c> event when it is visible). Runs at start-up, so listings that existed
    /// before the feed was deployed become part of it without anybody editing them. Idempotent and safe with several instances (the counter lock serializes them).
    /// </summary>
    public static async Task<int> BackfillAsync(AppDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        var candidates = await db.Products.AsNoTracking().Where(p => p.CatalogVersion == 0 && p.Status == ProductStatus.Active).Select(p => p.Id).ToListAsync(ct);
        foreach (var chunk in candidates.Chunk(500))
        {
            var t = new Touch(); t.Products.UnionWith(chunk);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var undo = new Undo();
            try
            {
                await ApplyAsync(db, clock, t, undo, ct);           // listings that are not visible (suspended seller, hidden...) get nothing until they become visible
                await db.SaveChangesAsync(ct);                       // the hook sees only catalog bookkeeping columns changed: no second version
                await tx.CommitAsync(ct);
            }
            catch { undo.Revert(); throw; }
            finally { db.ChangeTracker.Clear(); }
        }
        return candidates.Count;
    }

    /// <summary>
    /// Visible listings that predate the <c>delivery</c> field have no delivery state: queue one refresh per account so the job gives them a version (and a <c>listing.upserted</c>)
    /// and consumers that already fetched them pick the new field up. Idempotent: an account with an unprocessed request is skipped.
    /// </summary>
    public static async Task<int> BackfillDeliveryAsync(AppDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        var sellers = await db.Products.AsNoTracking().Where(p => p.CatalogListed && p.CatalogDeliveryState == null).Select(p => p.SellerId).Distinct().ToListAsync(ct);
        var pending = (await db.CatalogRefreshRequests.AsNoTracking().Where(r => r.ProcessedAt == null && r.BrandId == null).Select(r => r.SellerId).ToListAsync(ct)).ToHashSet();
        var n = 0;
        foreach (var seller in sellers.Where(x => !pending.Contains(x)))
        { db.CatalogRefreshRequests.Add(new CatalogRefreshRequest { SellerId = seller, CreatedAt = clock.UtcNow() }); n++; }
        if (n > 0) await db.SaveChangesAsync(ct);
        return n;
    }

    // ---- 3. stock changes made with ExecuteUpdate (inventory) --------------------------------------------------------------------

    /// <summary>
    /// Inventory changes use set-based updates that bypass the change tracker, so they call this afterwards, inside the same transaction: when the PUBLIC availability state of a
    /// visible listing changed, it gets a new version and a <c>listing.upserted</c> event (reserving one unit of many does not).
    /// </summary>
    public static async Task RefreshStockAsync(AppDbContext db, TimeProvider clock, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return;
        var ids = productIds.ToList();
        if ((await ChangedStockAsync(db, ids, ct)).Count == 0) return;                // the common case: no lock taken
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("RefreshStockAsync must run inside the transaction of the stock change.");
        using var side = db.Sibling(clock);                                            // same connection + transaction; the caller's pending entities stay untouched
        await LockCounterAsync(side, ct);
        var changed = await ChangedStockAsync(side, ids, ct);                         // re-check under the lock
        if (changed.Count == 0) return;
        var first = await AllocateAsync(side, changed.Count, ct);
        var outbox = new Outbox(side, clock);
        var i = 0;
        foreach (var (id, (sellerId, sig)) in changed.OrderBy(c => c.Key))
        {
            var version = first + i++;
            await side.Products.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.CatalogVersion, version).SetProperty(p => p.CatalogStockState, sig), ct);
            outbox.Enqueue("listing.upserted", sellerId, id.ToString(), new { listingId = id, version }, version);
        }
        await side.SaveChangesAsync(ct);
    }

    private static async Task<Dictionary<Guid, (Guid SellerId, string Sig)>> ChangedStockAsync(AppDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var rows = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id) && p.CatalogListed)
            .Select(p => new { p.Id, p.SellerId, p.Type, p.HasVariants, p.Stock, p.Reserved, p.AllowBackorder, p.LowStockAlert, p.CatalogStockState }).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var rowIds = rows.Select(r => r.Id).ToList();
        var vars = (await db.Variants.AsNoTracking().Where(v => rowIds.Contains(v.ProductId) && v.Enabled).Select(v => new { v.ProductId, v.Id, v.Stock, v.Reserved }).ToListAsync(ct))
            .ToLookup(v => v.ProductId, v => new CatalogRules.VariantStock(v.Id, v.Stock, v.Reserved));
        var res = new Dictionary<Guid, (Guid, string)>();
        foreach (var r in rows)
        {
            var sig = CatalogRules.Signature(r.Type, r.HasVariants, r.Stock, r.Reserved, r.AllowBackorder, r.LowStockAlert, vars[r.Id]);
            if (sig != r.CatalogStockState) res[r.Id] = (r.SellerId, sig);
        }
        return res;
    }
}
