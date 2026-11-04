using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;

namespace Solvelt.Api.Catalog;

/// <summary>
/// Re-derives the delivery options of listings after a shipping profile, delivery setting or zone changed (the change only leaves a <see cref="CatalogRefreshRequest"/>, so the settings
/// request never waits for a big account). For each request the visible listings in scope are handled in chunks, each chunk in its own short transaction: a listing whose delivery
/// signature differs from the one stored with its last version gets a new version and a <c>listing.upserted</c> through the outbox; the others are left alone. Idempotent: a repeated
/// or crashed run finds the signatures already equal and does nothing; several instances serialize on the version counter.
/// </summary>
public class CatalogRefreshJob(AppDbContext db, TimeProvider clock)
{
    public const int ChunkSize = 200;

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var requests = await db.CatalogRefreshRequests.AsNoTracking().Where(r => r.ProcessedAt == null).OrderBy(r => r.CreatedAt).Take(100).ToListAsync(ct);
        if (requests.Count == 0) return 0;
        var bumped = 0;
        foreach (var scope in requests.GroupBy(r => (r.SellerId, r.BrandId)))
        {
            var (seller, brand) = scope.Key;
            var ids = await db.Products.AsNoTracking().Where(p => p.SellerId == seller && (brand == null || p.BrandId == brand) && p.CatalogListed).Select(p => p.Id).ToListAsync(ct);
            foreach (var chunk in ids.Chunk(ChunkSize)) bumped += await RefreshChunkAsync(chunk, ct);
        }
        var done = requests.Select(r => r.Id).ToList();
        var now = clock.UtcNow();
        await db.CatalogRefreshRequests.Where(r => done.Contains(r.Id) && r.ProcessedAt == null).ExecuteUpdateAsync(u => u.SetProperty(r => r.ProcessedAt, now), ct);
        var old = now.AddDays(-7);
        await db.CatalogRefreshRequests.Where(r => r.ProcessedAt != null && r.ProcessedAt < old).ExecuteDeleteAsync(ct);
        return bumped;
    }

    private async Task<int> RefreshChunkAsync(Guid[] ids, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CatalogTracking.LockCounterAsync(db, ct);                                  // from here on what is committed decides
        var products = await db.Products.Where(p => ids.Contains(p.Id) && p.CatalogListed).ToListAsync(ct);
        var now = await DeliveryContext.ForProductsAsync(db, products, ct);
        var changed = products.Where(p => p.CatalogDeliveryState != now[p.Id].Signature).OrderBy(p => p.Id).ToList();
        if (changed.Count > 0)
        {
            var first = await CatalogTracking.AllocateAsync(db, changed.Count, ct);
            var outbox = new Outbox(db, clock);
            for (var i = 0; i < changed.Count; i++)
            {
                var p = changed[i]; var version = first + i;
                p.CatalogVersion = version; p.CatalogDeliveryState = now[p.Id].Signature;
                outbox.Enqueue("listing.upserted", p.SellerId, p.Id.ToString(), new { listingId = p.Id, version }, version);
            }
            await db.SaveChangesAsync(ct);                                                // only catalog bookkeeping columns changed: the SaveChanges hook adds nothing
        }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return changed.Count;
    }
}
