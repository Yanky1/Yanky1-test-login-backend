using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Orders;

/// <summary>JSON shapes of the seller order API.</summary>
public static class OrderViews
{
    public static bool IsLate(SellerOrder o, DateTime now) => o.ShipByAt < now && o.Status is OrderStatus.New or OrderStatus.Accepted or OrderStatus.PartiallyShipped or OrderStatus.OnHold
        && o.Lines.Any(l => OrderSupport.Unshipped(l) > 0);

    public static bool ReturnOpen(OrderStatus s) => s is OrderStatus.ReturnRequested or OrderStatus.ReturnApproved or OrderStatus.ReturnReceived;

    public static string Summary(SellerOrder o)
    {
        var first = o.Lines.FirstOrDefault();
        return first is null ? "" : o.Lines.Count == 1 ? first.Title : $"{first.Title} + {o.Lines.Count - 1} more";
    }

    public static object ListItem(SellerOrder o, string brandName, DateTime now) => new
    {
        id = o.Id, number = o.Number, marketplaceOrderId = o.MarketplaceOrderId, brandId = o.BrandId, brandName, placedAt = o.PlacedAt,
        status = o.Status.ToString(), buyerName = o.BuyerName, currency = o.Currency, totalCents = o.TotalCents, netCents = o.NetCents, refundedCents = o.RefundedCents,
        itemCount = o.Lines.Sum(l => l.Quantity), itemsSummary = Summary(o), thumbnailLineId = o.Lines.FirstOrDefault(l => l.ImageKey != null)?.Id,
        shipByAt = o.ShipByAt, deliverByAt = o.DeliverByAt, late = IsLate(o, now), oversold = o.Oversold, cancelRequested = o.CancelRequested,
        returnOpen = ReturnOpen(o.Status), shippingSpeed = o.ShippingSpeed, version = o.Version, redacted = o.PiiRedactedAt != null,
        needsAction = NeedsAction(o)
    };

    public static bool NeedsAction(SellerOrder o) => o.Status is OrderStatus.New or OrderStatus.Accepted or OrderStatus.PartiallyShipped or OrderStatus.ReturnRequested or OrderStatus.ReturnReceived
        || (o.CancelRequested && o.Status != OrderStatus.Cancelled);

    public static object Actions(SellerOrder o, AccountAccess a, long managerRefundLimit)
    {
        var w = a.CanWorkOnBrand(o.BrandId, true);
        var canCancelAll = OrderStateMachine.Can(o.Status, OrderStatus.Cancelled) && o.Lines.All(l => l.ShippedQty == 0);
        var canCancelRemainder = w && o.Lines.Any(l => l.ShippedQty > 0) && o.Lines.Any(l => OrderSupport.Unshipped(l) > 0)
            && (o.Status == OrderStatus.PartiallyShipped || o.Status == OrderStatus.OnHold && o.HeldFrom == OrderStatus.PartiallyShipped);
        var canRefund = w && o.Status != OrderStatus.Cancelled && o.Lines.Any(l => l.ShippedQty > 0) && o.RefundedCents < o.TotalCents && (a.Has(Permission.OrderRefund) || a.Role == AccountRole.Manager && managerRefundLimit > 0);
        return new
        {
            accept = w && OrderStateMachine.Can(o.Status, OrderStatus.Accepted),
            hold = w && o.Status is OrderStatus.New or OrderStatus.Accepted or OrderStatus.PartiallyShipped,
            resume = w && o.Status == OrderStatus.OnHold,
            ship = w && OrderStateMachine.CanShip(o.Status) && !o.CancelRequested,
            editTracking = w && o.Shipments.Any(s => s.DeliveredAt == null),
            markDelivered = w && o.Status == OrderStatus.Shipped,
            cancel = w && (canCancelAll || canCancelRemainder),
            cancelRemainder = canCancelRemainder,
            refund = canRefund,
            refundLimitCents = a.Has(Permission.OrderRefund) ? (long?)null : managerRefundLimit,
            note = w,
            decideCancellation = w && o.CancelRequested && o.Status != OrderStatus.Cancelled,
            decideReturn = w && o.Status == OrderStatus.ReturnRequested,
            receiveReturn = w && o.Status == OrderStatus.ReturnApproved,
        };
    }

    public static async Task<object?> DetailAsync(AppDbContext db, AccountAccess a, Guid id, DateTime now, long managerRefundLimit)
    {
        var o = await db.OrdersFor(a).AsNoTracking().AsSplitQuery().Include(x => x.Lines).Include(x => x.Shipments).ThenInclude(s => s.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (o is null) return null;
        var brand = await db.Brands.AsNoTracking().Where(b => b.Id == o.BrandId).Select(b => b.Name).FirstOrDefaultAsync() ?? "";
        var cancels = await db.Cancellations.AsNoTracking().Where(c => c.OrderId == id).OrderBy(c => c.CreatedAt).ToListAsync();
        var returns = await db.Returns.AsNoTracking().Include(r => r.Lines).Where(r => r.OrderId == id).OrderBy(r => r.CreatedAt).ToListAsync();
        var refunds = await db.Refunds.AsNoTracking().Include(r => r.Lines).Where(r => r.OrderId == id).OrderBy(r => r.CreatedAt).ToListAsync();
        var events = await db.OrderEvents.AsNoTracking().Where(e => e.OrderId == id).OrderBy(e => e.At).ThenBy(e => e.Type).ToListAsync();
        var notes = await db.OrderNotes.AsNoTracking().Where(n => n.OrderId == id).OrderBy(n => n.CreatedAt).ToListAsync();
        var conv = await db.Conversations.AsNoTracking().Where(c => c.OrderId == id).Select(c => (Guid?)c.Id).FirstOrDefaultAsync();
        var authors = notes.Select(n => n.AuthorId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => authors.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email ?? "");
        return new
        {
            id = o.Id, number = o.Number, marketplaceOrderId = o.MarketplaceOrderId, brandId = o.BrandId, brandName = brand, placedAt = o.PlacedAt, status = o.Status.ToString(),
            heldFrom = o.HeldFrom?.ToString(), currency = o.Currency, version = o.Version, late = IsLate(o, now), oversold = o.Oversold, cancelRequested = o.CancelRequested,
            returnOpen = ReturnOpen(o.Status), redacted = o.PiiRedactedAt != null, shipByAt = o.ShipByAt, deliverByAt = o.DeliverByAt, shippingMethod = o.ShippingMethod,
            shippingSpeed = o.ShippingSpeed, giftMessage = o.GiftMessage, cancelReason = o.CancelReason, cancelledBy = o.CancelledBy?.ToString(), shippedLate = o.ShippedLate,
            buyer = new { name = o.BuyerName, relayEmail = o.BuyerRelayEmail, phone = o.BuyerPhone },
            shippingAddress = new { name = o.ShipName, line1 = o.ShipLine1, line2 = o.ShipLine2, city = o.ShipCity, region = o.ShipRegion, postalCode = o.ShipPostalCode, country = o.ShipCountry },
            money = new
            {
                itemsCents = o.ItemsCents, discountCents = o.DiscountCents, shippingCents = o.ShippingCents, taxCents = o.TaxCents, totalCents = o.TotalCents,
                feeCents = o.FeeCents, netCents = o.NetCents, refundedCents = o.RefundedCents, refundableCents = o.Status == OrderStatus.Cancelled ? 0 : o.TotalCents - o.RefundedCents
            },
            lines = o.Lines.Select(l => new
            {
                id = l.Id, productId = l.ProductId, variantId = l.VariantId, title = l.Title, sku = l.Sku, options = l.OptionsText, hasImage = l.ImageKey != null,
                unitPriceCents = l.UnitPriceCents, quantity = l.Quantity, discountCents = l.DiscountCents, taxCents = l.TaxCents, lineTotalCents = OrderSupport.LineNet(l),
                shippedQty = l.ShippedQty, cancelledQty = l.CancelledQty, refundedQty = l.RefundedQty, returnedQty = l.ReturnedQty, unshippedQty = OrderSupport.Unshipped(l),
                reservedQty = l.ReservedQty, stockShort = o.Oversold && l.ReservedQty == 0 && l.ShippedQty == 0 && l.CancelledQty == 0
            }),
            shipments = o.Shipments.OrderBy(s => s.ShippedAt).Select(s => new
            {
                id = s.Id, carrier = s.Carrier, service = s.Service, trackingNumber = s.TrackingNumber, trackingUrl = Tracking.Url(s.Carrier, s.TrackingNumber), shippedAt = s.ShippedAt,
                deliveredAt = s.DeliveredAt, method = s.Method, trackingStatus = s.TrackingStatus, eta = s.Eta, lines = s.Lines.Select(x => new { lineId = x.OrderLineId, quantity = x.Quantity })
            }),
            cancellations = cancels.Select(c => new { id = c.Id, requestedBy = c.RequestedBy.ToString(), status = c.Status.ToString(), reason = c.Reason, note = c.Note, decisionNote = c.DecisionNote, createdAt = c.CreatedAt, resolvedAt = c.ResolvedAt }),
            returns = returns.Select(r => new
            {
                id = r.Id, status = r.Status.ToString(), reason = r.Reason, buyerComment = r.BuyerComment, decisionReason = r.DecisionReason, restock = r.Restock, refundCents = r.RefundCents,
                createdAt = r.CreatedAt, decidedAt = r.DecidedAt, receivedAt = r.ReceivedAt, lines = r.Lines.Select(x => new { lineId = x.OrderLineId, quantity = x.Quantity })
            }),
            refunds = refunds.Select(r => new
            {
                id = r.Id, returnId = r.ReturnId, amountCents = r.AmountCents, shippingCents = r.ShippingCents, reason = r.Reason, note = r.Note, createdAt = r.CreatedAt,
                lines = r.Lines.Select(x => new { lineId = x.OrderLineId, quantity = x.Quantity, amountCents = x.AmountCents })
            }),
            timeline = events.Select(e => new { id = e.Id, type = e.Type, actor = e.Actor.ToString(), text = e.Text, at = e.At }),
            notes = notes.Select(n => new { id = n.Id, text = n.Text, author = names.GetValueOrDefault(n.AuthorId, ""), createdAt = n.CreatedAt }),
            conversationId = conv,
            stockMismatch = events.Any(e => e.Type == "stock.mismatch"),
            actions = Actions(o, a, managerRefundLimit),
            reasons = new { cancel = OrderSupport.CancelReasons, refund = OrderSupport.RefundReasons, rejectReturn = OrderSupport.RejectReasons, carriers = Tracking.Carriers }
        };
    }
}
