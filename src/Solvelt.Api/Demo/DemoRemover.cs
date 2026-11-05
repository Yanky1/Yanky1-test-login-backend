using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Demo;

/// <summary>
/// Removes exactly the demo records of ONE account (the registry says which they are), children before parents, in one transaction. Real records of the account are never
/// touched: every delete is keyed by the ids the fill registered. The ledger is append-only for the application; the demo journals are the one deliberate exception (set-based
/// delete, see docs/DECISIONS.md), because demo money never existed and leaving it would put fake balances on a real account.
/// </summary>
public class DemoRemover(AppDbContext db, IFileStorage storage, IAuditLog audit)
{
    public async Task<DemoCounts> CountsAsync(Guid sellerId)
    {
        var rows = await db.DemoRecords.AsNoTracking().Where(r => r.SellerId == sellerId).GroupBy(r => r.Kind).Select(g => new { Kind = g.Key, N = g.Count() }).ToListAsync();
        int N(string k) => rows.FirstOrDefault(r => r.Kind == k)?.N ?? 0;
        // "notifications": the portal's in-app signals are the unread buyer conversations (there is no stored notification table); see DECISIONS
        return new(N("product"), N("service"), N("order"), N("booking"), N("quote"), N("conversation"), N("conversation"));
    }

    public async Task<DemoCounts> RemoveAsync(Guid sellerId, Guid actor, string reason = "removed")
    {
        var counts = await CountsAsync(sellerId);
        var recs = await db.DemoRecords.Where(r => r.SellerId == sellerId).OrderByDescending(r => r.Seq).ToListAsync();
        List<Guid> Ids(params string[] kinds) => recs.Where(r => kinds.Contains(r.Kind)).Select(r => r.EntityId).ToList();
        var orderIds = Ids("order"); var bookingIds = Ids("booking"); var quoteIds = Ids("quote"); var convIds = Ids("conversation");
        var productIds = Ids("product", "service"); var transferIds = Ids("transfer");
        var eventKeys = recs.Where(r => r.Kind == "payment_event").Select(r => r.Key!).ToList();
        var batch = await db.DemoBatches.FirstOrDefaultAsync(b => b.SellerId == sellerId);
        var shipFromIds = Ids("ship_from"); var packageIds = Ids("package"); var profileIdsS = Ids("shipping_profile"); var shipSettingIds = Ids("shipping_settings");
        var imageKeys = recs.Where(r => r.Kind == "image_file").Select(r => r.Key!).ToList();

        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;
        db.ChangeTracker.Clear();

        // payments first: the ledger rows point at orders and bookings
        var transferRefs = transferIds.Select(t => t.ToString()).ToList();
        var journalIds = await db.LedgerJournals.Where(j => j.SellerId == sellerId && ((j.OrderId != null && orderIds.Contains(j.OrderId.Value)) || (j.BookingId != null && bookingIds.Contains(j.BookingId.Value))
            || (j.SourceRef != null && transferRefs.Contains(j.SourceRef)))).Select(j => j.Id).ToListAsync();
        await db.LedgerEntries.Where(e => journalIds.Contains(e.JournalId)).ExecuteDeleteAsync();
        await db.LedgerJournals.Where(j => journalIds.Contains(j.Id)).ExecuteDeleteAsync();
        await db.PaymentTransfers.Where(t => t.SellerId == sellerId && transferIds.Contains(t.Id)).ExecuteDeleteAsync();
        await db.ProcessedPaymentEvents.Where(p => eventKeys.Contains(p.EventId)).ExecuteDeleteAsync();
        await db.PaymentReferences.Where(p => eventKeys.Contains(p.EventId)).ExecuteDeleteAsync();
        if (recs.Any(r => r.Kind == "payout_account")) await db.PayoutAccounts.Where(p => p.SellerId == sellerId).ExecuteDeleteAsync();

        // conversations and their messages (messages, attachments, reads, notes cascade)
        var orderConvs = await db.Conversations.Where(c => c.SellerId == sellerId && ((c.OrderId != null && orderIds.Contains(c.OrderId.Value)) || convIds.Contains(c.Id))).Select(c => c.Id).ToListAsync();
        await db.Conversations.Where(c => orderConvs.Contains(c.Id)).ExecuteDeleteAsync();

        // orders: everything that points at an order without a cascade first
        var deliveryIds = await db.LocalDeliveries.Where(d => orderIds.Contains(d.OrderId)).Select(d => d.Id).ToListAsync();
        await db.TrackingEvents.Where(t => orderIds.Contains(t.OrderId)).ExecuteDeleteAsync();
        await db.DeliveryAttempts.Where(x => deliveryIds.Contains(x.DeliveryId)).ExecuteDeleteAsync();
        await db.DeliveryProofs.Where(x => deliveryIds.Contains(x.DeliveryId)).ExecuteDeleteAsync();
        await db.DeliveryClientEvents.Where(x => deliveryIds.Contains(x.DeliveryId)).ExecuteDeleteAsync();
        await db.LocalDeliveries.Where(d => orderIds.Contains(d.OrderId)).ExecuteDeleteAsync();
        await db.ShippingLabels.Where(l => orderIds.Contains(l.OrderId)).ExecuteDeleteAsync();
        await db.ReturnShipments.Where(l => orderIds.Contains(l.OrderId)).ExecuteDeleteAsync();
        await db.ShippingQuotes.Where(l => orderIds.Contains(l.OrderId)).ExecuteDeleteAsync();
        await db.Orders.Where(o => o.SellerId == sellerId && orderIds.Contains(o.Id)).ExecuteDeleteAsync();   // lines, shipments, returns, refunds, notes, events cascade

        // bookings and quotes
        await db.BookingMessages.Where(m => bookingIds.Contains(m.BookingId)).ExecuteDeleteAsync();
        await db.Bookings.Where(b => b.SellerId == sellerId && bookingIds.Contains(b.Id)).ExecuteDeleteAsync();   // events cascade
        await db.QuoteRequests.Where(q => q.SellerId == sellerId && quoteIds.Contains(q.Id)).ExecuteDeleteAsync();

        // catalogue: removed through the change tracker so the catalog feed gets its tombstones (CatalogTracking)
        await db.Questions.Where(q => q.SellerId == sellerId && productIds.Contains(q.ProductId)).ExecuteDeleteAsync();
        await db.SkuClaims.Where(c => c.SellerId == sellerId && productIds.Contains(c.ProductId)).ExecuteDeleteAsync();
        await db.ServiceProviders.Where(l => productIds.Contains(l.ProductId)).ExecuteDeleteAsync();
        foreach (var chunk in productIds.Chunk(50))
        {
            var ps = await db.Products.Where(p => p.SellerId == sellerId && chunk.Contains(p.Id)).ToListAsync();
            db.Products.RemoveRange(ps);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        // settings and the rest of the account scaffolding the fill created
        await db.ShipFromAddresses.Where(x => x.SellerId == sellerId && shipFromIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.PackagePresets.Where(x => x.SellerId == sellerId && packageIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ShippingProfiles.Where(x => x.SellerId == sellerId && profileIdsS.Contains(x.Id)).ExecuteDeleteAsync();
        await db.SellerShippingSettings.Where(x => x.SellerId == sellerId && shipSettingIds.Contains(x.Id)).ExecuteDeleteAsync();
        var settingIds = Ids("delivery_setting");
        var windowIds = await db.DeliveryWindows.Where(w => settingIds.Contains(w.SettingId)).Select(w => w.Id).ToListAsync();
        await db.DeliveryWindowUsage.Where(u => windowIds.Contains(u.WindowId)).ExecuteDeleteAsync();
        await db.DeliverySettings.Where(s => s.SellerId == sellerId && settingIds.Contains(s.Id)).ExecuteDeleteAsync();   // zones and windows cascade
        var hourIds = Ids("working_hour");
        await db.WorkingHours.Where(w => hourIds.Contains(w.Id)).ExecuteDeleteAsync();
        var profileIds = Ids("provider_profile");
        await db.ProviderProfiles.Where(p => p.SellerId == sellerId && profileIds.Contains(p.MembershipId)).ExecuteDeleteAsync();
        var flagIds = Ids("provider_flag");
        await db.ProviderProfiles.Where(p => p.SellerId == sellerId && flagIds.Contains(p.MembershipId)).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsProvider, false));
        var brandIds = Ids("brand");
        await db.Brands.Where(b => b.SellerId == sellerId && brandIds.Contains(b.Id) && !db.Products.Any(p => p.BrandId == b.Id) && !db.Orders.Any(o => o.BrandId == b.Id)).ExecuteDeleteAsync();

        await db.DemoRecords.Where(r => r.SellerId == sellerId).ExecuteDeleteAsync();
        if (batch is not null) await db.DemoBatches.Where(b => b.Id == batch.Id).ExecuteDeleteAsync();
        if (tx is not null) await tx.CommitAsync();

        foreach (var key in imageKeys) { try { await storage.DeleteAsync(key); } catch { /* a missing file is already gone */ } }
        await audit.WriteAsync("demo.removed", actor, null, $"seller={sellerId};reason={reason};products={counts.Products};services={counts.Services};orders={counts.Orders};bookings={counts.Bookings}");
        return counts;
    }
}
