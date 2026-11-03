using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Orders;

public record BuyerCancelRequest(string? RequestId, string? SellerOrderNumber, string? Reason, string? Note);
public record BuyerReturnLine(string? MarketplaceLineId, int Quantity);
public record BuyerReturnRequest(string? ReturnId, string? SellerOrderNumber, string? Reason, string? Comment, List<BuyerReturnLine>? Lines);
public record BuyerDelivery(string? SellerOrderNumber, string? TrackingNumber, DateTime? DeliveredAt);

/// <summary>Buyer-originated events coming from the marketplace (cancel requests, return requests, delivery confirmations).</summary>
public class BuyerEventService(AppDbContext db, OrderActions actions, Outbox outbox, TimeProvider time, OrderNotifier notifier)
{
    private Task<List<SellerOrder>> Targets(string marketplaceOrderId, string? number) =>
        actions.Full(db.Orders.Where(o => o.MarketplaceOrderId == marketplaceOrderId && (number == null || o.Number == number))).ToListAsync();

    public record PerOrder(string Number, string Result, string? Message = null);

    public async Task<(int Status, string Code, string Message, List<PerOrder> Orders)> CancelRequestAsync(string marketplaceOrderId, BuyerCancelRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId) || req.RequestId.Length > 64) return (422, "requestId", "requestId is required (max 64 characters).", []);
        var reason = req.Reason?.Trim().ToLowerInvariant();
        if (reason is null || !OrderSupport.BuyerCancelReasons.Contains(reason)) return (422, "reason", "reason must be one of: " + string.Join(", ", OrderSupport.BuyerCancelReasons), []);
        if (req.Note is { Length: > 1000 }) return (422, "note", "note is too long.", []);
        var orders = await Targets(marketplaceOrderId, req.SellerOrderNumber);
        if (orders.Count == 0) return (404, "not_found", "Unknown order.", []);
        var results = new List<PerOrder>();
        var now = time.UtcNow();
        foreach (var o in orders)
        {
            if (await db.Cancellations.AnyAsync(c => c.OrderId == o.Id && c.MarketplaceRequestId == req.RequestId)) { results.Add(new(o.Number, "duplicate")); continue; }
            if (o.Status == OrderStatus.Cancelled) { results.Add(new(o.Number, "already_cancelled")); continue; }
            var shippedAlready = o.Lines.Any(l => l.ShippedQty > 0) || !OrderStateMachine.Can(o.Status, OrderStatus.Cancelled) && o.Status != OrderStatus.OnHold;
            var c = new OrderCancellation { OrderId = o.Id, MarketplaceRequestId = req.RequestId, RequestedBy = Party.Buyer, Reason = reason, Note = req.Note?.Trim(), CreatedAt = now };
            if (shippedAlready)
            {
                // too late: answer for the seller so the buyer isn't left hanging (they can still return the goods)
                c.Status = CancelRequestStatus.Rejected; c.ResolvedAt = now; c.DecisionNote = "The order has already shipped.";
                db.Cancellations.Add(c);
                db.AddEvent(o.Id, "cancellation.auto_rejected", Party.System, null, "Buyer asked to cancel after shipping; declined automatically", now);
                outbox.Enqueue("cancellation.decided", o.SellerId, o.MarketplaceOrderId, new { order = new { sellerOrderId = o.Id, sellerOrderNumber = o.Number, marketplaceOrderId = o.MarketplaceOrderId }, requestId = req.RequestId, decision = "rejected", note = c.DecisionNote });
                await db.SaveChangesAsync();
                results.Add(new(o.Number, "auto_rejected"));
                continue;
            }
            c.Status = CancelRequestStatus.Requested;
            db.Cancellations.Add(c);
            o.CancelRequested = true; o.Version++; o.UpdatedAt = now;
            db.AddEvent(o.Id, "cancellation.requested", Party.Buyer, null, $"{reason}" + (req.Note is null ? "" : $": {req.Note.Trim()}"), now);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); results.Add(new(o.Number, "duplicate")); continue; }
            await notifier.CancelRequestedAsync(o);
            results.Add(new(o.Number, "requested"));
        }
        return (200, "ok", "", results);
    }

    public async Task<(int Status, string Code, string Message, List<PerOrder> Orders)> ReturnRequestAsync(string marketplaceOrderId, BuyerReturnRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ReturnId) || req.ReturnId.Length > 64) return (422, "returnId", "returnId is required (max 64 characters).", []);
        var reason = req.Reason?.Trim().ToLowerInvariant();
        if (reason is null || !OrderSupport.BuyerReturnReasons.Contains(reason)) return (422, "reason", "reason must be one of: " + string.Join(", ", OrderSupport.BuyerReturnReasons), []);
        if (req.Comment is { Length: > 1000 }) return (422, "comment", "comment is too long.", []);
        if (req.Lines is null or { Count: 0 } or { Count: > 100 } || req.Lines.Any(l => l.Quantity < 1 || string.IsNullOrWhiteSpace(l.MarketplaceLineId)))
            return (422, "lines", "lines need marketplaceLineId and quantity >= 1.", []);
        var orders = await Targets(marketplaceOrderId, req.SellerOrderNumber);
        if (orders.Count == 0) return (404, "not_found", "Unknown order.", []);
        var results = new List<PerOrder>();
        var now = time.UtcNow();
        var unmatched = req.Lines.Where(l => !orders.Any(o => o.Lines.Any(x => x.MarketplaceLineId == l.MarketplaceLineId))).ToList();
        if (unmatched.Count > 0) return (422, "lines", "Unknown marketplaceLineId: " + string.Join(", ", unmatched.Select(u => u.MarketplaceLineId)), []);
        foreach (var o in orders)
        {
            var mine = req.Lines.Where(l => o.Lines.Any(x => x.MarketplaceLineId == l.MarketplaceLineId)).ToList();
            if (mine.Count == 0) continue;
            if (await db.Returns.AnyAsync(r => r.OrderId == o.Id && r.MarketplaceReturnId == req.ReturnId)) { results.Add(new(o.Number, "duplicate")); continue; }
            if (!OrderStateMachine.Can(o.Status, OrderStatus.ReturnRequested)) { results.Add(new(o.Number, "rejected", $"The order is {OrderStateMachine.Label(o.Status)}; a return can't be requested.")); continue; }
            var rr = new ReturnRequest { OrderId = o.Id, MarketplaceReturnId = req.ReturnId, Status = ReturnStatus.Requested, Reason = reason, BuyerComment = req.Comment?.Trim(), CreatedAt = now };
            string? bad = null;
            foreach (var l in mine.GroupBy(m => m.MarketplaceLineId))
            {
                var line = o.Lines.First(x => x.MarketplaceLineId == l.Key);
                var qty = l.Sum(x => x.Quantity);
                if (qty > line.ShippedQty - line.ReturnedQty - line.RefundedQty) { bad = $"Only {line.ShippedQty - line.ReturnedQty - line.RefundedQty} of \"{line.Title}\" can be returned."; break; }
                rr.Lines.Add(new ReturnLine { ReturnId = rr.Id, OrderLineId = line.Id, Quantity = qty });
            }
            if (bad is not null) { results.Add(new(o.Number, "rejected", bad)); continue; }
            db.Returns.Add(rr);
            OrderStateMachine.Move(o, OrderStatus.ReturnRequested);
            o.Version++; o.UpdatedAt = now;
            db.AddEvent(o.Id, "return.requested", Party.Buyer, null, reason, now);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); results.Add(new(o.Number, "duplicate")); continue; }
            await notifier.ReturnRequestedAsync(o);
            results.Add(new(o.Number, "requested"));
        }
        return (200, "ok", "", results);
    }

    public async Task<(int Status, string Code, string Message, List<PerOrder> Orders)> DeliveryAsync(string marketplaceOrderId, BuyerDelivery req)
    {
        var orders = await Targets(marketplaceOrderId, req.SellerOrderNumber);
        if (orders.Count == 0) return (404, "not_found", "Unknown order.", []);
        var results = new List<PerOrder>();
        foreach (var o in orders)
        {
            if (o.Status is OrderStatus.Delivered or OrderStatus.Completed) { results.Add(new(o.Number, "already_delivered")); continue; }
            var r = await actions.MarkDeliveredAsync(o, Guid.Empty, null, Party.Buyer, req.TrackingNumber, req.DeliveredAt);
            results.Add(new(o.Number, r.Ok ? "delivered" : "rejected", r.Ok ? null : r.Message));
            db.ChangeTracker.Clear();
        }
        return (200, "ok", "", results);
    }
}
