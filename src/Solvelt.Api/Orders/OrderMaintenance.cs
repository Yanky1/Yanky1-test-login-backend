using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Orders;

/// <summary>
/// Periodic jobs (hosted worker + callable from tests): auto-accept, delivered -> completed after the return window, PII redaction after retention,
/// overdue-reply notifications, replay-table cleanup.
/// </summary>
public class OrderMaintenance(AppDbContext db, OrderActions actions, IOptions<OrderOptions> opt, TimeProvider time, IFileStorage storage, OrderNotifier notifier, ReservationDrift drift, InflightHolds inflight)
{
    public record Summary(int AutoAccepted, int Completed, int Redacted, int ConversationsRedacted, int OverdueNotified, int NoncesRemoved, int ReservationsRepaired = 0);

    public async Task<Summary> RunAsync(CancellationToken ct = default)
    {
        var o = opt.Value;
        var now = time.UtcNow();
        int accepted = 0, completed = 0, redacted = 0, convRedacted = 0, overdue = 0;

        if (o.AutoAcceptMinutes > 0)
        {
            var cutoff = now.AddMinutes(-o.AutoAcceptMinutes);
            var ids = await db.Orders.Where(x => x.Status == OrderStatus.New && x.PlacedAt <= cutoff).Select(x => x.Id).Take(200).ToListAsync(ct);
            foreach (var id in ids)
            {
                var order = await actions.Full(db.Orders.Where(x => x.Id == id)).FirstAsync(ct);
                if ((await actions.AcceptAsync(order, Guid.Empty, null, Party.System)).Ok) accepted++;
                db.ChangeTracker.Clear();
            }
        }

        // delivered orders become Completed once the return window has passed
        var windowEnd = now.AddDays(-o.ReturnWindowDays);
        var done = await db.Orders.Where(x => (x.Status == OrderStatus.Delivered && x.DeliveredAt != null && x.DeliveredAt <= windowEnd
            || x.Status == OrderStatus.ReturnRejected && x.UpdatedAt <= windowEnd)).ToListAsync(ct);
        foreach (var x in done)
        {
            OrderStateMachine.Move(x, OrderStatus.Completed);
            x.CompletedAt = now; x.Version++; x.UpdatedAt = now;
            db.AddEvent(x.Id, "order.completed", Party.System, null, "Return window closed", now);
            completed++;
        }
        await db.SaveChangesAsync(ct);

        // privacy: redact address/phone/names N days after completion or cancellation (and conversations after the same retention)
        var cut = now.AddDays(-o.RedactAfterDays);
        var pii = await db.Orders.Where(x => x.PiiRedactedAt == null
            && ((x.CompletedAt != null && x.CompletedAt <= cut) || (x.CancelledAt != null && x.CancelledAt <= cut)
                || (x.Status == OrderStatus.Refunded && x.UpdatedAt <= cut))).ToListAsync(ct);
        foreach (var x in pii)
        {
            x.BuyerName = "Redacted"; x.BuyerPhone = null; x.BuyerRelayEmail = null; x.GiftMessage = null;
            x.ShipName = "Redacted"; x.ShipLine1 = "Redacted"; x.ShipLine2 = null; x.ShipCity = "Redacted"; x.ShipRegion = null; x.ShipPostalCode = "Redacted"; x.ShipLat = null; x.ShipLng = null;
            x.PiiRedactedAt = now; x.Version++; x.UpdatedAt = now;
            // free-text that can carry buyer data: event texts, buyer comments, cancellation notes, internal notes, outbound payloads
            await db.OrderEvents.Where(e => e.OrderId == x.Id && e.Type != "order.pii_redacted").ExecuteUpdateAsync(u => u.SetProperty(e => e.Text, (string?)null), ct);
            await db.Returns.Where(r => r.OrderId == x.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.BuyerComment, (string?)null).SetProperty(r => r.DecisionReason, (string?)null), ct);
            await db.Cancellations.Where(c => c.OrderId == x.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Note, (string?)null).SetProperty(c => c.DecisionNote, (string?)null), ct);
            await db.OrderNotes.Where(n => n.OrderId == x.Id).ExecuteDeleteAsync(ct);
            var mid = x.MarketplaceOrderId;
            var quotedMid = "\"marketplaceOrderId\":" + System.Text.Json.JsonSerializer.Serialize(mid, OrderJson.Opts);   // delimited, so MP-1 never matches MP-10
            if (!await db.Orders.AnyAsync(y => y.MarketplaceOrderId == mid && y.PiiRedactedAt == null && y.Id != x.Id, ct))
                await db.Outbox.Where(ob => (ob.AggregateId == mid || ob.PayloadJson.Contains(quotedMid)) && ob.Status != OutboxStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(ob => ob.PayloadJson, "{\"redacted\":true}"), ct);
            db.AddEvent(x.Id, "order.pii_redacted", Party.System, null, "Buyer details removed after the retention period", now);
            redacted++;
        }
        await db.SaveChangesAsync(ct);

        var staleConvs = await db.Conversations.Where(c => c.PiiRedactedAt == null
            && ((c.OrderId != null && db.Orders.Any(x => x.Id == c.OrderId && x.PiiRedactedAt != null))
                || (c.OrderId == null && c.LastMessageAt <= cut))).ToListAsync(ct);
        foreach (var c in staleConvs)
        {
            c.BuyerName = "Redacted"; c.PiiRedactedAt = now; c.AwaitingReplySince = null;
            var atts = await db.MessageAttachments.Where(a => a.ConversationId == c.Id).ToListAsync(ct);
            foreach (var a in atts) { await storage.DeleteAsync(a.StorageKey, ct); }
            db.MessageAttachments.RemoveRange(atts);
            await db.Messages.Where(m => m.ConversationId == c.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Text, "[removed after the retention period]"), ct);
            await db.ConversationNotes.Where(n => n.ConversationId == c.Id).ExecuteDeleteAsync(ct);
            var agg = c.MarketplaceConversationId ?? c.Id.ToString();
            await db.Outbox.Where(ob => ob.AggregateId == agg && ob.Status != OutboxStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(ob => ob.PayloadJson, "{\"redacted\":true}"), ct);
            convRedacted++;
        }
        await db.SaveChangesAsync(ct);

        // overdue replies: one notification per overdue stretch
        var dueCut = now.AddHours(-o.ReplyDueHours);
        var late = await db.Conversations.Where(c => c.AwaitingReplySince != null && !c.NoReplyNeeded && c.AwaitingReplySince < dueCut && c.OverdueNotifiedAt == null && c.PiiRedactedAt == null).Take(200).ToListAsync(ct);
        foreach (var c in late) { c.OverdueNotifiedAt = now; await notifier.OverdueAsync(c, ct); overdue++; }
        await db.SaveChangesAsync(ct);

        // abandoned drafts (uploaded, never sent) disappear after a day
        var stale = await db.MessageAttachments.Where(a => a.MessageId == null && a.CreatedAt < now.AddDays(-1)).ToListAsync(ct);
        foreach (var a in stale) await storage.DeleteAsync(a.StorageKey, ct);
        db.MessageAttachments.RemoveRange(stale);
        await db.SaveChangesAsync(ct);

        var nonces = await db.InboundNonces.Where(n => n.ExpiresAt < now).ExecuteDeleteAsync(ct);
        return new(accepted, completed, redacted, convRedacted, overdue, nonces, await ReconcileReservationsAsync(now, ct));
    }

    /// <summary>
    /// Repairs reservation leaks (a crash between "stock reserved" and "order saved", a failed release): for every product/variant the held stock must equal
    /// the sum of the open order lines' <c>ReservedQty</c>. Because an ingest in flight legitimately holds stock for a moment, a difference is only repaired
    /// once the SAME difference has been seen for <see cref="ReservationDrift.Grace"/>.
    /// </summary>
    private async Task<int> ReconcileReservationsAsync(DateTime now, CancellationToken ct)
    {
        var sums = (await db.OrderLines.AsNoTracking().Where(l => l.ReservedQty > 0).GroupBy(l => new { l.ProductId, l.VariantId })
            .Select(g => new { g.Key.ProductId, g.Key.VariantId, Sum = g.Sum(x => x.ReservedQty) }).ToListAsync(ct))
            .ToDictionary(x => (x.ProductId, x.VariantId), x => x.Sum);
        var held = new List<(Guid Id, Guid ProductId, bool Variant, int Reserved)>();
        held.AddRange((await db.Products.AsNoTracking().Where(p => p.Reserved > 0 && !p.HasVariants).Select(p => new { p.Id, p.Reserved }).ToListAsync(ct)).Select(p => (p.Id, p.Id, false, p.Reserved)));
        held.AddRange((await db.Variants.AsNoTracking().Where(v => v.Reserved > 0).Select(v => new { v.Id, v.ProductId, v.Reserved }).ToListAsync(ct)).Select(v => (v.Id, v.ProductId, true, v.Reserved)));
        var repaired = 0; var seen = new HashSet<Guid>();
        foreach (var (id, productId, isVariant, reserved) in held)
        {
            // holds of ingests that are running right now (reserved, not saved yet) are legitimate even though no order line shows them yet
            var expected = sums.GetValueOrDefault((productId, isVariant ? (Guid?)id : null)) + inflight.Sum(productId, isVariant ? id : null);
            seen.Add(id);
            if (reserved <= expected) { drift.Forget(id); continue; }           // only leaks (more held than any order explains) are repaired
            if (!drift.Confirm(id, reserved, expected, now)) continue;
            var n = isVariant
                ? await db.Variants.Where(v => v.Id == id && v.Reserved == reserved).ExecuteUpdateAsync(u => u.SetProperty(v => v.Reserved, expected), ct)
                : await db.Products.Where(p => p.Id == id && p.Reserved == reserved).ExecuteUpdateAsync(u => u.SetProperty(p => p.Reserved, expected), ct);
            if (n == 1) { repaired++; drift.Forget(id); }
        }
        drift.Prune(seen);
        return repaired;
    }
}

/// <summary>Remembers (in memory) since when a reservation difference has been observed unchanged.</summary>
public class ReservationDrift
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    private readonly Dictionary<Guid, (int Reserved, int Expected, DateTime Since)> _seen = [];
    private readonly object _l = new();

    public bool Confirm(Guid id, int reserved, int expected, DateTime now)
    {
        lock (_l)
        {
            if (_seen.TryGetValue(id, out var s) && s.Reserved == reserved && s.Expected == expected) return now - s.Since >= Grace;
            _seen[id] = (reserved, expected, now);
            return false;
        }
    }
    public void Forget(Guid id) { lock (_l) _seen.Remove(id); }
    public void Prune(HashSet<Guid> still) { lock (_l) foreach (var k in _seen.Keys.Where(k => !still.Contains(k)).ToList()) _seen.Remove(k); }
}

/// <summary>
/// Stock holds of ingests that are running right now (this process): reserved but the order is not saved yet. The reconciler counts them as expected, so it can
/// never wipe a legitimate hold of a slow request. (Another instance's in-flight holds are covered by the reconciler's grace period.)
/// </summary>
public class InflightHolds
{
    private readonly Dictionary<Guid, List<(Guid ProductId, Guid? VariantId, int Qty)>> _active = [];
    private readonly object _l = new();

    public sealed class Scope(InflightHolds owner, Guid id) : IDisposable
    {
        public void Add(Guid productId, Guid? variantId, int qty) { lock (owner._l) owner._active[id].Add((productId, variantId, qty)); }
        public void Dispose() { lock (owner._l) owner._active.Remove(id); }
    }

    public Scope Begin() { var id = Guid.NewGuid(); lock (_l) _active[id] = []; return new Scope(this, id); }
    public int Sum(Guid productId, Guid? variantId) { lock (_l) return _active.Values.SelectMany(x => x).Where(h => h.ProductId == productId && h.VariantId == variantId).Sum(h => h.Qty); }
}
