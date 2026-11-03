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
public class OrderMaintenance(AppDbContext db, OrderActions actions, IOptions<OrderOptions> opt, TimeProvider time, IFileStorage storage, OrderNotifier notifier)
{
    public record Summary(int AutoAccepted, int Completed, int Redacted, int ConversationsRedacted, int OverdueNotified, int NoncesRemoved);

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
            x.ShipName = "Redacted"; x.ShipLine1 = "Redacted"; x.ShipLine2 = null; x.ShipCity = "Redacted"; x.ShipRegion = null; x.ShipPostalCode = "Redacted";
            x.PiiRedactedAt = now; x.Version++; x.UpdatedAt = now;
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
            convRedacted++;
        }
        await db.SaveChangesAsync(ct);

        // overdue replies: one notification per overdue stretch
        var dueCut = now.AddHours(-o.ReplyDueHours);
        var late = await db.Conversations.Where(c => c.AwaitingReplySince != null && !c.NoReplyNeeded && c.AwaitingReplySince < dueCut && c.OverdueNotifiedAt == null && c.PiiRedactedAt == null).Take(200).ToListAsync(ct);
        foreach (var c in late) { c.OverdueNotifiedAt = now; await notifier.OverdueAsync(c, ct); overdue++; }
        await db.SaveChangesAsync(ct);

        var nonces = await db.InboundNonces.Where(n => n.ExpiresAt < now).ExecuteDeleteAsync(ct);
        return new(accepted, completed, redacted, convRedacted, overdue, nonces);
    }
}

