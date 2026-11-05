using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Orders;

/// <summary>
/// C16: the signed read of a marketplace order (GET /api/internal/v1/orders/{marketplaceOrderId}) for reconciliation after an event with <c>predecessorDead</c> and for manual reconciles.
/// Allow-list only: no buyer name, address, e-mail, phone, notes or messages. The money is the stored numbers the events were built from (the refund's own columns, never recomputed).
/// </summary>
public static class OrderRead
{
    public static async Task<object?> BuildAsync(AppDbContext db, string marketplaceOrderId, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.Shipments).Where(o => o.MarketplaceOrderId == marketplaceOrderId).OrderBy(o => o.CreatedAt).ThenBy(o => o.Number).ToListAsync(ct);
        if (orders.Count == 0) return null;
        var ids = orders.Select(o => o.Id).ToList();
        var refunds = await db.Refunds.AsNoTracking().Include(r => r.Lines).Where(r => ids.Contains(r.OrderId)).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var returns = await db.Returns.AsNoTracking().Where(r => ids.Contains(r.OrderId)).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var scopes = orders.ToDictionary(o => o.Id, OutboxScopes.Order);
        var scopeList = scopes.Values.ToList();
        var seq = await db.Outbox.AsNoTracking().Where(m => m.SequenceScope != null && scopeList.Contains(m.SequenceScope!) && m.OrderKey != null)
            .GroupBy(m => m.SequenceScope!).Select(g => new { Scope = g.Key, Max = g.Max(m => m.OrderKey) }).ToListAsync(ct);
        return new
        {
            marketplaceOrderId,
            sellerOrders = orders.Select(o => new
            {
                sellerOrderId = o.Id, sellerOrderNumber = o.Number, sellerId = o.SellerId, brandId = o.BrandId, status = o.Status.ToString(), updatedAt = o.UpdatedAt,
                currency = o.Currency, totalCents = o.TotalCents, taxCents = o.TaxCents, shippingCents = o.ShippingCents, shippingTaxCents = o.ShippingTaxCents, refundedCents = o.RefundedCents,
                lines = o.Lines.OrderBy(l => l.MarketplaceLineId).ThenBy(l => l.Id).Select(l => new { marketplaceLineId = l.MarketplaceLineId, sku = l.Sku, quantity = l.Quantity, shippedQty = l.ShippedQty, cancelledQty = l.CancelledQty, refundedQty = l.RefundedQty, returnedQty = l.ReturnedQty }),
                refunds = refunds.Where(r => r.OrderId == o.Id).Select(r => new
                {
                    refundId = r.Id, amountCents = r.AmountCents,
                    taxCents = r.Lines.Sum(x => x.TaxCents) + r.ShippingTaxCents,        // exactly the refund.issued event's taxCents: all the tax inside amountCents
                    shippingCents = r.ShippingCents, shippingTaxCents = r.ShippingTaxCents, reason = r.Reason, createdAt = r.CreatedAt
                }),
                shipments = o.Shipments.OrderBy(s => s.ShippedAt).ThenBy(s => s.Id).Select(s => new { shipmentId = s.Id, method = s.Method, carrier = s.Carrier, trackingNumber = s.TrackingNumber, status = s.TrackingStatus, shippedAt = s.ShippedAt, deliveredAt = s.DeliveredAt }),
                returns = returns.Where(r => r.OrderId == o.Id).Select(r => new
                {
                    returnId = r.MarketplaceReturnId ?? r.Id.ToString(), status = r.Status.ToString(), createdAt = r.CreatedAt,
                    updatedAt = new[] { r.CreatedAt, r.DecidedAt ?? DateTime.MinValue, r.ReceivedAt ?? DateTime.MinValue }.Max()
                }),
                lastEventSequence = seq.FirstOrDefault(x => x.Scope == scopes[o.Id])?.Max
            })
        };
    }
}
